using System.Diagnostics;
using System.Text;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Test seam for the three providers that shell out: the GitHub Copilot
/// CLI client (<c>gh copilot ...</c>), the custom command provider, and
/// the M365 Copilot Chat client's <c>az</c> fallback. Mirrors the
/// behavior of TS <c>runProcess</c> in <c>eval-gen/src/llm-client.ts</c>.
///
/// <para>TS shell semantics (preserved here):</para>
/// <list type="bullet">
///   <item><c>gh copilot</c> runs WITHOUT a shell (<c>shell: false</c>).</item>
///   <item><c>az</c> uses a shell on Windows only.</item>
///   <item>The <c>command</c> provider always uses a shell so users can
///         supply <c>"python my_script.py --flag"</c>.</item>
/// </list>
///
/// Implementations must:
/// <list type="bullet">
///   <item>Write <see cref="ProcessInvocation.StandardInput"/> to stdin
///         then close it (if non-null).</item>
///   <item>Throw if the exit code is non-zero, including stderr/stdout
///         in the message in the same format as TS:
///         <c>"{command} exited with code {code}: {stderr || stdout}"</c>.</item>
///   <item>Respect the cancellation token.</item>
/// </list>
/// </summary>
public interface IProcessRunner
{
    Task<string> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Describes a single process invocation. See <see cref="IProcessRunner"/>.</summary>
public sealed record ProcessInvocation(
    string Command,
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    bool UseShell = false);

/// <summary>
/// Default <see cref="IProcessRunner"/> that uses <see cref="Process"/>.
/// Mirrors TS <c>runProcess</c>; on the shell path it mirrors TS
/// <c>buildShellCommand</c> + <c>quoteShellArg</c>.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<string> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        ProcessStartInfo psi;
        if (invocation.UseShell)
        {
            string commandLine = BuildShellCommand(invocation.Command, invocation.Arguments);
            psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c " + commandLine,
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"" + commandLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = invocation.Command,
            };
            foreach (string arg in invocation.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {invocation.Command}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (invocation.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Close();
            }
        }
        else
        {
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        // GPT-5.5 R2: WaitForExitAsync can return before async output events
        // are fully drained. Block on the synchronous WaitForExit to flush the
        // OutputDataReceived / ErrorDataReceived handlers, otherwise stdout
        // can appear empty for fast-exiting children (e.g. `gh copilot`).
        process.WaitForExit();

        string stdoutText = stdout.ToString();
        string stderrText = stderr.ToString();

        if (process.ExitCode != 0)
        {
            string errorOutput = string.IsNullOrEmpty(stderrText) ? stdoutText : stderrText;
            throw new InvalidOperationException(
                $"{invocation.Command} exited with code {process.ExitCode}: {errorOutput}");
        }

        return stdoutText;
    }

    internal static string BuildShellCommand(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return command;
        var sb = new StringBuilder(command);
        foreach (string a in args)
        {
            sb.Append(' ').Append(QuoteShellArg(a));
        }
        return sb.ToString();
    }

    internal static string QuoteShellArg(string value)
    {
        if (!ContainsShellMetachar(value)) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static bool ContainsShellMetachar(string value)
    {
        foreach (char c in value)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '"' or '&' or '|' or '<' or '>' or '^')
            {
                return true;
            }
        }
        return false;
    }
}
