using System.Globalization;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.EvalGen.Models;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class EvalGenPipelineTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in pairs) d[p.Key] = p.Value;
        return d;
    }

    private static IReadOnlyDictionary<string, object?>[] MakeRecords(int n = 5)
    {
        return Enumerable.Range(1, n).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(
                ("id", i.ToString(CultureInfo.InvariantCulture)),
                ("name", $"item-{i}"),
                ("status", i % 2 == 0 ? "active" : "inactive"))).ToArray();
    }

    [Fact]
    public async Task RunAsync_EmptyRecords_Throws()
    {
        var opts = new EvalGenPipeline.Options
        {
            Records = Array.Empty<IReadOnlyDictionary<string, object?>>(),
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "x",
            Count = 1,
            LlmClient = new MockLlmClient(),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvalGenPipeline.RunAsync(opts));
    }

    [Fact]
    public async Task RunAsync_EndToEnd_ProducesValidatedItems()
    {
        var records = MakeRecords(5);
        var mock = new MockLlmClient();

        mock.SetResponse("Analyze this dataset", new IntentsResponse
        {
            Intents = new List<QuestionIntentDto>
            {
                new() {
                    Intent = "find item 1",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    TargetFields = new List<string> { "name" },
                    TargetRowReferences = new List<string> { "f.csv:row 1" },
                },
            },
        });

        mock.SetResponse("Draft natural-language questions", new QuestionsResponse
        {
            Questions = new List<DraftedQuestionDto>
            {
                new() {
                    Prompt = "What is the name of item 1?",
                    Category = "single_record_lookup",
                    Difficulty = "easy",
                    ExpectedAnswer = "item-1",
                    SupportingFacts = new List<string> { "name=item-1" },
                    SourceLocation = "f.csv:row 1",
                },
            },
        });

        var result = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "items",
            Count = 1,
            LlmClient = mock,
        });

        Assert.NotNull(result.Profile);
        Assert.NotEmpty(result.Facts);
        Assert.Single(result.Intents);
        Assert.Single(result.Drafted);
        Assert.Single(result.Grounded);
        Assert.NotEmpty(result.Validated);
        Assert.Equal("item-1", result.Validated[0].ExpectedAnswer);
        Assert.Equal("What is the name of item 1?", result.Validated[0].Prompt);
    }

    [Fact]
    public async Task RunAsync_AssignedRows_ScaleWithCount()
    {
        var records = MakeRecords(10);
        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse { Intents = new List<QuestionIntentDto>() });
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse { Questions = new List<DraftedQuestionDto>() });

        var result = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "d",
            Count = 3,
            LlmClient = mock,
        });

        Assert.Equal(3, result.AssignedRows.Count);
    }

    [Fact]
    public async Task RunAsync_NoAvoidance_AvoidanceResultIsNull()
    {
        var records = MakeRecords(3);
        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse { Intents = new List<QuestionIntentDto>() });
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse { Questions = new List<DraftedQuestionDto>() });

        var result = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "d",
            Count = 1,
            LlmClient = mock,
        });

        Assert.Null(result.AvoidanceResult);
    }

    [Fact]
    public async Task RunAsync_ProgressCallback_ReportsAllPhases()
    {
        var records = MakeRecords(3);
        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse { Intents = new List<QuestionIntentDto>() });
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse { Questions = new List<DraftedQuestionDto>() });

        var phases = new List<string>();
        var progress = new Progress<string>(p => phases.Add(p));

        await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "d",
            Count = 1,
            LlmClient = mock,
            Progress = progress,
        });

        // Progress<T> marshals through the current SynchronizationContext, which
        // in xUnit is null — callbacks run synchronously inline. Spin briefly to
        // tolerate the rare case where a sync context IS installed and posts
        // are queued.
        var spin = 0;
        while (phases.Count < 7 && spin++ < 50)
        {
            await Task.Delay(10);
        }

        Assert.Contains("Profiling dataset", phases);
        Assert.Contains("Extracting facts", phases);
        Assert.Contains("Generating intents", phases);
        Assert.Contains("Drafting questions", phases);
        Assert.Contains("Grounding answers", phases);
        Assert.Contains("Generating assertions", phases);
        Assert.Contains("Validating", phases);
    }

    [Fact]
    public async Task RunAsync_NoProgressCallback_StillSucceeds()
    {
        // Regression guard: the new IProgress<string>? Progress field is optional.
        var records = MakeRecords(3);
        var mock = new MockLlmClient();
        mock.SetResponse("Analyze this dataset", new IntentsResponse { Intents = new List<QuestionIntentDto>() });
        mock.SetResponse("Draft natural-language questions", new QuestionsResponse { Questions = new List<DraftedQuestionDto>() });

        var result = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = "f.csv",
            Format = InputFormat.Csv,
            Description = "d",
            Count = 1,
            LlmClient = mock,
            // Progress intentionally not set.
        });

        Assert.NotNull(result.Validation);
    }
}
