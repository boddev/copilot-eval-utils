using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EvalToolkit.Jobs;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using EvalToolkit.UI.Views;
using WinRT.Interop;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="ITrayIconService"/>. Owns a single
/// <see cref="TaskbarIcon"/> created programmatically (no XAML
/// markup), routes its events to the shell window, and surfaces
/// Windows toast notifications when background jobs reach a terminal
/// state while the main window is not in focus.
/// </summary>
/// <remarks>
/// <para>
/// Slice 28 (winui-native-plus-tray). All four GPT-5.5 plan-review
/// blockers are addressed:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="IsExiting"/> guards the shell window's
/// <c>AppWindow.Closing</c> handler so the tray <em>Exit</em> menu can
/// actually shut the app down. Window X click reroutes through
/// <see cref="HideToTray"/>; tray Exit sets <see cref="IsExiting"/>
/// and raises <see cref="ExitRequested"/>, after which the caller is
/// free to call <see cref="Application.Exit"/>.
/// </description></item>
/// <item><description>
/// <see cref="ShowWindow"/> calls
/// <see cref="WindowExtensions.Show(Window, bool)"/> from H.NotifyIcon
/// (which AppWindow-shows + restores efficiency mode) and then
/// <see cref="ShellWindow.BringToFront"/> for foreground promotion —
/// so tray left-click and single-instance reactivation both bring a
/// hidden window all the way back.
/// </description></item>
/// <item><description>
/// Toast routing: terminal-status toasts include
/// <c>action=open-job</c> + <c>path=&lt;dir&gt;</c> arguments. The
/// service hooks <see cref="AppNotificationManager.NotificationInvoked"/>
/// and opens the job folder via Explorer when the user clicks the
/// toast body or its action button.
/// </description></item>
/// <item><description>
/// <see cref="Dispose"/> unsubscribes from both job-service events,
/// from <see cref="AppNotificationManager.NotificationInvoked"/>, and
/// disposes the <see cref="TaskbarIcon"/> rather than relying on
/// process teardown — the WinUI bridge can outlive the managed
/// runtime briefly during shutdown.
/// </description></item>
/// </list>
/// <para>
/// Toast plumbing for <strong>unpackaged</strong> runs is fragile —
/// <see cref="AppNotificationManager.Default"/>.<c>Register()</c>
/// requires a COM activator class that is only wired up automatically
/// for packaged apps. Slice 31 (<c>winui-native-plus-toasts</c>) will
/// add the activator registration. For slice 28 the Register call is
/// wrapped in try / catch + Debug.WriteLine so an unpackaged dev run
/// keeps working even when toasts silently no-op.
/// </para>
/// </remarks>
public sealed class TrayIconService : ITrayIconService
{
    private readonly IEvalGenJobService _genJobs;
    private readonly IEvalScoreJobService _scoreJobs;
    private readonly string _workspaceRoot;
    private readonly DispatcherQueue _uiDispatcher;
    private readonly INotificationActionRouter _notificationRouter;
    private readonly object _gate = new();

    private TaskbarIcon? _tray;
    private Window? _shellWindow;
    // Slice 28 GPT-5.5 code-review non-blocker: cache the HWND on the
    // UI thread during Initialize so the foreground-window check can run
    // from arbitrary job-event threads without re-entering WinRT thread-
    // affinity surprises.
    private IntPtr _shellHwnd;
    private bool _isHidden;
    private bool _isExiting;
    private bool _firstHideHintShown;
    private bool _notificationsRegistered;
    private bool _notificationInvokedSubscribed;
    private bool _genJobsSubscribed;
    private bool _scoreJobsSubscribed;
    private bool _disposed;

    // Toast dedupe: collapse rapid repeat-fires of the same job
    // terminal status into a single notification. The window of 2s
    // matches GPT-5.5's "debounce within 2s" guidance from the plan
    // review — long enough to swallow back-to-back state events,
    // short enough that genuinely-separate completions still notify.
    private string? _lastNotifiedJobDir;
    private DateTimeOffset _lastNotifiedAtUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan NotificationDedupeWindow = TimeSpan.FromSeconds(2);

    public TrayIconService(
        IEvalGenJobService genJobs,
        IEvalScoreJobService scoreJobs,
        string workspaceRoot,
        DispatcherQueue uiDispatcher,
        INotificationActionRouter notificationRouter)
    {
        _genJobs = genJobs ?? throw new ArgumentNullException(nameof(genJobs));
        _scoreJobs = scoreJobs ?? throw new ArgumentNullException(nameof(scoreJobs));
        _workspaceRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : throw new ArgumentException("Workspace root required.", nameof(workspaceRoot));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _notificationRouter = notificationRouter ?? throw new ArgumentNullException(nameof(notificationRouter));
    }

