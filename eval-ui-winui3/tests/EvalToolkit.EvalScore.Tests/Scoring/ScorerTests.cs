using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Scoring;

public class ScorerTests
{
    private static EvalRow Row(string actual = "answer", string? expected = "answer", string id = "q1")
        => new() { Prompt = "Q?", ExpectedAnswer = expected ?? string.Empty, SourceLocation = "s", ActualAnswer = actual, Id = id };

    [Fact]
    public async Task Skips_rows_with_existing_similarity_score()
    {
        var judge = new MockJudge();
        var rows = new[] { Row() };
        rows[0].SimilarityScore = 75;
        await Scorer.ScoreAnswersAsync(rows, client: null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
        });
        Assert.Equal(0, judge.CallCount);
        Assert.Equal(75, rows[0].SimilarityScore);
    }

    [Fact]
    public async Task Empty_actual_answer_short_circuits_with_turnSkipped()
    {
        var judge = new MockJudge();
        var rows = new[] { Row(actual: string.Empty) };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
        });
        Assert.Equal(0, judge.CallCount);
        Assert.Equal(0, rows[0].SimilarityScore);
        Assert.NotNull(rows[0].Error);
        Assert.Equal(EvalErrorCode.TurnSkipped, rows[0].Error!.Code);
        Assert.Equal("Actual answer is empty.", rows[0].Error!.Message);
    }

    [Fact]
    public async Task Error_prefixed_answer_short_circuits_with_agentRequestFailed()
    {
        var judge = new MockJudge();
        var rows = new[] { Row(actual: "[ERROR: timeout]") };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
        });
        Assert.Equal(0, judge.CallCount);
        Assert.Equal(0, rows[0].SimilarityScore);
        Assert.NotNull(rows[0].Error);
        Assert.Equal(EvalErrorCode.AgentRequestFailed, rows[0].Error!.Code);
    }

    [Fact]
    public async Task Sets_similarity_score_from_primary_metric()
    {
        var judge = new MockJudge { Scorer = (_, _) => new JudgeScore(82, "good", "m") };
        var rows = new[] { Row() };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
            Threshold = 70,
        });
        Assert.Equal(82, rows[0].SimilarityScore);
        Assert.NotNull(rows[0].Metrics);
        Assert.Single(rows[0].Metrics!);
        Assert.Equal(EvaluatorName.Similarity, rows[0].Metrics![0].Name);
        Assert.True(rows[0].Metrics![0].Passed);
    }

    [Fact]
    public async Task Falls_back_to_secondary_judge_on_eligible_failure()
    {
        var primary = new MockJudge { ErrorThrower = (_, _) => new InvalidOperationException("rate limit hit") };
        var fallback = new MockJudge
        {
            Provider = JudgeProvider.GitHubCopilot,
            Scorer = (_, _) => new JudgeScore(60, "fallback used", "gh"),
        };
        var rows = new[] { Row() };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = primary,
            FallbackJudge = fallback,
            Evaluators = new[] { EvaluatorName.Similarity },
            Threshold = 70,
        });
        Assert.Equal(60, rows[0].SimilarityScore);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
        Assert.NotNull(rows[0].Metrics);
        string? reason = rows[0].Metrics![0].Reason;
        Assert.NotNull(reason);
        Assert.Contains("Fallback from", reason);
        Assert.Contains("rate limit hit", reason);
    }

    [Fact]
    public async Task Does_not_fall_back_for_non_eligible_error()
    {
        var primary = new MockJudge { ErrorThrower = (_, _) => new InvalidOperationException("permission denied") };
        var fallback = new MockJudge { Provider = JudgeProvider.GitHubCopilot };
        var rows = new[] { Row() };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = primary,
            FallbackJudge = fallback,
            Evaluators = new[] { EvaluatorName.Similarity },
        });
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task Deterministic_metrics_appended_after_llm_metrics()
    {
        var judge = new MockJudge { Scorer = (_, _) => new JudgeScore(50, "r", "m") };
        var rows = new[] { Row(actual: "hello world", expected: "hello world") };
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity, EvaluatorName.ExactMatch, EvaluatorName.PartialMatch },
        });
        Assert.NotNull(rows[0].Metrics);
        Assert.Equal(3, rows[0].Metrics!.Count);
        Assert.Equal(EvaluatorName.Similarity, rows[0].Metrics![0].Name);
        Assert.Equal(EvaluatorName.ExactMatch, rows[0].Metrics![1].Name);
        Assert.Equal(EvaluatorName.PartialMatch, rows[0].Metrics![2].Name);
        Assert.Equal(MetricProvider.Deterministic, rows[0].Metrics![1].Provider);
    }

    [Fact]
    public async Task OnProgress_fires_per_row()
    {
        var judge = new MockJudge();
        var rows = new[] { Row(id: "q1"), Row(id: "q2"), Row(id: "q3") };
        var progress = new List<(int, int)>();
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
            OnProgress = (done, total) => progress.Add((done, total)),
        });
        Assert.Equal(3, progress.Count);
        Assert.Equal((3, 3), progress[^1]);
    }

    [Fact]
    public async Task OnRowCompleteAsync_invoked_for_each_processed_row()
    {
        var judge = new MockJudge();
        var rows = new[] { Row(id: "q1"), Row(id: "q2") };
        var seen = new List<string>();
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
            OnRowCompleteAsync = (_, row, _, _) => { seen.Add(row.Id!); return Task.CompletedTask; },
        });
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Empty_rows_returns_early_without_judge_call()
    {
        var judge = new MockJudge();
        await Scorer.ScoreAnswersAsync(Array.Empty<EvalRow>(), null, new ScoreOptions { Judge = judge });
        Assert.Equal(0, judge.CallCount);
    }

    // Reviewer-flagged mandatory gap: pre-scored rows must not fire
    // OnProgress or OnRowComplete and must not push the completed count
    // above total. TS scoreAnswers (scorer.ts line 45-47) returns BEFORE
    // increment/callbacks for pre-scored rows.
    [Fact]
    public async Task Pre_scored_rows_do_not_fire_callbacks_or_overcount()
    {
        var judge = new MockJudge();
        var rows = new[]
        {
            Row(id: "a"),
            Row(id: "b"),
            Row(id: "c"),
        };
        rows[0].SimilarityScore = 70;
        rows[2].SimilarityScore = 90;

        var progress = new List<(int done, int total)>();
        var completed = new List<string>();
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
            OnProgress = (d, t) => progress.Add((d, t)),
            OnRowCompleteAsync = (_, row, _, _) => { completed.Add(row.Id!); return Task.CompletedTask; },
        });

        // Only the middle row ("b") was scored — judge called exactly once.
        Assert.Equal(1, judge.CallCount);
        // OnRowComplete fires only for "b" — pre-scored "a"/"c" return early.
        Assert.Equal(new[] { "b" }, completed);
        // OnProgress fires only for "b". The final (done, total) is (3, 3)
        // because the seed already counted "a"+"c", then "b" pushes to 3.
        Assert.Single(progress);
        Assert.Equal((3, 3), progress[0]);
        // Critically: no progress event exceeds total.
        Assert.All(progress, p => Assert.True(p.done <= p.total));
    }

    // Reviewer-flagged mandatory gap: multi-turn rows must be scored AND
    // their callbacks fired in original row order. TS scoreAnswers uses a
    // single shared FIFO `nextIndex++` worker pool — it does not group by
    // thread. With concurrency=1 the order must be strictly 0,1,2,3...
    // even when turn_index/thread_id would otherwise suggest grouping.
    [Fact]
    public async Task Out_of_order_thread_rows_score_in_original_row_order()
    {
        var judge = new MockJudge();
        // Interleave two threads: A0, B0, A1, B1 — TS would score them
        // in this exact order. A thread-grouped scheduler would instead
        // score A0, A1, B0, B1 — that's the bug we're guarding against.
        var rows = new[]
        {
            new EvalRow { Prompt = "Q?", ExpectedAnswer = "x", SourceLocation = "s", ActualAnswer = "a", Id = "A0", ThreadId = "A", TurnIndex = 0, ItemIndex = 0 },
            new EvalRow { Prompt = "Q?", ExpectedAnswer = "x", SourceLocation = "s", ActualAnswer = "a", Id = "B0", ThreadId = "B", TurnIndex = 0, ItemIndex = 1 },
            new EvalRow { Prompt = "Q?", ExpectedAnswer = "x", SourceLocation = "s", ActualAnswer = "a", Id = "A1", ThreadId = "A", TurnIndex = 1, ItemIndex = 0 },
            new EvalRow { Prompt = "Q?", ExpectedAnswer = "x", SourceLocation = "s", ActualAnswer = "a", Id = "B1", ThreadId = "B", TurnIndex = 1, ItemIndex = 1 },
        };

        var completed = new List<string>();
        await Scorer.ScoreAnswersAsync(rows, null, new ScoreOptions
        {
            Judge = judge,
            Evaluators = new[] { EvaluatorName.Similarity },
            Concurrency = 1,
            DelayMs = 0,
            OnRowCompleteAsync = (_, row, _, _) => { completed.Add(row.Id!); return Task.CompletedTask; },
        });

        Assert.Equal(new[] { "A0", "B0", "A1", "B1" }, completed);
    }
}
