using System.Text.Json;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

public sealed class GitHubCopilotCliLlmClientTests
{
    public sealed record Reply(string Status);

    [Fact]
    public async Task SendsExpectedArgsAndParsesStdout()
    {
        ProcessInvocation? captured = null;
        var runner = new RecordingRunner(invocation =>
        {
            captured = invocation;
            return "{\"status\":\"ok\"}";
        });

        var client = new GitHubCopilotCliLlmClient(new LlmClientOptions(), runner);
        Reply reply = await client.GenerateStructuredAsync<Reply>("What is the answer?", "Return {status}.");

        Assert.NotNull(captured);
        Assert.Equal("gh", captured!.Command);
        Assert.False(captured.UseShell);
        Assert.Equal("copilot", captured.Arguments[0]);
        Assert.Equal("--", captured.Arguments[1]);
        Assert.Equal("-p", captured.Arguments[2]);
        Assert.Contains("What is the answer?", captured.Arguments[3]);
        Assert.Contains("--silent", captured.Arguments);
        Assert.Contains("--no-color", captured.Arguments);
        Assert.Equal("ok", reply.Status);
    }

    [Fact]
    public async Task IncludesModelWhenSpecified()
    {
        ProcessInvocation? captured = null;
        var runner = new RecordingRunner(invocation =>
        {
            captured = invocation;
            return "{\"status\":\"ok\"}";
        });

        var client = new GitHubCopilotCliLlmClient(new LlmClientOptions { Model = "gpt-4o" }, runner);
        await client.GenerateStructuredAsync<Reply>("q", "s");

        Assert.NotNull(captured);
        int modelIdx = captured!.Arguments.ToList().IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gpt-4o", captured.Arguments[modelIdx + 1]);
    }
}

public sealed class CommandLlmClientTests
{
    public sealed record Reply(string Status);

    [Fact]
    public async Task SendsJsonOnStdinAndParsesStdout()
    {
        string? capturedStdin = null;
        ProcessInvocation? captured = null;
        var runner = new RecordingRunner(invocation =>
        {
            captured = invocation;
            capturedStdin = invocation.StandardInput;
            return "{\"status\":\"done\"}";
        });

        var client = new CommandLlmClient("python script.py", runner);
        Reply reply = await client.GenerateStructuredAsync<Reply>("the prompt", "the schema");

        Assert.NotNull(capturedStdin);
        Assert.True(captured!.UseShell);
        Assert.Equal("python script.py", captured.Command);

        using JsonDocument doc = JsonDocument.Parse(capturedStdin!);
        Assert.Equal("the prompt", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("the schema", doc.RootElement.GetProperty("schemaDescription").GetString());
        Assert.Equal("done", reply.Status);
    }

    [Fact]
    public void EmptyCommand_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new CommandLlmClient(""));
    }
}

internal sealed class RecordingRunner : IProcessRunner
{
    private readonly Func<ProcessInvocation, string> _handler;

    public RecordingRunner(Func<ProcessInvocation, string> handler)
    {
        _handler = handler;
    }

    public Task<string> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult(_handler(invocation));
}
