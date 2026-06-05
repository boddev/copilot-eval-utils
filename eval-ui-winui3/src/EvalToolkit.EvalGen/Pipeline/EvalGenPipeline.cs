using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Orchestrates the in-memory eval-generation pipeline. Mirrors the post-CLI
/// portion of <c>eval-gen/src/index.ts</c>: profile → extract facts →
/// generate intents → draft questions → ground answers → generate assertions
/// → validate → optionally filter against avoidance set.
///
/// File reading, CLI arg parsing, and on-disk writing remain with their
/// owning slices (readers, cli-shims, writers); this class operates entirely
/// on in-memory records and returns an in-memory result.
/// </summary>
public static class EvalGenPipeline
{
    /// <summary>Inputs for <see cref="RunAsync"/>.</summary>
    public sealed record Options
    {
        /// <summary>Records already loaded from the source file/folder/connector.</summary>
        public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Records { get; init; }

        /// <summary>Display name for the source (file basename, etc.).</summary>
        public required string SourceName { get; init; }

        /// <summary>Input format for profiling.</summary>
        public required InputFormat Format { get; init; }

        /// <summary>Free-form description of the dataset for the LLM prompt.</summary>
        public required string Description { get; init; }

        /// <summary>Requested question count (clamped by caller; pipeline honors as-is).</summary>
        public required int Count { get; init; }

        /// <summary>LLM client used for intent generation and question drafting.</summary>
        public required ILlmClient LlmClient { get; init; }

        /// <summary>Optional avoidance set loaded from prior <c>.evalgen.json</c> sidecars.</summary>
        public Dedupe.AvoidanceSet? Avoidance { get; init; }

        /// <summary>Optional cancellation propagated to LLM calls.</summary>
        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>Output of <see cref="RunAsync"/>.</summary>
    public sealed record Result
    {
        public required DatasetProfile Profile { get; init; }
        public required IReadOnlyList<Fact> Facts { get; init; }
        public required IReadOnlyList<QuestionIntent> Intents { get; init; }
        public required IReadOnlyList<DraftedQuestion> Drafted { get; init; }
        public required IReadOnlyList<DraftedQuestion> Grounded { get; init; }
        public required IReadOnlyList<GeneratedEvalItem> Validated { get; init; }
        public required ValidationResult Validation { get; init; }
        public required IReadOnlyList<string> AssignedRows { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }

        /// <summary>Avoidance-filter result (null when no avoidance was provided).</summary>
        public Dedupe.AvoidanceFilterResult? AvoidanceResult { get; init; }
    }

    /// <summary>
    /// Run the full in-memory pipeline against the supplied records. Throws
    /// <see cref="InvalidOperationException"/> for an empty dataset.
    /// </summary>
    public static async Task<Result> RunAsync(Options options)
    {
        if (options.Records.Count == 0)
        {
            throw new InvalidOperationException("Cannot generate eval set from empty records");
        }

        var ct = options.CancellationToken;

        // 1. Profile dataset
        var profile = Profiler.ProfileDataset(options.Records, options.SourceName, options.Format);

        // 2. Extract facts — row pool scales with requested count.
        var targetRecords = Math.Min(options.Records.Count, Math.Max(100, options.Count * 4));
        var factBudget = Math.Max(200, targetRecords * 8);
        var facts = FactExtractor.ExtractFacts(options.Records, profile, new FactExtractor.ExtractFactsOptions
        {
            MaxFacts = factBudget,
            TargetRecords = targetRecords,
        });

        // 3. Pre-assign one distinct row per intent slot.
        var distinctRowPool = new List<string>();
        var seenRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in facts)
        {
            if (seenRows.Add(f.RowReference)) distinctRowPool.Add(f.RowReference);
        }
        var assignedRows = new List<string>(options.Count);
        for (int i = 0; i < options.Count; i++)
        {
            var pool = Math.Max(1, distinctRowPool.Count);
            if (distinctRowPool.Count == 0)
            {
                assignedRows.Add(string.Empty);
            }
            else
            {
                assignedRows.Add(distinctRowPool[i % pool]);
            }
        }

        // 4. LLM intent generation.
        var intents = await QuestionGenerator.GenerateIntentsAsync(
            profile, facts, options.Description, options.Count, options.LlmClient,
            assignedRows, ct).ConfigureAwait(false);

        // 5. LLM question drafting.
        var drafted = await QuestionGenerator.DraftQuestionsAsync(
            intents, facts, options.Records, profile, options.Description, options.LlmClient,
            ct).ConfigureAwait(false);

        // 6. Ground answers + generate assertions.
        var grounded = AnswerGrounder.GroundAllAnswers(drafted, options.Records, options.SourceName);
        var assertionMap = AssertionGenerator.GenerateAllAssertions(grounded);

        // 7. Build eval items.
        var evalItems = Validator.BuildEvalItems(grounded, assertionMap);

        // 8. Avoidance filter (optional).
        Dedupe.AvoidanceFilterResult? avoidanceResult = null;
        var allWarnings = new List<string>();
        if (options.Avoidance is not null)
        {
            avoidanceResult = Dedupe.FilterAgainstAvoidance(evalItems, options.Avoidance, options.SourceName);
            evalItems = avoidanceResult.Items;
            allWarnings.AddRange(avoidanceResult.Warnings);
        }

        // 9. Validate.
        var (validated, validation) = Validator.ValidateEvalSet(evalItems, options.Records.Count);

        return new Result
        {
            Profile = profile,
            Facts = facts,
            Intents = intents,
            Drafted = drafted,
            Grounded = grounded,
            Validated = validated,
            Validation = validation,
            AssignedRows = assignedRows,
            Warnings = allWarnings,
            AvoidanceResult = avoidanceResult,
        };
    }
}
