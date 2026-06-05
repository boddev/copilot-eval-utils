using EvalToolkit.EvalScore.Process;

namespace EvalToolkit.EvalScore.Tests.Judges;

/// <summary>
/// Real-process behavior tests for <see cref="SystemProcessRunner"/>.
/// Uses platform commands that are guaranteed available on Windows CI.
/// </summary>
public class SystemProcessRunnerTests
{
    [Fact]
    public async Task SuccessfulExit_ReturnsStdout()
    {
        var runner = new SystemProcessRunner();
        var output = await runner.RunAsync(
            new ProcessInvocation(
                Command: "cmd.exe",
                Arguments: new[] { "/c", "echo", "hello" }),
            CancellationToken.None);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task NonZeroExit_WithErrorPrefix_UsesStderrOnly_NoStdoutFallback()
    {
        // Per round-2 review B1: judge invocations (ErrorMessagePrefix
        // set) must use stderr only — TS does NOT fall back to stdout.
        var runner = new SystemProcessRunner();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                new ProcessInvocation(
                    Command: "cmd.exe",
                    Arguments: new[] { "/c", "echo only-on-stdout && exit 7" },
                    ErrorMessagePrefix: "Test judge"),
                CancellationToken.None));

        Assert.StartsWith("Test judge exited with code 7: ", ex.Message);
        // Must NOT contain the stdout text. cmd's `echo only-on-stdout`
        // writes to stdout; stderr is empty; with the prefix set we use
        // stderr only → suffix is empty.
        Assert.DoesNotContain("only-on-stdout", ex.Message);
    }

    [Fact]
    public async Task NonZeroExit_WithoutErrorPrefix_FallsBackToStdoutWhenStderrEmpty()
    {
        // Non-judge consumers keep the legacy stderr-or-stdout fallback.
        var runner = new SystemProcessRunner();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                new ProcessInvocation(
                    Command: "cmd.exe",
                    Arguments: new[] { "/c", "echo legacy-stdout && exit 1" }),
                CancellationToken.None));

        Assert.StartsWith("cmd.exe exited with code 1: ", ex.Message);
        Assert.Contains("legacy-stdout", ex.Message);
    }

    [Fact]
    public async Task StdinIsWrittenAndClosed()
    {
        var runner = new SystemProcessRunner();
        // findstr will hang waiting for stdin if not closed; if we
        // close it correctly the process exits with code 1 (no match).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                new ProcessInvocation(
                    Command: "findstr",
                    Arguments: new[] { "no-match-pattern-xyz" },
                    StandardInput: "irrelevant input\n",
                    ErrorMessagePrefix: "findstr test"),
                CancellationToken.None));
        Assert.StartsWith("findstr test exited with code 1: ", ex.Message);
    }
}
