using System.Text;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.EvalGen.Models;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/question-generator.ts</c>. LLM-driven intent
/// generation (stage A) and question drafting with answer grounding
/// (stage B). The LLM returns mutable DTOs (<see cref="QuestionIntentDto"/>
/// / <see cref="DraftedQuestionDto"/>); this orchestrator converts those to
/// immutable <see cref="EvalToolkit.Core.QuestionIntent"/> /
/// <see cref="EvalToolkit.Core.DraftedQuestion"/> records after
/// post-processing.
/// </summary>
public static class QuestionGenerator
{
    /// <summary>
    /// Generate question intents using LLM analysis of the dataset profile
    /// and facts. Mirrors TS <c>generateIntents</c>.
    /// </summary>
    public static async Task<IReadOnlyList<QuestionIntent>> GenerateIntentsAsync(
        DatasetProfile profile,
        IReadOnlyList<Fact> facts,
        string description,
        int count,
        ILlmClient client,
        IReadOnlyList<string>? assignedRows = null,
        CancellationToken cancellationToken = default)
    {
        var sampleRecordCount = Math.Min(100, Math.Max(60, count * 2));
        var factSummary = FactExtractor.SummarizeFacts(facts, sampleRecordCount);

        var schemaSb = new StringBuilder();
        for (int i = 0; i < profile.Columns.Count; i++)
        {
            var c = profile.Columns[i];
            if (i > 0) schemaSb.Append("\n  ");
            schemaSb.Append(c.Name).Append(" (").Append(c.DataType)
                .Append(", ").Append(c.UniqueCount).Append(" unique, ")
                .Append(c.NullCount).Append(" nulls");
            if (c.ValueCounts is not null)
            {
                var cats = c.ValueCounts.Keys.Take(5);
                schemaSb.Append(", categories: ").Append(string.Join(", ", cats));
            }
            schemaSb.Append(')');
        }

        var categoryTargetsSb = new StringBuilder();
        bool first = true;
        foreach (var (cat, weight) in QuestionCategories.DefaultWeights)
        {
            if (!first) categoryTargetsSb.Append(", ");
            first = false;
            categoryTargetsSb.Append(cat.ToWireString()).Append(": ")
                .Append(Math.Max(1, (int)Math.Round(count * weight, MidpointRounding.AwayFromZero)));
        }

        string assignmentBlock = string.Empty;
        if (assignedRows is { Count: > 0 })
        {
            var slotsSb = new StringBuilder();
            for (int i = 0; i < assignedRows.Count && i < count; i++)
            {
                if (i > 0) slotsSb.Append('\n');
                slotsSb.Append("  ").Append(i + 1).Append(". ").Append(assignedRows[i]);
            }
            assignmentBlock = $@"

## Pre-assigned Primary Rows (REQUIRED)
Generate exactly {assignedRows.Count} intents. Each intent slot has a pre-assigned primary row:
{slotsSb}

For intent N, target_row_references MUST start with the assigned row for slot N.
You MAY add additional rows to target_row_references when the category is ""comparison"" or ""filtered_find"" and a second row genuinely improves the question.
DO NOT use the same primary row for two different intents.";
        }

        var prompt = $@"Analyze this dataset and generate exactly {count} question intents for evaluating a Microsoft 365 Copilot connector.

## Dataset Description
{description}

## Dataset Schema ({profile.RowCount} rows)
  {schemaSb}

## Sample Records (with [f-N] fact IDs)
{factSummary}

## Question Category Targets
{categoryTargetsSb}{assignmentBlock}

## Instructions
Generate question intents that a knowledge worker would naturally ask Copilot about this data.
Each intent should specify:
- ""intent"": what the question is about (brief description)
- ""category"": one of [single_record_lookup, attribute_retrieval, filtered_find, temporal, comparison, edge_case]
- ""difficulty"": easy, medium, or hard
- ""target_fields"": which columns/fields the question targets
- ""target_row_references"": which specific rows to reference (use format ""{profile.FileName}:row N"")

Rules:
- Questions must be answerable from the actual data shown above
- Use natural language as a knowledge worker would type into Copilot
- Avoid exact-count aggregation questions (Copilot connectors don't reliably support these)
- Reference specific entities/values from the sample data
- Spread questions across different records and fields
- Include a few edge cases (asking about values that don't exist, ambiguous queries)

Respond with JSON: {{""intents"": [...]}}";

        var result = await client.GenerateStructuredAsync<IntentsResponse>(
            prompt,
            "Respond with a JSON object containing an \"intents\" array of question intent objects.",
            cancellationToken).ConfigureAwait(false);

        var dtoList = result?.Intents ?? new List<QuestionIntentDto>();

        // Carry the pre-assigned row through onto each intent.
        if (assignedRows is { Count: > 0 })
        {
            for (int i = 0; i < dtoList.Count && i < assignedRows.Count; i++)
            {
                var assigned = assignedRows[i];
                dtoList[i].AssignedPrimaryRow = assigned;
                dtoList[i].TargetRowReferences ??= new List<string>();
                if (!dtoList[i].TargetRowReferences.Contains(assigned))
                {
                    var newRefs = new List<string> { assigned };
                    newRefs.AddRange(dtoList[i].TargetRowReferences);
                    dtoList[i].TargetRowReferences = newRefs;
                }
            }
        }

        var intents = new List<QuestionIntent>(dtoList.Count);
        foreach (var dto in dtoList)
        {
            // Parity with TS generateIntents: TS just trusts whatever the LLM
            // returns. Mirror the draft-stage fallback pattern so a single
            // malformed intent can't fail the whole pipeline.
            QuestionCategory intentCategory;
            try { intentCategory = QuestionCategories.FromWireString(dto.Category); }
            catch { intentCategory = QuestionCategory.SingleRecordLookup; }

            Difficulty intentDifficulty;
            try { intentDifficulty = Difficulties.FromWireString(dto.Difficulty); }
            catch { intentDifficulty = Difficulty.Medium; }

            intents.Add(new QuestionIntent
            {
                Intent = dto.Intent ?? string.Empty,
                Category = intentCategory,
                Difficulty = intentDifficulty,
                TargetFields = (dto.TargetFields ?? new List<string>()).ToList(),
                TargetRowReferences = (dto.TargetRowReferences ?? new List<string>()).ToList(),
                AssignedPrimaryRow = dto.AssignedPrimaryRow,
            });
        }

        return intents;
    }

    /// <summary>
    /// Draft full natural-language questions from intents, grounding each in
    /// specific facts. Mirrors TS <c>draftQuestions</c>.
    /// </summary>
    public static async Task<IReadOnlyList<DraftedQuestion>> DraftQuestionsAsync(
        IReadOnlyList<QuestionIntent> intents,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        DatasetProfile profile,
        string description,
        ILlmClient client,
        CancellationToken cancellationToken = default)
    {
        _ = records; _ = profile; // Unused here; passed through for parity with TS signature.

        var grouped = FactExtractor.GroupFactsByRecord(facts);
        var factById = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var f in facts) factById[f.Id] = f;

        // Build context: for each intent, find relevant facts.
        var intentsWithContext = new List<(QuestionIntent Intent, List<Fact> Facts)>();
        foreach (var intent in intents)
        {
            var relevantFacts = new List<Fact>();
            var seenFactIds = new HashSet<string>(StringComparer.Ordinal);

            void AddFact(Fact f)
            {
                if (seenFactIds.Add(f.Id)) relevantFacts.Add(f);
            }

            foreach (var rowRef in intent.TargetRowReferences)
            {
                if (grouped.TryGetValue(rowRef, out var rowFacts))
                {
                    foreach (var f in rowFacts) AddFact(f);
                }
            }

            if (relevantFacts.Count == 0)
            {
                foreach (var field in intent.TargetFields)
                {
                    int taken = 0;
                    foreach (var f in facts)
                    {
                        if (f.Field == field)
                        {
                            AddFact(f);
                            taken++;
                            if (taken >= 5) break;
                        }
                    }
                }
            }

            intentsWithContext.Add((intent, relevantFacts));
        }

        var contextSb = new StringBuilder();
        for (int i = 0; i < intentsWithContext.Count; i++)
        {
            if (i > 0) contextSb.Append("\n\n");
            var (intent, relevantFacts) = intentsWithContext[i];
            var assigned = intent.AssignedPrimaryRow is { } ap
                ? $"\nAssigned primary row: {ap}"
                : string.Empty;
            contextSb.Append("Intent ").Append(i + 1).Append(": ")
                .Append(intent.Intent).Append(" (")
                .Append(intent.Category.ToWireString()).Append(", ")
                .Append(intent.Difficulty.ToWireString()).Append(")\n");
            contextSb.Append("Target fields: ")
                .Append(string.Join(", ", intent.TargetFields))
                .Append(assigned)
                .Append("\nAvailable facts:\n");
            for (int j = 0; j < relevantFacts.Count; j++)
            {
                if (j > 0) contextSb.Append('\n');
                var f = relevantFacts[j];
                contextSb.Append("  [").Append(f.Id).Append("] ")
                    .Append(f.Field).Append('=').Append(FactExtractor.JsonStringify(f.Value))
                    .Append(" [").Append(f.RowReference).Append(']');
            }
        }

        var prompt = $@"Draft natural-language questions with expected answers for each intent below.

## Dataset Description
{description}

## Intents with Available Facts
{contextSb}

## Instructions
For each intent, produce:
- ""prompt"": A natural-language question as a knowledge worker would type into Copilot
- ""category"": Same category as the intent
- ""difficulty"": Same difficulty as the intent
- ""expected_answer"": The correct answer derived ONLY from the facts shown. Write a concise, natural response.
- ""supporting_facts"": Array of ""field=value"" strings that ground the answer
- ""supporting_fact_ids"": Array of fact IDs (e.g., ""f-3"", ""f-7"") from the ""Available facts"" block that the answer is grounded in. Include only facts you actually used.
- ""source_location"": The row reference where the primary answer data is found (use the assigned primary row when one was specified)

Rules:
- The expected_answer MUST be derivable from the facts provided — do not invent data
- Write questions in natural language (not SQL-like or technical)
- Expected answers should be concise but complete
- supporting_fact_ids must reference IDs that appeared in the Available facts block for this intent
- When an ""Assigned primary row"" is specified, source_location MUST equal that row

Respond with JSON: {{""questions"": [...]}}";

        var result = await client.GenerateStructuredAsync<QuestionsResponse>(
            prompt,
            "Respond with a JSON object containing a \"questions\" array of drafted question objects.",
            cancellationToken).ConfigureAwait(false);

        var dtoList = result?.Questions ?? new List<DraftedQuestionDto>();

        // Resolve referenced_rows from supporting_fact_ids (preferred) or
        // back-match supporting_facts strings against the intent's candidate
        // fact pool. Mirrors the TS post-processing loop.
        var questions = new List<DraftedQuestion>(dtoList.Count);
        for (int i = 0; i < dtoList.Count; i++)
        {
            var dto = dtoList[i];
            var ctx = i < intentsWithContext.Count ? intentsWithContext[i] : default;
            var ctxFacts = ctx.Facts ?? new List<Fact>();
            var assignedPrimaryRow = ctx.Intent?.AssignedPrimaryRow;

            var rowSet = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(dto.SourceLocation)) rowSet.Add(dto.SourceLocation);

            var cited = dto.SupportingFactIds ?? new List<string>();
            if (cited.Count > 0)
            {
                foreach (var id in cited)
                {
                    if (factById.TryGetValue(id, out var f)) rowSet.Add(f.RowReference);
                }
            }
            else
            {
                foreach (var factStr in dto.SupportingFacts ?? new List<string>())
                {
                    var eqIndex = factStr.IndexOf('=', StringComparison.Ordinal);
                    if (eqIndex < 0) continue;
                    var field = factStr.Substring(0, eqIndex).Trim();
                    var rawValue = TrimSurroundingQuotes(factStr.Substring(eqIndex + 1).Trim());
                    var matches = ctxFacts.Where(f =>
                        f.Field == field
                        && TrimSurroundingQuotes(Profiler.ConvertToString(f.Value)) == rawValue
                    ).ToList();
                    if (matches.Count == 1)
                    {
                        rowSet.Add(matches[0].RowReference);
                    }
                }
            }

            // Category/difficulty default to the intent's values if the LLM
            // didn't echo them or returned an unknown string.
            QuestionCategory category;
            try { category = QuestionCategories.FromWireString(dto.Category); }
            catch { category = ctx.Intent?.Category ?? QuestionCategory.SingleRecordLookup; }

            Difficulty difficulty;
            try { difficulty = Difficulties.FromWireString(dto.Difficulty); }
            catch { difficulty = ctx.Intent?.Difficulty ?? Difficulty.Medium; }

            questions.Add(new DraftedQuestion
            {
                Prompt = dto.Prompt ?? string.Empty,
                Category = category,
                Difficulty = difficulty,
                ReferencedFacts = ctxFacts,
                ExpectedAnswer = dto.ExpectedAnswer ?? string.Empty,
                SupportingFacts = (dto.SupportingFacts ?? new List<string>()).ToList(),
                SourceLocation = dto.SourceLocation ?? string.Empty,
                SupportingFactIds = dto.SupportingFactIds?.ToList(),
                ReferencedRows = rowSet.ToList(),
                AssignedPrimaryRow = assignedPrimaryRow,
            });
        }

        return questions;
    }

    private static string TrimSurroundingQuotes(string s)
    {
        var start = s.Length >= 1 && s[0] == '"' ? 1 : 0;
        var end = s.Length >= 1 && s[^1] == '"' ? s.Length - 1 : s.Length;
        return end > start ? s.Substring(start, end - start) : string.Empty;
    }
}
