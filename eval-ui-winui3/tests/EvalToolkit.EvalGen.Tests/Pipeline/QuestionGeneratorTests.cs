using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.EvalGen.Models;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class QuestionGeneratorTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in pairs) d[p.Key] = p.Value;
        return d;
    }

    private static IReadOnlyDictionary<string, object?>[] MakeRecords()
    {
        return new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("id", "1"), ("name", "Alice"), ("city", "NYC")),
            (IReadOnlyDictionary<string, object?>)Row(("id", "2"), ("name", "Bob"), ("city", "LA")),
            (IReadOnlyDictionary<string, object?>)Row(("id", "3"), ("name", "Carol"), ("city", "SF")),
        };
    }

    [Fact]
    public async Task GenerateIntentsAsync_DeserializesAndConvertsToCoreRecords()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse
        {
            Intents = new List<QuestionIntentDto>
            {
                new() {
                    Intent = "find Alice",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    TargetFields = new List<string> { "name", "city" },
                    TargetRowReferences = new List<string> { "people.csv:row 1" },
                },
            },
        });

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 1, mock);
        Assert.Single(intents);
        Assert.Equal("find Alice", intents[0].Intent);
        Assert.Equal(QuestionCategory.SingleRecordLookup, intents[0].Category);
        Assert.Equal(Difficulty.Easy, intents[0].Difficulty);
        Assert.Contains("name", intents[0].TargetFields);
    }

    [Fact]
    public async Task GenerateIntentsAsync_PrefersAssignedRow()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse
        {
            Intents = new List<QuestionIntentDto>
            {
                new() {
                    Intent = "x",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    TargetFields = new List<string> { "name" },
                    TargetRowReferences = new List<string> { "people.csv:row 3" },
                },
            },
        });
        var assigned = new[] { "people.csv:row 1" };

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 1, mock, assigned);
        Assert.Equal("people.csv:row 1", intents[0].AssignedPrimaryRow);
        Assert.Equal("people.csv:row 1", intents[0].TargetRowReferences[0]);
    }

    [Fact]
    public async Task GenerateIntentsAsync_NullResponse_ReturnsEmpty()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var mock = new MockLlmClient(defaultResponse: new IntentsResponse { Intents = null });
        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "d", 1, mock);
        Assert.Empty(intents);
    }

    [Fact]
    public async Task DraftQuestionsAsync_BuildsDraftedQuestionsFromDtos()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var intents = new[]
        {
            new QuestionIntent
            {
                Intent = "find Alice",
                Category = QuestionCategory.SingleRecordLookup,
                Difficulty = Difficulty.Easy,
                TargetFields = new[] { "name" },
                TargetRowReferences = new[] { "people.csv:row 1" },
                AssignedPrimaryRow = "people.csv:row 1",
            },
        };

        var mock = new MockLlmClient();
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse
        {
            Questions = new List<DraftedQuestionDto>
            {
                new() {
                    Prompt = "Where does Alice live?",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    ExpectedAnswer = "NYC",
                    SupportingFacts = new List<string> { "name=Alice", "city=NYC" },
                    SourceLocation = "people.csv:row 1",
                    SupportingFactIds = facts.Count > 0 ? new List<string> { facts[0].Id } : new List<string>(),
                },
            },
        });

        var drafted = await QuestionGenerator.DraftQuestionsAsync(intents, facts, records, profile, "people", mock);
        Assert.Single(drafted);
        Assert.Equal("Where does Alice live?", drafted[0].Prompt);
        Assert.Equal("NYC", drafted[0].ExpectedAnswer);
        Assert.Equal(QuestionCategory.SingleRecordLookup, drafted[0].Category);
        Assert.Equal("people.csv:row 1", drafted[0].AssignedPrimaryRow);
    }

    [Fact]
    public async Task DraftQuestionsAsync_UnknownCategory_FallsBackToIntent()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var intents = new[]
        {
            new QuestionIntent
            {
                Intent = "x",
                Category = QuestionCategory.Comparison,
                Difficulty = Difficulty.Hard,
                TargetFields = new[] { "name" },
                TargetRowReferences = new[] { "people.csv:row 1" },
            },
        };

        var mock = new MockLlmClient();
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse
        {
            Questions = new List<DraftedQuestionDto>
            {
                new() {
                    Prompt = "q",
                    Category = "invalid_category",
                    Difficulty = "unknown",
                    ExpectedAnswer = "a",
                    SupportingFacts = new List<string> { "name=Alice" },
                    SourceLocation = "people.csv:row 1",
                },
            },
        });

        var drafted = await QuestionGenerator.DraftQuestionsAsync(intents, facts, records, profile, "p", mock);
        Assert.Equal(QuestionCategory.Comparison, drafted[0].Category);
        Assert.Equal(Difficulty.Hard, drafted[0].Difficulty);
    }

    [Fact]
    public async Task DraftQuestionsAsync_ResolvesReferencedRowsFromFactIds()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        Assert.NotEmpty(facts);
        var firstFactRow = facts[0].RowReference;

        var intents = new[]
        {
            new QuestionIntent
            {
                Intent = "x",
                Category = QuestionCategory.SingleRecordLookup,
                Difficulty = Difficulty.Easy,
                TargetFields = new[] { "name" },
                TargetRowReferences = new[] { firstFactRow },
            },
        };

        var mock = new MockLlmClient();
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse
        {
            Questions = new List<DraftedQuestionDto>
            {
                new() {
                    Prompt = "q",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    ExpectedAnswer = "a",
                    SupportingFacts = new List<string> { facts[0].Field + "=" + facts[0].Value },
                    SourceLocation = firstFactRow,
                    SupportingFactIds = new List<string> { facts[0].Id },
                },
            },
        });

        var drafted = await QuestionGenerator.DraftQuestionsAsync(intents, facts, records, profile, "p", mock);
        Assert.NotNull(drafted[0].ReferencedRows);
        Assert.Contains(firstFactRow, drafted[0].ReferencedRows!);
    }

    [Fact]
    public async Task GenerateIntentsAsync_UnknownCategory_FallsBackToSingleRecordLookup()
    {
        // Round-2 fix: intent-stage FromWireString used to throw on unknown values,
        // aborting the whole pipeline. Now mirrors draft-stage try/catch fallback.
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse
        {
            Intents = new List<QuestionIntentDto>
            {
                new() {
                    Intent = "x",
                    Category = "not_a_real_category",
                    Difficulty = "also_bogus",
                    TargetFields = new List<string> { "name" },
                    TargetRowReferences = new List<string>(),
                },
            },
        });

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 1, mock);
        Assert.Single(intents);
        Assert.Equal(QuestionCategory.SingleRecordLookup, intents[0].Category);
        Assert.Equal(Difficulty.Medium, intents[0].Difficulty);
    }

    [Fact]
    public async Task DraftQuestionsAsync_SplitsIntoBatches_WhenClientHasPromptLimit()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var intents = new List<QuestionIntent>();
        for (int i = 0; i < 6; i++)
        {
            intents.Add(new QuestionIntent
            {
                Intent = $"intent number {i}",
                Category = QuestionCategory.SingleRecordLookup,
                Difficulty = Difficulty.Easy,
                TargetFields = new[] { "name" },
                TargetRowReferences = new[] { "people.csv:row 1" },
            });
        }

        // Tiny budget forces multiple batches; the client echoes one question
        // per "Intent " block in the prompt it actually received.
        var client = new BatchCountingClient(maxPromptChars: 600);

        var drafted = await QuestionGenerator.DraftQuestionsAsync(intents, facts, records, profile, "people", client);

        Assert.True(client.CallCount > 1, $"expected multiple batches, got {client.CallCount}");
        Assert.Equal(6, drafted.Count);
    }

    [Fact]
    public async Task DraftQuestionsAsync_SingleCall_WhenNoPromptLimit()
    {
        var records = MakeRecords();
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);

        var intents = new List<QuestionIntent>();
        for (int i = 0; i < 6; i++)
        {
            intents.Add(new QuestionIntent
            {
                Intent = $"intent number {i}",
                Category = QuestionCategory.SingleRecordLookup,
                Difficulty = Difficulty.Easy,
                TargetFields = new[] { "name" },
                TargetRowReferences = new[] { "people.csv:row 1" },
            });
        }

        // No IPromptSizeLimited: original single-call behavior is preserved.
        var client = new CountingDraftClient();

        var drafted = await QuestionGenerator.DraftQuestionsAsync(intents, facts, records, profile, "people", client);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(6, drafted.Count);
    }

    /// <summary>
    /// Test client that returns one drafted question per "Intent " block found
    /// in the prompt and counts how many times it was called. Does not advertise
    /// a prompt-size limit, so the pipeline keeps single-call behavior.
    /// </summary>
    private class CountingDraftClient : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
        {
            CallCount++;
            int count = CountOccurrences(prompt, "Intent ");
            var dtos = new List<DraftedQuestionDto>();
            for (int i = 0; i < count; i++)
            {
                dtos.Add(new DraftedQuestionDto
                {
                    Prompt = $"q{i}",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    ExpectedAnswer = "a",
                });
            }
            return Task.FromResult((T)(object)new QuestionsResponse { Questions = dtos });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }

    /// <summary>
    /// <see cref="CountingDraftClient"/> that also advertises a prompt-size
    /// limit, triggering pipeline batching when the prompt would exceed it.
    /// </summary>
    private sealed class BatchCountingClient : CountingDraftClient, IPromptSizeLimited
    {
        public BatchCountingClient(int maxPromptChars) => MaxPromptChars = maxPromptChars;

        public int MaxPromptChars { get; }
    }

    private static IReadOnlyDictionary<string, object?>[] MakeWideRecords(int rowCount)
    {
        var bio = new string('x', 400);
        var rows = new IReadOnlyDictionary<string, object?>[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = Row(("id", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("name", $"Person{i + 1}"), ("bio", $"{bio}-{i}"));
        }
        return rows;
    }

    private static List<string> AssignDistinctRows(IReadOnlyList<Fact> facts, int count)
    {
        var distinct = facts.Select(f => f.RowReference).Distinct().ToList();
        var assigned = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            assigned.Add(distinct[i % distinct.Count]);
        }
        return assigned;
    }

    [Fact]
    public async Task GenerateIntentsAsync_SplitsIntoBatches_WhenPromptExceedsLimit()
    {
        var records = MakeWideRecords(8);
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var assigned = AssignDistinctRows(facts, 8);

        // Wide rows + small budget force the intents prompt over the limit, so
        // the pipeline must split into multiple row-aligned batches.
        var client = new LimitedIntentRecordingClient(maxPromptChars: 3000);

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 8, client, assigned);

        Assert.True(client.CallCount > 1, $"expected multiple batches, got {client.CallCount}");
        Assert.Equal(8, intents.Count);

        // Critique invariant: every assigned row in a batch must appear in that
        // batch's Sample Records block, and no multi-slot batch may exceed budget.
        foreach (var prompt in client.Prompts)
        {
            var assignedInPrompt = ExtractAssignedRows(prompt);
            var sampleRecords = ExtractSampleRecordsSection(prompt);
            foreach (var row in assignedInPrompt)
            {
                Assert.Contains("[" + row + "]", sampleRecords);
            }

            if (assignedInPrompt.Count > 1)
            {
                Assert.True(prompt.Length <= 3000, $"multi-slot batch prompt was {prompt.Length} chars");
            }
        }
    }

    [Fact]
    public async Task GenerateIntentsAsync_SingleCall_WhenNoPromptLimit()
    {
        var records = MakeWideRecords(8);
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var assigned = AssignDistinctRows(facts, 8);

        // No IPromptSizeLimited: the whole job stays a single intents call.
        var client = new IntentRecordingClient();

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 8, client, assigned);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(8, intents.Count);
    }

    [Fact]
    public async Task GenerateIntentsAsync_ClampsOverReturnPerBatch()
    {
        var records = MakeWideRecords(8);
        var profile = Profiler.ProfileDataset(records, "people.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var assigned = AssignDistinctRows(facts, 8);

        // Return twice as many intents per batch as requested; the pipeline must
        // clamp each batch to its slot count so totals stay correct.
        var client = new LimitedIntentRecordingClient(maxPromptChars: 3000, overReturnFactor: 2);

        var intents = await QuestionGenerator.GenerateIntentsAsync(profile, facts, "people", 8, client, assigned);

        Assert.True(client.CallCount > 1);
        Assert.Equal(8, intents.Count);
    }

    private static List<string> ExtractAssignedRows(string prompt)
    {
        var rows = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(prompt, @"(?m)^  \d+\. (.+)$"))
        {
            rows.Add(m.Groups[1].Value.Trim());
        }
        return rows;
    }

    private static string ExtractSampleRecordsSection(string prompt)
    {
        const string start = "## Sample Records";
        const string end = "## Question Category Targets";
        int s = prompt.IndexOf(start, StringComparison.Ordinal);
        int e = prompt.IndexOf(end, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return string.Empty;
        return prompt.Substring(s, e - s);
    }

    /// <summary>
    /// Records every intents prompt it receives and returns one intent per slot
    /// requested by that prompt ("generate exactly N question intents"). Does not
    /// advertise a prompt-size limit, so the pipeline keeps single-call behavior.
    /// </summary>
    private class IntentRecordingClient : ILlmClient
    {
        private readonly int _overReturnFactor;

        public IntentRecordingClient(int overReturnFactor = 1) => _overReturnFactor = overReturnFactor;

        public List<string> Prompts { get; } = new();

        public int CallCount => Prompts.Count;

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            int requested = ExtractRequestedCount(prompt) * _overReturnFactor;
            var dtos = new List<QuestionIntentDto>();
            for (int i = 0; i < requested; i++)
            {
                dtos.Add(new QuestionIntentDto
                {
                    Intent = $"i{i}",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    TargetFields = new List<string>(),
                    TargetRowReferences = new List<string>(),
                });
            }
            return Task.FromResult((T)(object)new IntentsResponse { Intents = dtos });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static int ExtractRequestedCount(string prompt)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                prompt, @"generate exactly (\d+) question intents");
            return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    /// <summary>
    /// <see cref="IntentRecordingClient"/> that advertises a prompt-size limit,
    /// triggering intents-stage batching when the prompt would exceed it.
    /// </summary>
    private sealed class LimitedIntentRecordingClient : IntentRecordingClient, IPromptSizeLimited
    {
        public LimitedIntentRecordingClient(int maxPromptChars, int overReturnFactor = 1)
            : base(overReturnFactor) => MaxPromptChars = maxPromptChars;

        public int MaxPromptChars { get; }
    }
}
