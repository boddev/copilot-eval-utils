using EvalToolkit.EvalScore.Process;

namespace EvalToolkit.EvalScore.Tests.Judges;

/// <summary>
/// In-memory <see cref="IProcessRunner"/> that records every invocation
/// and returns a configured response. Used by the judge process tests.
/// </summary>
internal sealed class RecordingRunner : IProcessRunner
{
    public List<ProcessInvocation> Invocations { get; } = [];

    public string Response { get; set; } = string.Empty;
    public Exception? Throw { get; set; }

    public Task<string> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Response);
    }
}
