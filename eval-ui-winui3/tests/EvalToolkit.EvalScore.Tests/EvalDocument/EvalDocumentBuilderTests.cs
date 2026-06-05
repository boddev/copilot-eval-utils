using System.Text.Json;
using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Tests.EvalDocument;

public class EvalDocumentBuilderTests
{
    private static EvalRow Row(
        string prompt = "q",
        string expected = "a",
        string actual = "answer",
        double? similarity = null,
        int? itemIndex = null,
        int? turnIndex = null,
        string? threadId = null,
        string? id = null,
        IList<MetricResult>? metrics = null,
        EvalError? error = null,
        EvalStatus? status = null,
        IReadOnlyList<Citation>? citations = null,
        string? context = null,
        string sourceLocation = "")
    {
        return new EvalRow
        {
            Prompt = prompt,
            ExpectedAnswer = expected,
            SourceLocation = sourceLocation,
            ActualAnswer = actual,
            SimilarityScore = similarity,
            ItemIndex = itemIndex,
            TurnIndex = turnIndex,
            ThreadId = threadId,
            Id = id,
            Metrics = metrics,
            Error = error,
            Status = status,
            Citations = citations,
            Context = context,
        };
    }

    private static MetricResult M(
        EvaluatorName name,
        bool? passed = null,
        double? score = null,
        string? reason = null,
        string? model = null,
        double? threshold = null,
        MetricProvider provider = MetricProvider.WorkIq,
        string? rubricVersion = null,
        MetricScale scale = MetricScale.ZeroToOneHundred)
        => new()
        {
            Name = name,
            Provider = provider,
            Scale = scale,
            Passed = passed,
            Score = score,
            Reason = reason,
            Model = model,
            Threshold = threshold,
            RubricVersion = rubricVersion,
        };

    private static readonly JsonSerializerOptions s_compactJson = new() { WriteIndented = false };
    private static string Serialize(Models.EvalDocument doc)
        => JsonSerializer.Serialize(doc, s_compactJson);

    // ---------- shell / metadata ----------