    public bool IsWindowHidden
    {
        get { lock (_gate) { return _isHidden; } }
    }

    public bool IsExiting
    {
        get { lock (_gate) { return _isExiting; } }
    }

    public bool IsNotificationsRegistered
    {
        get { lock (_gate) { return _notificationsRegistered; } }
    }

    public event EventHandler? ExitRequested;

    public void Initialize(Window shellWindow)
    {
        ArgumentNullException.ThrowIfNull(shellWindow);
        if (_tray is not null)
        {
            throw new InvalidOperationException("TrayIconService.Initialize called more than once.");
        }

        _shellWindow = shellWindow;
        // Cache the HWND while we're on the UI thread; the foreground
        // check fires from job-state callbacks that aren't guaranteed
        // to marshal to the dispatcher first.
        try
        {
            _shellHwnd = WindowNative.GetWindowHandle(shellWindow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.Initialize: HWnd capture failed: {ex}");
            _shellHwnd = IntPtr.Zero;
        }

        _tray = new TaskbarIcon
        {
            ToolTipText = "EvalToolkit",

            // GeneratedIconSource renders a glyph at runtime instead of
            // requiring a bundled .ico. Slice 29 (msix-packaging) is
            // the right place to swap in a real branded icon asset; for
            // slice 28 we want zero asset commitment so dev builds and
            // CI both work without binary checks-in.
            IconSource = new GeneratedIconSource
            {
                Text = "ET",
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
                FontSize = 28,
            },

            ContextFlyout = BuildContextMenu(),

            // LeftClick → bring the window forward, same as picking
            // "Open EvalToolkit" from the menu. This is the gesture
            // Windows users expect from tray icons.
            NoLeftClickDelay = true,
        };

        _tray.LeftClickCommand = new RelayDelegateCommand(() => ShowWindow());

        // Programmatic creation requires ForceCreate() — the WinUI XAML
        // path would do this implicitly when the FrameworkElement gets
        // measured, but we never put the TaskbarIcon in the visual tree.
        // efficiency-mode=false because the shell window stays visible
        // initially; the user has to actively hide-to-tray for the
        // efficiency-mode optimization to be worth taking.
        _tray.ForceCreate(enablesEfficiencyMode: false);

        TryRegisterNotifications();

        // Subscribe to terminal-status events from BOTH pipelines so
        // background completions notify regardless of source. Slice 28
        // (GPT-5.5 code-review blocker): the original implementation
        // only subscribed to gen and silently missed score completions.
        _genJobs.JobStateChanged += OnJobStateChanged;
        _genJobsSubscribed = true;
        _scoreJobs.JobStateChanged += OnJobStateChanged;
        _scoreJobsSubscribed = true;
    }

    public void ShowWindow()
    {
        var window = _shellWindow;
        if (window is null) return;

        // Marshal — caller may be the tray thread (LeftClickCommand
        // fires on a background pump) or the JobStateChanged thread.
        if (!_uiDispatcher.HasThreadAccess)
        {
            _uiDispatcher.TryEnqueue(ShowWindow);
            return;
        }

        try
        {
            // H.NotifyIcon's Show() does AppWindow.Show() + clears
            // efficiency mode if Hide() set it. Matches the recommended
            // tray-restore pattern from the H.NotifyIcon docs.
            WindowExtensions.Show(window);
        }
        catch (Exception ex)
        {
            // Window may have been torn down between the user's click
            // and the dispatch — log and bail.
            Debug.WriteLine($"TrayIconService.ShowWindow: {ex}");
            return;
        }

        lock (_gate) { _isHidden = false; }

        if (window is ShellWindow shell)
        {
            shell.BringToFront();
        }
        else
        {
            window.Activate();
        }
    }

    public void HideToTray()
    {
        var window = _shellWindow;
        if (window is null) return;

        if (!_uiDispatcher.HasThreadAccess)
        {
            _uiDispatcher.TryEnqueue(HideToTray);
            return;
        }

        try
        {
            // enableEfficiencyMode=true (default) lets Windows 11 throttle
            // the process while the window is hidden — appropriate since
            // we're explicitly going into background mode.
            WindowExtensions.Hide(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.HideToTray: {ex}");
            return;
        }

        lock (_gate) { _isHidden = true; }
    }

    public void ShowFirstHideHint()
    {
        lock (_gate)
        {
            if (_firstHideHintShown) return;
            _firstHideHintShown = true;
        }

        // Best-effort balloon — uses the same toast pipeline. If
        // notifications failed to register (unpackaged dev) this is a
        // no-op, which is acceptable.
        TryShowToast(
            title: "EvalToolkit is still running",
            body: "EvalToolkit moved to the notification area. Right-click the tray icon and choose Exit to fully quit.",
            jobDirectory: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_genJobsSubscribed)
        {
            try { _genJobs.JobStateChanged -= OnJobStateChanged; } catch { /* swallow */ }
            _genJobsSubscribed = false;
        }
        if (_scoreJobsSubscribed)
        {
            try { _scoreJobs.JobStateChanged -= OnJobStateChanged; } catch { /* swallow */ }
            _scoreJobsSubscribed = false;
        }

        if (_notificationInvokedSubscribed)
        {
            try { AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked; } catch { /* swallow */ }
            _notificationInvokedSubscribed = false;
        }

        if (_notificationsRegistered)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { /* swallow */ }
            _notificationsRegistered = false;
        }

        try { _tray?.Dispose(); } catch { /* swallow */ }
        _tray = null;
    }

    // ----- internals -----

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open EvalToolkit" };
        openItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(openItem);

        var openLastJob = new MenuFlyoutItem { Text = "Open last completed job" };
        openLastJob.Click += (_, _) => OpenLastCompletedJob();
        menu.Items.Add(openLastJob);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => RequestExit();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OpenLastCompletedJob()
    {
        try
        {
            // GPT-5.5 non-blocker: define "Open last job" precisely —
            // latest *completed* job only. Failed / cancelled / in-progress
            // are skipped. If none exists we surface a balloon instead of
            // silently doing nothing.
            var repo = new JobsRepository();
            var latest = repo.ListJobs(_workspaceRoot)
                .FirstOrDefault(j => j.Status == JobStatus.Complete);

            if (latest is null)
            {
                TryShowToast(
                    title: "No completed jobs yet",
                    body: "Generate or score a dataset first; the latest completed job will appear here.",
                    jobDirectory: null);
                return;
            }

            OpenFolderInExplorer(latest.Path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.OpenLastCompletedJob: {ex}");
        }
    }

    private void RequestExit()
    {
        // Slice 28 GPT-5.5 code-review non-blocker: make this idempotent.
        // Two exit clicks (or an Exit click racing with another exit
        // path) must not raise ExitRequested twice — the App handler
        // would otherwise call Application.Exit() against an already-
        // disposed tray.
        lock (_gate)
        {
            if (_isExiting) return;
            _isExiting = true;
        }
        try
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.RequestExit: handler threw {ex}");
        }
    }

    private void OnJobStateChanged(object? sender, JobStateChangedEventArgs e)
    {
        // Only fire on terminal states; in-progress flips are noise for
        // the tray channel (the wizard's progress panel covers them).
        if (e.Status != JobStatus.Complete &&
            e.Status != JobStatus.Failed &&
            e.Status != JobStatus.Cancelled)
        {
            return;
        }

        // Dedupe rapid repeats per GPT-5.5 plan-review guidance.
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (string.Equals(_lastNotifiedJobDir, e.JobDirectory, StringComparison.OrdinalIgnoreCase)
                && now - _lastNotifiedAtUtc < NotificationDedupeWindow)
            {
                return;
            }
            _lastNotifiedJobDir = e.JobDirectory;
            _lastNotifiedAtUtc = now;
        }

        // Skip if the main window is in the foreground — the user is
        // already watching the wizard and doesn't need a duplicate
        // toast. Hidden-to-tray or background → notify.
        if (IsShellWindowForeground())
        {
            return;
        }

        var (title, body) = (e.Kind, e.Status) switch
        {
            (JobKind.Generation, JobStatus.Complete)  => ("Job complete",  $"Eval generation finished: {Path.GetFileName(e.JobDirectory)}"),
            (JobKind.Generation, JobStatus.Failed)    => ("Job failed",    $"Eval generation failed: {Path.GetFileName(e.JobDirectory)}"),
            (JobKind.Generation, JobStatus.Cancelled) => ("Job cancelled", $"Eval generation cancelled: {Path.GetFileName(e.JobDirectory)}"),
            (JobKind.Scoring,    JobStatus.Complete)  => ("Scoring complete",  $"Eval scoring finished: {Path.GetFileName(e.JobDirectory)}"),
            (JobKind.Scoring,    JobStatus.Failed)    => ("Scoring failed",    $"Eval scoring failed: {Path.GetFileName(e.JobDirectory)}"),
            (JobKind.Scoring,    JobStatus.Cancelled) => ("Scoring cancelled", $"Eval scoring cancelled: {Path.GetFileName(e.JobDirectory)}"),
            _ => ("Job state", $"Status {e.Status}: {Path.GetFileName(e.JobDirectory)}"),
        };

        TryShowToast(title, body, e.JobDirectory, isFailure: e.Status == JobStatus.Failed);
    }

