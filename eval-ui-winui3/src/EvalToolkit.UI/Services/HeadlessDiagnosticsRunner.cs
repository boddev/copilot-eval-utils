using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Runs <see cref="DiagnosticsService.CollectAsync"/> from the
/// <c>--diagnostics</c> CLI verb WITHOUT calling
/// <see cref="Microsoft.UI.Xaml.Application.Start"/>. Writes the JSON
/// report to <see cref="Console.Out"/> and returns a process exit code
/// suitable for CI smoke pipelines.
/// </summary>
/// <remarks>
/// GPT-5.5 slice-diagnostics plan-review BLOCKER #1 fix: the
/// <c>--diagnostics</c> verb must be detected and dispatched in
/// <see cref="Program.Main"/> BEFORE single-instance redirection,
/// otherwise a secondary invocation with <c>--diagnostics</c> would
/// redirect to the primary GUI and exit without writing JSON.
/// <para>
/// The runner builds only the services its probes need
/// (<see cref="WebView2RuntimeService"/>). The tray and jump-list
/// instances are nulled, so notifications get a throwaway
/// register/unregister probe and the jump list is reported as Yellow
/// with a "headless probe — jump-list not initialized" note. The
/// WinUI shell is never brought up.
/// </para>
/// <para>
/// Exit codes:
/// <list type="bullet">
/// <item><description><c>0</c> — overall health Green or Yellow.</description></item>
/// <item><description><c>1</c> — overall health Red (any blocking failure).</description></item>
/// <item><description><c>2</c> — diagnostics collector itself threw.</description></item>
/// </list>
/// </para>
/// <para>
/// WinExe stdout caveat (GPT-5.5 NON-BLOCKER #7): the WinUI app runs
/// under the Windows subsystem, so an interactive terminal won't see
/// stdout by default. We mitigate two ways:
/// <list type="number">
/// <item><description>
/// On entry the runner calls <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>
/// (Win32 / kernel32) so a console host that launched us — cmd.exe,
/// pwsh, Windows Terminal — receives our stdout/stderr writes. This
/// covers interactive CLI use and CI pipelines that redirect stdout
/// at the shell level (<c>EvalToolkit.UI.exe --diagnostics &gt; diag.json</c>).
/// </description></item>
/// <item><description>
/// Callers that need guaranteed JSON capture regardless of console
/// behavior (e.g. PowerShell <c>&amp; exe</c> variable assignment,
/// detached service hosts) can pass <c>--diagnostics-out &lt;path&gt;</c>
/// and the runner writes the report to that file instead of stdout.
/// The path may be absolute or relative to the working directory.
/// </description></item>
/// </list>
/// The packaging docs spell both options out.
/// </para>
/// </remarks>
internal static class HeadlessDiagnosticsRunner
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private const int STD_OUTPUT_HANDLE = -11;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    /// <summary>
    /// Attach to the parent console only when this process does NOT
    /// already have a valid stdout handle. If the parent redirected
    /// stdout to a file or pipe (e.g. <c>exe --diagnostics &gt; diag.json</c>
    /// from cmd.exe, or PowerShell's <c>Start-Process -RedirectStandardOutput</c>),
    /// we MUST keep the inherited handle — AttachConsole would replace
    /// it with the parent's console screen buffer and the redirect file
    /// would end up empty.
    /// </summary>
    private static void EnsureConsoleAttached()
    {
        try
        {
            IntPtr currentStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
            // Inherited stdout (real handle, IsOutputRedirected==true)
            // → leave it alone. AttachConsole would replace it.
            if (currentStdOut != IntPtr.Zero && currentStdOut != INVALID_HANDLE_VALUE)
            {
                return;
            }
            // No inherited stdout → best-effort attach to parent so an
            // interactive cmd / pwsh terminal sees our writes.
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch
        {
            // Best effort. --diagnostics-out remains a reliable fallback.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Task<int> RunAsync(CancellationToken cancellationToken = default)
        => RunAsync(Array.Empty<string>(), cancellationToken);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // Best-effort: if parent has no console for us, attach to the
        // parent so cmd / pwsh / Windows Terminal can see stdout.
        // Crucially we DO NOT call AttachConsole when stdout was already
        // inherited (e.g. cmd's `> file` redirect) — that would clobber
        // the redirect. --diagnostics-out is always honored as a file
        // fallback regardless.
        EnsureConsoleAttached();

        string? outPath = TryGetOutPath(args);

        try
        {
            string workspaceRoot = ResolveWorkspaceRoot();
            string aumid = JumpListService.DefaultAppId;
            IWebView2RuntimeService webView2 = new WebView2RuntimeService();

            // Headless mode: tray + jump-list nulls → DiagnosticsService
            // probes those subsystems instead of reading live state.
            using DiagnosticsService svc = new DiagnosticsService(
                workspaceRoot,
                aumid,
                webView2,
                trayIcon: null,
                jumpList: null);

            DiagnosticsReport report = await svc.CollectAsync(cancellationToken).ConfigureAwait(false);

            string json = JsonSerializer.Serialize(report, JsonOptions);

            if (!string.IsNullOrWhiteSpace(outPath))
            {
                string fullPath = Path.GetFullPath(outPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(fullPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Sync WriteLine + Flush: the async overload taking
                // ReadOnlyMemory<char> was observed to return without
                // flushing under PowerShell `& exe` variable capture in
                // slice 32 smoke. Sync write + explicit Flush is reliable
                // when the parent console exists (post-AttachConsole).
                Console.Out.WriteLine(json);
                Console.Out.Flush();
            }

            return report.OverallHealth == DiagnosticsHealth.Red ? 1 : 0;
        }
        catch (Exception ex)
        {
            try
            {
                var err = new { error = ex.GetType().FullName, message = ex.Message };
                string errJson = JsonSerializer.Serialize(err, JsonOptions);
                if (!string.IsNullOrWhiteSpace(outPath))
                {
                    try
                    {
                        await File.WriteAllTextAsync(Path.GetFullPath(outPath), errJson, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Out-path itself may be bogus; fall through to stderr.
                    }
                }
                Console.Error.WriteLine(errJson);
                Console.Error.Flush();
            }
            catch
            {
                // Last-ditch — at least leave a marker on the wire.
            }
            return 2;
        }
    }

    private static string? TryGetOutPath(string[] args)
    {
        if (args is null) return null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            // Forms accepted: --diagnostics-out <path>, /diagnostics-out <path>,
            // --diagnostics-out=<path>, /diagnostics-out=<path>.
            if (a.StartsWith("--diagnostics-out=", StringComparison.OrdinalIgnoreCase))
            {
                return a.Substring("--diagnostics-out=".Length);
            }
            if (a.StartsWith("/diagnostics-out=", StringComparison.OrdinalIgnoreCase))
            {
                return a.Substring("/diagnostics-out=".Length);
            }
            if ((string.Equals(a, "--diagnostics-out", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(a, "/diagnostics-out", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string ResolveWorkspaceRoot()
    {
        // Mirror App.WorkspaceRoot resolution so headless and GUI agree
        // on the path being probed.
        string? env = Environment.GetEnvironmentVariable("EVALTOOLKIT_WORKSPACE_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "EvalToolkit", "workspace");
    }
}