    [Fact]
    public void RowsToEvalDocument_HasLockedSchemaVersion()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row() });
        Assert.Equal("1.4.0", doc.SchemaVersion);
    }

    [Fact]
    public void Metadata_IncludesEvaluatedAtAndCliVersion()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row() });
        Assert.NotNull(doc.Metadata);
        Assert.True(doc.Metadata!.ContainsKey("evaluatedAt"));
        Assert.Equal("eval-score", doc.Metadata["cliVersion"]);
        var evaluatedAt = (string)doc.Metadata["evaluatedAt"]!;
        Assert.EndsWith("Z", evaluatedAt);
    }

    [Fact]
    public void Metadata_Extensions_PreservesSiblingKeys()
    {
        // Reviewer R3: inbound metadata.extensions sibling keys must
        // survive the evalscore overlay.
        var inbound = new Dictionary<string, object?>
        {
            ["extensions"] = new Dictionary<string, object?>
            {
                ["mychannel"] = new Dictionary<string, object?> { ["x"] = 1 },
            },
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(
            new[] { Row() },
            new EvalDocumentBuilder.BuildOptions { Metadata = inbound });
        var extensions = (IDictionary<string, object?>)doc.Metadata!["extensions"]!;
        Assert.True(extensions.ContainsKey("mychannel"));
        Assert.True(extensions.ContainsKey("evalscore"));
    }

    [Fact]
    public void Metadata_EvalScoreBlock_OmitsNullsButKeepsCanonicalScoreScale()
    {
        // Reviewer B1: optional fields must not appear as JSON null.
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row() });
        var extensions = (IDictionary<string, object?>)doc.Metadata!["extensions"]!;
        var evalscore = (IDictionary<string, object?>)extensions["evalscore"]!;
        Assert.Equal("0-100", evalscore["canonicalScoreScale"]);
        Assert.False(evalscore.ContainsKey("inputFile"));
        Assert.False(evalscore.ContainsKey("target"));
        Assert.False(evalscore.ContainsKey("judgeProvider"));
        Assert.False(evalscore.ContainsKey("evaluators"));
    }

    [Fact]
    public void Metadata_EvalScoreBlock_PopulatedFromOptions()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(
            new[] { Row() },
            new EvalDocumentBuilder.BuildOptions
            {
                InputFile = "in.csv",
                Target = new EvaluationTarget { Type = TargetType.WorkIq, AgentId = "agent-1" },
                JudgeProvider = JudgeProvider.WorkIq,
                RunEvaluators = new[] { EvaluatorName.Relevance, EvaluatorName.Coherence },
            });
        var extensions = (IDictionary<string, object?>)doc.Metadata!["extensions"]!;
        var evalscore = (IDictionary<string, object?>)extensions["evalscore"]!;
        Assert.Equal("in.csv", evalscore["inputFile"]);
        Assert.Equal("workiq", evalscore["judgeProvider"]);
        var target = (IDictionary<string, object?>)evalscore["target"]!;
        Assert.Equal("workiq", target["type"]);
        Assert.Equal("agent-1", target["agentId"]);
        var evaluators = (List<object>)evalscore["evaluators"]!;
        Assert.Equal(new object[] { "Relevance", "Coherence" }, evaluators);
    }

    // ---------- single-turn shape ----------

    [Fact]
    public void SingleRow_ProducesSingleTurnItem_NoTurnsKey()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(actual: "answer", similarity: 80) });
        Assert.Single(doc.Items);
        var item = doc.Items[0];
        Assert.False(item.ContainsKey("turns"), "single-turn item should not carry a turns array");
        Assert.Equal("q", item["prompt"]);
        Assert.Equal("pass", item["status"]);
    }

    [Fact]
    public void EmptyExpectedAnswer_OmitsExpectedResponse()
    {
        // Reviewer B2: empty strings on TS-`||`-guarded fields must be omitted.
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(expected: "") });
        var item = doc.Items[0];
        Assert.False(item.ContainsKey("expected_response"));
    }

    [Fact]
    public void EmptyActualAnswer_OmitsResponse()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(actual: "") });
        var item = doc.Items[0];
        Assert.False(item.ContainsKey("response"));
    }

    [Fact]
    public void EmptySourceLocation_OmitsContextAndExtensionField()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(sourceLocation: "") });
        var item = doc.Items[0];
        Assert.False(item.ContainsKey("context"));
        var ext = (IDictionary<string, object?>)((IDictionary<string, object?>)item["extensions"]!)["evalscore"]!;
        Assert.False(ext.ContainsKey("source_location"));
    }

    [Fact]
    public void ContextFallsBackToSourceLocation()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(sourceLocation: "sp:/doc.md") });
        var item = doc.Items[0];
        Assert.Equal("sp:/doc.md", item["context"]);
    }

    [Fact]
    public void ExplicitContext_BeatsSourceLocation()
    {
        var doc = EvalDocumentBuilder.RowsToEvalDocument(
            new[] { Row(context: "real-ctx", sourceLocation: "fallback") });
        var item = doc.Items[0];
        Assert.Equal("real-ctx", item["context"]);
    }

    // ---------- grouping / threads ----------

    [Fact]
    public void TurnIndexedRows_GroupIntoThread_SortedByTurnIndex()
    {
        var rows = new[]
        {
            Row(prompt: "q1", turnIndex: 1, threadId: "t1"),
            Row(prompt: "q0", turnIndex: 0, threadId: "t1"),
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(rows);
        Assert.Single(doc.Items);
        var turns = (List<Dictionary<string, object?>>)doc.Items[0]["turns"]!;
        Assert.Equal(2, turns.Count);
        Assert.Equal("q0", turns[0]["prompt"]);
        Assert.Equal("q1", turns[1]["prompt"]);
    }

    [Fact]
    public void ThreadSummary_AggregatesStatuses()
    {
        var rows = new[]
        {
            Row(prompt: "q0", turnIndex: 0, threadId: "t1", similarity: 90),
            Row(prompt: "q1", turnIndex: 1, threadId: "t1", similarity: 10),
            Row(prompt: "q2", turnIndex: 2, threadId: "t1", actual: "[ERROR: x]"),
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(rows);
        var summary = (IDictionary<string, object?>)doc.Items[0]["summary"]!;
        Assert.Equal(3, summary["turns_total"]);
        Assert.Equal(1, summary["turns_passed"]);
        Assert.Equal(1, summary["turns_failed"]);
        Assert.Equal(1, summary["turns_errored"]);
        Assert.Equal(0, summary["turns_partial"]);
        Assert.Equal("error", summary["overall_status"]);
    }

    [Fact]
    public void MixedSingleAndThreadRows_PreservesGroupingOrder()
    {
        var rows = new[]
        {
            Row(prompt: "single-a"),
            Row(prompt: "t-0", turnIndex: 0, threadId: "t1"),
            Row(prompt: "t-1", turnIndex: 1, threadId: "t1"),
            Row(prompt: "single-b"),
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(rows);
        Assert.Equal(3, doc.Items.Count);
        Assert.Equal("single-a", doc.Items[0]["prompt"]);
        Assert.True(doc.Items[1].ContainsKey("turns"));
        Assert.Equal("single-b", doc.Items[2]["prompt"]);
    }

    // ---------- score mapping ----------

    [Fact]
    public void Scores_SimilarityMetric_UsesFivePointScaleAndProvider()
    {
        var row = Row(metrics: new[] { M(EvaluatorName.Similarity, passed: true, score: 80, model: "gpt-4", rubricVersion: "v1") });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var item = doc.Items[0];
        var scores = (IDictionary<string, object?>)item["scores"]!;
        var sim = (IDictionary<string, object?>)scores["similarity"]!;
        Assert.Equal(4.0, (double)sim["score"]!);
        Assert.Equal("pass", sim["result"]);
        Assert.Equal(80.0, (double)sim["score_0_100"]!);
        Assert.Equal("workiq", sim["provider"]);
        Assert.Equal("gpt-4", sim["model"]);
        Assert.Equal("v1", sim["rubricVersion"]);
    }

    [Fact]
    public void Scores_ExactMatch_UsesMatchShape()
    {
        var row = Row(metrics: new[]
        {
            M(EvaluatorName.ExactMatch, passed: true, score: 100, provider: MetricProvider.Deterministic, scale: MetricScale.Boolean),
        });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var em = (IDictionary<string, object?>)
            ((IDictionary<string, object?>)doc.Items[0]["scores"]!)["exactMatch"]!;
        Assert.Equal(true, em["match"]);
        Assert.Equal("pass", em["result"]);
        Assert.Equal(100.0, (double)em["score_0_100"]!);
        Assert.False(em.ContainsKey("score"));
    }

    [Fact]
    public void Scores_PartialMatch_NormalizesScoreToZeroToOne()
    {
        var row = Row(metrics: new[]
        {
            M(EvaluatorName.PartialMatch, passed: false, score: 40, provider: MetricProvider.Deterministic),
        });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var pm = (IDictionary<string, object?>)
            ((IDictionary<string, object?>)doc.Items[0]["scores"]!)["partialMatch"]!;
        Assert.Equal(0.4, (double)pm["score"]!, precision: 6);
        Assert.Equal(0.7, (double)pm["threshold"]!, precision: 6);
        Assert.Equal("fail", pm["result"]);
        Assert.Equal(40.0, (double)pm["score_0_100"]!);
    }

    [Fact]
    public void Scores_Citations_UsesCountShape()
    {
        var row = Row(metrics: new[]
        {
            M(EvaluatorName.Citations, passed: true, score: 100, provider: MetricProvider.Deterministic, scale: MetricScale.Boolean),
        });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var c = (IDictionary<string, object?>)
            ((IDictionary<string, object?>)doc.Items[0]["scores"]!)["citations"]!;
        Assert.Equal(1, c["count"]);
        Assert.Equal(1, c["threshold"]);
        Assert.Equal("pass", c["result"]);
    }

    [Fact]
    public void Scores_NullReasonAndModel_AreOmitted()
    {
        // Reviewer B1: nulls must not serialize as null.
        var row = Row(metrics: new[] { M(EvaluatorName.Similarity, passed: true, score: 80) });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var sim = (IDictionary<string, object?>)
            ((IDictionary<string, object?>)doc.Items[0]["scores"]!)["similarity"]!;
        Assert.False(sim.ContainsKey("reason"));
        Assert.False(sim.ContainsKey("model"));
        Assert.False(sim.ContainsKey("rubricVersion"));
    }

    // ---------- citations ----------

    [Fact]
    public void Citations_NormalizedWithOneBasedIndex()
    {
        var row = Row(citations: new[]
        {
            new Citation { Title = "Doc A", Url = "https://a/" },
            new Citation { Title = "Doc B", SourceLocation = "sp:/b.md" },
        });
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        var cits = (List<IDictionary<string, object?>>)doc.Items[0]["citations"]!;
        Assert.Equal(2, cits.Count);
        Assert.Equal(1, cits[0]["index"]);
        Assert.Equal("Doc A", cits[0]["text"]);
        Assert.Equal("https://a/", cits[0]["source"]);
        Assert.Equal(2, cits[1]["index"]);
        Assert.Equal("sp:/b.md", cits[1]["source"]);
    }

    // ---------- JSON wire shape ----------

    [Fact]
    public void Serialized_OptionalFields_NotEmittedAsNull()
    {
        // Reviewer B1: round-trip a minimal doc and assert the raw JSON
        // contains no "null" tokens for omitted optional fields.
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { Row(similarity: 80) });
        string json = Serialize(doc);
        Assert.DoesNotContain("null", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialized_EnumValuesAreWireStrings()
    {
        // Reviewer B3: enum-to-wire conversion must happen at build time,
        // not via STJ's default enum-name serialization.
        var doc = EvalDocumentBuilder.RowsToEvalDocument(
            new[] { Row(metrics: new[] { M(EvaluatorName.Similarity, true, 80) }) });
        string json = Serialize(doc);
        Assert.Contains("\"status\":\"pass\"", json);
        Assert.Contains("\"provider\":\"workiq\"", json);
    }

    [Fact]
    public void Serialized_EvaluatorMapKeysUseWireNames()
    {
        // Reviewer B3: EvaluatorMap dictionary keys must serialize as
        // TS-wire names like "Relevance", not numeric enum values.
        var row = Row();
        row.Evaluators = new EvaluatorMap
        {
            [EvaluatorName.Relevance] = new EvaluatorOptions { Threshold = 80 },
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        string json = Serialize(doc);
        Assert.Contains("\"evaluators\":{\"Relevance\":", json);
    }

    [Fact]
    public void Serialized_DefaultEvaluatorsKeyIsSnakeCase()
    {
        var row = Row();
        row.DocumentDefaultEvaluators = new EvaluatorMap
        {
            [EvaluatorName.Relevance] = new EvaluatorOptions(),
        };
        var doc = EvalDocumentBuilder.RowsToEvalDocument(new[] { row });
        string json = Serialize(doc);
        Assert.Contains("\"default_evaluators\":", json);
    }

    // ---------- ToM365FivePointScore ----------

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-50, 1.0)]
    [InlineData(20, 1.0)]
    [InlineData(40, 2.0)]
    [InlineData(60, 3.0)]
    [InlineData(80, 4.0)]
    [InlineData(100, 5.0)]
    [InlineData(120, 5.0)]
    [InlineData(70, 3.5)]
    [InlineData(73, 3.7)]
    public void ToM365FivePointScore_Quantization(double input, double expected)
    {
        Assert.Equal(expected, EvalDocumentBuilder.ToM365FivePointScore(input), precision: 6);
    }

    // ---------- SummarizeStatuses ----------

    [Theory]
    [InlineData(new[] { "pass", "pass" }, "pass")]
    [InlineData(new[] { "fail", "fail" }, "fail")]
    [InlineData(new[] { "pass", "fail" }, "partial")]
    [InlineData(new[] { "pass", "error" }, "error")]
    [InlineData(new[] { "partial" }, "partial")]
    public void SummarizeStatuses_Matrix(string[] inputs, string expected)
    {
        var statuses = inputs.Select(EvalStatuses.FromWireString).ToArray();
        Assert.Equal(
            EvalStatuses.FromWireString(expected),
            EvalDocumentBuilder.SummarizeStatuses(statuses));
    }
}