    private bool IsShellWindowForeground()
    {
        // Slice 28 GPT-5.5 code-review non-blocker: read the cached
        // HWND so we don't reach back into WinRT (which has thread
        // affinity) from arbitrary job-event threads.
        try
        {
            if (_shellHwnd == IntPtr.Zero) return false;
            return GetForegroundWindow() == _shellHwnd;
        }
        catch
        {
            return false;
        }
    }

    private void TryRegisterNotifications()
    {
        // Slice 31 (winui-native-plus-toasts) BLOCKER #1 from GPT-5.5
        // plan review: subscribe to NotificationInvoked BEFORE Register
        // so the WAS pipeline can never deliver a queued cold-start
        // activation between Register and the handler subscribe. If
        // Register subsequently throws (unpackaged dev without the COM
        // activator), unsubscribe to avoid a dead handler reference.
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            _notificationInvokedSubscribed = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService: subscribing NotificationInvoked failed: {ex.Message}");
            _notificationInvokedSubscribed = false;
            return;
        }

        try
        {
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch (Exception ex)
        {
            // Unpackaged dev runs without the slice-32 MSIX COM
            // activator can fail here; toasts will silently no-op
            // until the packaged build wires up the activator CLSID.
            Debug.WriteLine($"TrayIconService: AppNotificationManager.Register failed (expected for unpackaged dev): {ex.Message}");
            if (_notificationInvokedSubscribed)
            {
                try { AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked; } catch { /* swallow */ }
                _notificationInvokedSubscribed = false;
            }
        }
    }

