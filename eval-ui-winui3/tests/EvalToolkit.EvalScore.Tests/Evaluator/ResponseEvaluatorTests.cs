using EvalToolkit.EvalScore.Evaluator;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Tests.Judges;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Tests.Evaluator;

public class ResponseEvaluatorTests
{
    private static EvalRow Row(string id, string prompt = "Q?", string? threadId = null, int? turnIndex = null)
        => new()
        {
            Prompt = prompt,
            ExpectedAnswer = "expected",
            SourceLocation = "src",
            Id = id,
            ThreadId = threadId,
            TurnIndex = turnIndex,
        };

    [Fact]
    public async Task Populates_actual_answer_citations_and_metadata()
    {
        var client = new StubWorkIQClient
        {
            AskWithMetadataResponse = new WorkIQResponse(
                "  the answer  ",
                new[] { new Citation("title", "url") },
                Raw: new { foo = 1 },
                ConversationId: "conv-1"),
        };
        var rows = new[] { Row("q1") };
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client);
        Assert.Equal("the answer", rows[0].ActualAnswer);
        Assert.Single(rows[0].Citations!);
        Assert.NotNull(rows[0].ResponseMetadata);
        Assert.Equal("conv-1", rows[0].ConversationId);
    }

    [Fact]
    public async Task Skips_rows_with_existing_actual_answer()
    {
        var client = new StubWorkIQClient { AskWithMetadataResponse = new WorkIQResponse("fresh") };
        var rows = new[] { Row("q1") };
        rows[0].ActualAnswer = "preset";
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client);
        Assert.Empty(client.AskWithMetadataCalls);
        Assert.Equal("preset", rows[0].ActualAnswer);
    }

    [Fact]
    public async Task Captures_errors_as_error_prefix_and_continues()
    {
        var client = new ThrowingClient(new InvalidOperationException("boom"));
        var rows = new[] { Row("q1") };
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client);
        Assert.StartsWith("[ERROR:", rows[0].ActualAnswer);
        Assert.Contains("boom", rows[0].ActualAnswer);
        Assert.NotNull(rows[0].Error);
        Assert.Equal(EvalErrorCode.AgentRequestFailed, rows[0].Error!.Code);
    }

    [Fact]
    public async Task Threads_conversation_id_forward_within_thread()
    {
        var client = new CountingClient(idx => new WorkIQResponse(
            $"a{idx}", null, null, ConversationId: $"conv-{idx}"));
        var rows = new[]
        {
            Row("q1", threadId: "t", turnIndex: 0),
            Row("q2", threadId: "t", turnIndex: 1),
        };
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client);
        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[0].Options?.ConversationId);
        Assert.Equal("conv-0", client.Calls[1].Options?.ConversationId);
    }

    [Fact]
    public async Task ConversationChaining_false_breaks_chain_for_that_row()
    {
        var client = new CountingClient(idx => new WorkIQResponse(
            $"a{idx}", null, null, ConversationId: $"conv-{idx}"));
        var rows = new[]
        {
            Row("q1", threadId: "t", turnIndex: 0),
            Row("q2", threadId: "t", turnIndex: 1),
        };
        rows[1].ConversationChaining = false;
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client);
        Assert.Null(client.Calls[1].Options?.ConversationId);
    }

    [Fact]
    public async Task OnProgress_fires_for_each_row()
    {
        var client = new StubWorkIQClient { AskWithMetadataResponse = new WorkIQResponse("a") };
        var rows = new[] { Row("q1"), Row("q2"), Row("q3") };
        var progress = new List<(int Done, int Total)>();
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client, new EvaluateOptions
        {
            OnProgress = (done, total, _) => progress.Add((done, total)),
        });
        Assert.Equal(3, progress.Count);
        Assert.Equal((3, 3), progress[^1]);
    }

    [Fact]
    public async Task OnRowCompleteAsync_fires_per_row()
    {
        var client = new StubWorkIQClient { AskWithMetadataResponse = new WorkIQResponse("a") };
        var rows = new[] { Row("q1"), Row("q2") };
        int callCount = 0;
        await ResponseEvaluator.EvaluatePromptsAsync(rows, client, new EvaluateOptions
        {
            OnRowCompleteAsync = (_, _, _, _) => { callCount++; return Task.CompletedTask; },
        });
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Empty_rows_returns_quickly()
    {
        var client = new StubWorkIQClient();
        await ResponseEvaluator.EvaluatePromptsAsync(Array.Empty<EvalRow>(), client);
        Assert.Empty(client.AskWithMetadataCalls);
    }

    // -- helpers --
    private sealed class ThrowingClient(Exception ex) : IWorkIQClient
    {
        public Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
            => throw ex;
        public Task<WorkIQResponse> AskWithMetadataAsync(string prompt, WorkIQAskOptions? options = null, CancellationToken cancellationToken = default)
            => throw ex;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingClient(Func<int, WorkIQResponse> factory) : IWorkIQClient
    {
        public List<(string Prompt, WorkIQAskOptions? Options)> Calls { get; } = new();
        public Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(factory(Calls.Count).Text);
        public Task<WorkIQResponse> AskWithMetadataAsync(string prompt, WorkIQAskOptions? options = null, CancellationToken cancellationToken = default)
        {
            int idx = Calls.Count;
            Calls.Add((prompt, options));
            return Task.FromResult(factory(idx));
        }
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
