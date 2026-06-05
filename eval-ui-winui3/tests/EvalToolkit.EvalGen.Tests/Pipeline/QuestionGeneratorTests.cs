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
}
