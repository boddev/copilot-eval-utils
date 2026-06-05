using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class WorkIQCopilotLlmClientTests
{
    public sealed record Reply(string Status);

    [Fact]
    public async Task GenerateStructured_DelegatesToInnerAndParses()
    {
        var inner = new StubWorkIQClient(_ => "{\"status\":\"ok\"}");
        await using var client = new WorkIQCopilotLlmClient(options: null, inner);

        Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

        Assert.Equal("ok", reply.Status);
        Assert.Single(inner.Asks);
        Assert.Contains("schema", inner.Asks[0]);
        Assert.Contains("\np", inner.Asks[0]); // structured prompt format
    }

    [Fact]
    public async Task EmptyResponse_Throws()
    {
        var inner = new StubWorkIQClient(_ => "   ");
        await using var client = new WorkIQCopilotLlmClient(options: null, inner);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("WorkIQ returned an empty response", ex.Message);
    }

    [Fact]
    public async Task Authenticate_PreflightAsksAndChecksResponse()
    {
        var inner = new StubWorkIQClient(_ => "{\"ok\":true}");
        await using var client = new WorkIQCopilotLlmClient(options: null, inner);
        await client.AuthenticateAsync();
        Assert.Single(inner.Asks);
        Assert.Contains("Reply with exactly this JSON object", inner.Asks[0]);
    }

    [Fact]
    public async Task Authenticate_EmptyResponse_Throws()
    {
        var inner = new StubWorkIQClient(_ => string.Empty);
        await using var client = new WorkIQCopilotLlmClient(options: null, inner);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.AuthenticateAsync());
        Assert.Equal("WorkIQ authentication preflight returned an empty response", ex.Message);
    }
}

internal sealed class StubWorkIQClient : IWorkIQClient
{
    private readonly Func<string, string> _handler;
    public List<string> Asks { get; } = [];

    public StubWorkIQClient(Func<string, string> handler)
    {
        _handler = handler;
    }

    public Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        Asks.Add(prompt);
        return Task.FromResult(_handler(prompt));
    }

    public Task<WorkIQResponse> AskWithMetadataAsync(string prompt, WorkIQAskOptions? options = null, CancellationToken cancellationToken = default)
    {
        Asks.Add(prompt);
        return Task.FromResult(new WorkIQResponse(_handler(prompt)));
    }

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
