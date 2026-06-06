using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="IDiagnosticsService"/>. Probes:
/// workspace path + writability, WebView2 runtime, AppNotification
/// registration, jump-list state, and process identity (AUMID).
/// </summary>
/// <remarks>
/// Two construction modes:
/// <list type="bullet">
/// <item><description><b>GUI mode</b>: receives live <see cref="ITrayIconService"/>
/// and <see cref="IJumpListService"/> instances; reads their state
/// directly so the report matches what the running app sees.</description></item>
/// <item><description><b>Headless mode</b>: receives nulls for tray + jump-list.
/// The notifications subsystem is PROBED directly with a throwaway
/// register/unregister round-trip so the report reflects whether the
/// current process identity could surface a toast. The jump-list
/// subsystem is NOT probed in headless mode (a throwaway
/// <see cref="JumpListService"/> would require a UI dispatcher and a
/// real workspace job repository) — it is reported as Yellow with a
/// "headless probe — jump-list not initialized" note. Used by the
/// <c>--diagnostics</c> CLI verb that runs without
/// <see cref="Microsoft.UI.Xaml.Application.Start"/>.</description></item>
/// </list>
/// A <see cref="SemaphoreSlim"/> serializes concurrent
/// <see cref="CollectAsync"/> calls so the GUI Refresh button can't
/// double-run probes (GPT-5.5 NON-BLOCKER #11).
/// </remarks>
public sealed class DiagnosticsService : IDiagnosticsService, IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _configuredAumid;
    private readonly IWebView2RuntimeService _webView2;
    private readonly ITrayIconService? _trayIcon;
    private readonly IJumpListService? _jumpList;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DiagnosticsService(
        string workspaceRoot,
        string configuredAumid,
        IWebView2RuntimeService webView2,
        ITrayIconService? trayIcon,
        IJumpListService? jumpList)
    {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _configuredAumid = configuredAumid ?? throw new ArgumentNullException(nameof(configuredAumid));
        _webView2 = webView2 ?? throw new ArgumentNullException(nameof(webView2));
        _trayIcon = trayIcon;
        _jumpList = jumpList;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    public async Task<DiagnosticsReport> CollectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ws = CollectWorkspace(_workspaceRoot);
            var wv = await CollectWebView2Async(_webView2, cancellationToken).ConfigureAwait(false);
            var notif = await CollectNotificationsAsync(_trayIcon).ConfigureAwait(false);
            var jl = await CollectJumpListAsync(_jumpList, cancellationToken).ConfigureAwait(false);
            var proc = CollectProcess(_configuredAumid);

            return new DiagnosticsReport(
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                AppVersion: GetAppVersion(),
                Workspace: ws,
                WebView2: wv,
                Notifications: notif,
                JumpList: jl,
                Process: proc);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- per-section collectors (internal so HeadlessDiagnosticsRunner can reuse) ----

    internal static WorkspaceStatus CollectWorkspace(string workspaceRoot)
    {
        try
        {
            string full = Path.GetFullPath(workspaceRoot);
            bool exists = Directory.Exists(full);
            if (exists)
            {
                bool writable = TryWriteAndDelete(full, out var err);
                return new WorkspaceStatus(
                    Path: full,
                    Exists: true,
                    Writable: writable,
                    Creatable: false,
                    Health: writable ? DiagnosticsHealth.Green : DiagnosticsHealth.Red,
                    Note: writable ? null : $"Write test failed: {err}");
            }

            // Slice diagnostics BLOCKER #3 from GPT-5.5 plan-review:
            // workspace root may legitimately not exist yet on a fresh
            // install (the EvalGenJobService creates it lazily on first
            // job). Probe whether we COULD create it under its nearest
            // existing ancestor: yellow (healthy-for-smoke) instead of
            // red.
            //
            // Slice 32 code-review BLOCKER #2: walk up to the nearest
            // EXISTING ancestor rather than requiring the immediate
            // parent to exist. The default workspace is
            // `%LOCALAPPDATA%\EvalToolkit\workspace` — on a clean
            // machine `%LOCALAPPDATA%\EvalToolkit` typically does not
            // exist yet, but `Directory.CreateDirectory` will create
            // both segments. Requiring the immediate parent to exist
            // false-flags a clean install as Red.
            string? existingAncestor = FindNearestExistingAncestor(full);
            bool creatable = existingAncestor is not null
                && TryWriteAndDelete(existingAncestor, out _);

            return new WorkspaceStatus(
                Path: full,
                Exists: false,
                Writable: false,
                Creatable: creatable,
                Health: creatable ? DiagnosticsHealth.Yellow : DiagnosticsHealth.Red,
                Note: creatable
                    ? "Workspace will be created on first job."
                    : "Workspace root does not exist and no writable ancestor was found.");
        }
        catch (Exception ex)
        {
            return new WorkspaceStatus(
                Path: workspaceRoot,
                Exists: false,
                Writable: false,
                Creatable: false,
                Health: DiagnosticsHealth.Red,
                Note: $"Probe failed: {ex.Message}");
        }
    }

    internal static async Task<WebView2Status> CollectWebView2Async(
        IWebView2RuntimeService webView2,
        CancellationToken cancellationToken)
    {
        bool available;
        try
        {
            available = await webView2.IsRuntimeAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new WebView2Status(
                RuntimeAvailable: false,
                BundledInstallerPresent: webView2.IsBundledInstallerAvailable,
                BundledInstallerPath: webView2.BundledInstallerPath,
                ManualInstallerUrl: webView2.ManualInstallerUrl,
                Health: DiagnosticsHealth.Red,
                Note: $"Detection failed: {ex.Message}");
        }

        bool installerPresent = webView2.IsBundledInstallerAvailable;
        DiagnosticsHealth health;
        string? note;
        if (available)
        {
            health = DiagnosticsHealth.Green;
            note = null;
        }
        else if (installerPresent)
        {
            health = DiagnosticsHealth.Yellow;
            note = "Runtime missing — bundled bootstrapper available.";
        }
        else
        {
            health = DiagnosticsHealth.Red;
            note = "Runtime missing and no bundled installer.";
        }

        return new WebView2Status(
            RuntimeAvailable: available,
            BundledInstallerPresent: installerPresent,
            BundledInstallerPath: webView2.BundledInstallerPath,
            ManualInstallerUrl: webView2.ManualInstallerUrl,
            Health: health,
            Note: note);
    }

    internal static Task<NotificationsStatus> CollectNotificationsAsync(ITrayIconService? trayIcon)
    {
        if (trayIcon is not null)
        {
            // GUI mode: read the live tray state.
            bool reg = trayIcon.IsNotificationsRegistered;
            return Task.FromResult(new NotificationsStatus(
                Registered: reg,
                Health: reg ? DiagnosticsHealth.Green : DiagnosticsHealth.Yellow,
                Note: reg
                    ? "AppNotificationManager registered."
                    : "Not registered — expected in unpackaged dev runs."));
        }

        // Headless mode: probe Register/Unregister directly. Avoids
        // requiring the GUI bootstrap to run.
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
            try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister(); } catch { /* swallow */ }
            return Task.FromResult(new NotificationsStatus(
                Registered: true,
                Health: DiagnosticsHealth.Green,
                Note: "Probe Register/Unregister succeeded."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new NotificationsStatus(
                Registered: false,
                Health: DiagnosticsHealth.Yellow,
                Note: $"Probe failed (expected in unpackaged dev): {ex.Message}"));
        }
    }

    internal static async Task<JumpListStatus> CollectJumpListAsync(
        IJumpListService? jumpList,
        CancellationToken cancellationToken)
    {
        if (jumpList is not null)
        {
            bool init = jumpList.Initialized;
            // GPT-5.5 BLOCKER #2: read the LAST captured refresh result
            // (not just "enqueued"). RefreshAsync is fire-and-forget for
            // the GUI but the diagnostics surface needs the actual COM
            // result; new RefreshAndWaitAsync gives us that.
            bool ok;
            DateTimeOffset? at;
            try
            {
                ok = await jumpList.RefreshAndWaitAsync(cancellationToken).ConfigureAwait(false);
                at = jumpList.LastRefreshUtc;
            }
            catch (Exception ex)
            {
                return new JumpListStatus(
                    Initialized: init,
                    LastRefreshSucceeded: false,
                    LastRefreshUtc: jumpList.LastRefreshUtc,
                    Health: DiagnosticsHealth.Yellow,
                    Note: $"Refresh threw: {ex.Message}");
            }

            return new JumpListStatus(
                Initialized: init,
                LastRefreshSucceeded: ok,
                LastRefreshUtc: at,
                Health: ok ? DiagnosticsHealth.Green : DiagnosticsHealth.Yellow,
                Note: ok ? null : "Refresh failed — jump-list COM unavailable.");
        }

        // Headless mode: no service instance. Report "uninitialized"
        // as yellow (jump-list works but wasn't probed).
        return new JumpListStatus(
            Initialized: false,
            LastRefreshSucceeded: false,
            LastRefreshUtc: null,
            Health: DiagnosticsHealth.Yellow,
            Note: "Headless probe — jump-list not initialized.");
    }

    internal static ProcessStatus CollectProcess(string configuredAumid)
    {
        int pid = Environment.ProcessId;
        string exe = Environment.ProcessPath ?? "(unknown)";

        string? actual = null;
        string? aumidError = null;
        try
        {
            int hr = GetCurrentProcessExplicitAppUserModelID(out IntPtr ptr);
            if (hr == 0 && ptr != IntPtr.Zero)
            {
                actual = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
            }
            else
            {
                aumidError = $"GetCurrentProcessExplicitAppUserModelID hr=0x{hr:X8}";
            }
        }
        catch (Exception ex)
        {
            aumidError = ex.Message;
        }

        // AUMID is informational — if the call fails, the report is
        // still useful for other sections. Health stays Green.
        return new ProcessStatus(
            Pid: pid,
            ExePath: exe,
            ConfiguredAumid: configuredAumid,
            ActualAumid: actual,
            AumidError: aumidError,
            Health: DiagnosticsHealth.Green);
    }

    // ---- helpers ----

    /// <summary>
    /// Walks up the directory chain from <paramref name="path"/> to find
    /// the nearest ancestor that already exists on disk. Returns
    /// <c>null</c> if no ancestor exists (e.g. an unreachable drive
    /// letter). Used by the workspace probe so a clean install with
    /// `%LOCALAPPDATA%\EvalToolkit\workspace` missing still resolves to
    /// `%LOCALAPPDATA%` as the writable ancestor.
    /// </summary>
    private static string? FindNearestExistingAncestor(string path)
    {
        try
        {
            string? current = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }
                string? next = Path.GetDirectoryName(current);
                if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
                {
                    // GetDirectoryName returns the same path at the root
                    // ("C:\\" => "C:\\") — guard against infinite loops.
                    return Directory.Exists(current) ? current : null;
                }
                current = next;
            }
        }
        catch
        {
            // Unreachable / malformed path — treat as no ancestor.
        }
        return null;
    }

    private static bool TryWriteAndDelete(string directory, out string? error)
    {
        error = null;
        string probe = Path.Combine(directory, $".evaltoolkit-probe-{Path.GetRandomFileName()}");
        try
        {
            File.WriteAllBytes(probe, new byte[] { 0x42 });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* swallow */ }
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = typeof(DiagnosticsService).Assembly;
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr AppID);
}