    private void TryShowToast(string title, string body, string? jobDirectory, bool isFailure = false)
    {
        if (!_notificationsRegistered) return;

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);

            // Slice 31: sticky toast for failures so the user can't
            // miss a build/score error during long-running batches
            // (GPT-5.5 plan-review NON-BLOCKER #4). Default scenario
            // (toast auto-dismisses after a few seconds) for success /
            // cancellation / informational toasts. SetAttribution is
            // deferred to slice 32 packaging where the branded
            // AppLogoOverride asset lives.
            if (isFailure)
            {
                builder = builder.SetScenario(AppNotificationScenario.Reminder);
            }

            if (!string.IsNullOrWhiteSpace(jobDirectory))
            {
                // Encode the action + path on both the toast body
                // (clicking the toast itself) and the button (explicit
                // affordance). Handler parses the args back out via the
                // shared NotificationActionRouter.
                builder = builder
                    .AddArgument("action", "open-job")
                    .AddArgument("path", jobDirectory)
                    .AddButton(new AppNotificationButton("Open job folder")
                        .AddArgument("action", "open-job")
                        .AddArgument("path", jobDirectory));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.TryShowToast: {ex}");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Slice 31: delegate to the shared NotificationActionRouter so
        // warm-start (this handler) and cold-start
        // (App.HandleActivation seeing AppNotificationActivatedEventArgs)
        // both flow through the same dedupe + path-validation pipeline
        // (GPT-5.5 plan-review BLOCKER #3). The router rejects paths
        // outside the workspace root and collapses double-fires within
        // a 2-second window.
        try
        {
            _notificationRouter.Route(args.Arguments, source: "warm-NotificationInvoked");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.OnNotificationInvoked: {ex}");
        }
    }

    private static void OpenFolderInExplorer(string path)
    {
        if (!Directory.Exists(path))
        {
            Debug.WriteLine($"TrayIconService.OpenFolderInExplorer: missing path {path}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TrayIconService.OpenFolderInExplorer: {ex}");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Minimal <see cref="System.Windows.Input.ICommand"/> for wiring
    /// MenuFlyout / TaskbarIcon click → callback without taking a dep
    /// on CommunityToolkit just for this. CanExecute always true;
    /// CanExecuteChanged never fires.
    /// </summary>
    private sealed class RelayDelegateCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayDelegateCommand(Action action) => _action = action;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
