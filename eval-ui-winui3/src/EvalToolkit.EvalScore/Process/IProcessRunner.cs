using System.Diagnostics;
using System.Text;

namespace EvalToolkit.EvalScore.Process;

/// <summary>
/// Test seam for spawning judge-side CLIs (<c>copilot</c> for the
/// GitHub Copilot judge; arbitrary shell command via
/// <c>EVALSCORE_GITHUB_COPILOT_COMMAND</c>). Mirrors the inline TS
/// helpers <c>runCopilotCli</c> and <c>runPromptCommand</c> in
/// <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>This is intentionally a separate implementation from
/// <c>EvalToolkit.EvalGen.LlmClients.IProcessRunner</c> for two
/// reasons:</para>
/// <list type="bullet">
///   <item>The TS source itself duplicates this logic between
///         eval-gen and eval-score.</item>
///   <item>Judge errors require provider-specific exit message
///         prefixes (e.g. <c>"GitHub Copilot CLI judge"</c>) which
///         differ from EvalGen's generic <c>"{command}"</c> prefix —
///         called out by GPT-5.5 round-1 review.</item>
/// </list>
/// </summary>
public interface IProcessRunner
{
    Task<string> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Describes a single process invocation. <see cref="ErrorMessagePrefix"/>
/// lets callers override the prefix in the non-zero-exit error message
/// so judges can match the TS strings like <c>"GitHub Copilot CLI
/// judge exited with code 1"</c>. When null, the prefix defaults to
/// <see cref="Command"/>.
/// </summary>
public sealed record ProcessInvocation(
    string Command,
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    bool UseShell = false,
    string? ErrorMessagePrefix = null);

/// <summary>
/// Default <see cref="IProcessRunner"/>. Mirrors TS spawn semantics:
/// <c>shell: false</c> for <c>copilot</c>; <c>shell: true</c> for the
/// custom command provider. Writes <see cref="ProcessInvocation.StandardInput"/>
/// to stdin then closes it. Includes the GPT-5.5 slice-13 R2 sync
/// <see cref="System.Diagnostics.Process.WaitForExit()"/> drain after
/// <see cref="System.Diagnostics.Process.WaitForExitAsync"/> so async
/// output handlers fire before we read stdout.
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

        using var process = new System.Diagnostics.Process { StartInfo = psi };
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

        // Slice-13 R2 fix: drain async output handlers before reading
        // stdout for fast-exiting children (e.g. `copilot` may print and
        // exit before OutputDataReceived has fired).
        process.WaitForExit();

        string stdoutText = stdout.ToString();
        string stderrText = stderr.ToString();

        if (process.ExitCode != 0)
        {
            // For judge invocations (ErrorMessagePrefix set) the TS
            // source uses **stderr only** — no stdout fallback. Match
            // that. For runner consumers that don't override the prefix
            // we keep the existing stderr-then-stdout fallback.
            string errorOutput = invocation.ErrorMessagePrefix is not null
                ? stderrText
                : (string.IsNullOrEmpty(stderrText) ? stdoutText : stderrText);
            string prefix = invocation.ErrorMessagePrefix ?? invocation.Command;
            throw new InvalidOperationException(
                $"{prefix} exited with code {process.ExitCode}: {errorOutput}");
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
