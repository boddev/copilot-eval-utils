using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Tests.Judges;

/// <summary>Captures the args passed to each WorkIQ call for assertions.</summary>
internal sealed class StubWorkIQClient : IWorkIQClient
{
    public string AskResponse { get; set; } = "0";
    public WorkIQResponse AskWithMetadataResponse { get; set; } = new("0");

    public List<(string Prompt, string? TenantId)> AskCalls { get; } = [];
    public List<(string Prompt, WorkIQAskOptions? Options)> AskWithMetadataCalls { get; } = [];
    public int ResetCalls { get; private set; }

    public Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        AskCalls.Add((prompt, tenantId));
        return Task.FromResult(AskResponse);
    }

    public Task<WorkIQResponse> AskWithMetadataAsync(string prompt, WorkIQAskOptions? options = null, CancellationToken cancellationToken = default)
    {
        AskWithMetadataCalls.Add((prompt, options));
        return Task.FromResult(AskWithMetadataResponse);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ResetCalls++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
