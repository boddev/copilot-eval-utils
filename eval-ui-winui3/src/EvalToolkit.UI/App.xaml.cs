using EvalToolkit.UI.Services;
using EvalToolkit.UI.ViewModels;
using EvalToolkit.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EvalToolkit.Jobs;

namespace EvalToolkit.UI;

/// <summary>
/// Singleton XAML application. Owns the shell window, navigation, and
/// theme services. Construction runs on the UI thread inside
/// <see cref="Program.Main"/> via <see cref="Application.Start"/>.
/// </summary>
public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public ShellWindow? ShellWindow { get; private set; }
    public NavigationService Navigation { get; private set; } = null!;
    public ThemeService Theme { get; private set; } = null!;
    public IFileDialogService FileDialog { get; private set; } = null!;
    public IEvalGenJobService JobService { get; private set; } = null!;
    public IEvalScoreJobService ScoreService { get; private set; } = null!;
    public IWebView2RuntimeService WebView2Runtime { get; private set; } = null!;
    public ITrayIconService Tray { get; private set; } = null!;
    public IJumpListService JumpList { get; private set; } = null!;
    public IFileActivationRouter Router { get; private set; } = null!;
    public INotificationActionRouter NotificationRouter { get; private set; } = null!;
    public IDiagnosticsService DiagnosticsService { get; private set; } = null!;
    public DiagnosticsViewModel? Diagnostics { get; private set; }
    public MainShellViewModel? MainShell { get; private set; }
    public string WorkspaceRoot { get; private set; } = null!;

    // Slice 29 (GPT-5.5 code review BLOCKER #2): jump-list / activation
    // verbs may arrive in OnLaunched BEFORE MainShell.OnLoaded has had
    // a chance to register the "Wizard" route with NavigationService.
    // Queue verbs that need navigation until MainShell drains them via
    // DrainPendingVerbs(). Verbs that don't depend on navigation
    // (--job-id) execute immediately. Single-threaded UI-thread access
    // — no lock needed.
    private readonly System.Collections.Generic.Queue<string> _pendingVerbs = new();

    // Nullable because OnReactivation can fire on a background thread
    // between Application construction and OnLaunched assigning the
    // dispatcher. Treat null as "shell not ready" and enqueue.
    public DispatcherQueue? UiDispatcher { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiDispatcher = DispatcherQueue.GetForCurrentThread();
        ShellWindow = new ShellWindow();
        Navigation = new NavigationService(ShellWindow.RootFrame);
        Theme = new ThemeService(ShellWindow);
        Theme.Apply(ShellWindow);

        // Slice 22: the file dialog service is created with a lazy
        // HWND provider so the VM never touches a Window. Routing the
        // shell window's handle here keeps pickers anchored on the
        // currently-foreground main window (no child popups yet).
        FileDialog = new FileDialogService(() => ShellWindow!.HWnd);

        // Slice 23: workspace root + job service. Default workspace lives
        // under %LOCALAPPDATA%\EvalToolkit\workspace; slice 24 (jobs sidebar)
        // can swap to an imported workspace via a first-run wizard. Note:
        // DefaultWorkspaceRoot is now side-effect-free (slice 24 GPT-5.5
        // finding #8) — the directory is created lazily on first job.
        WorkspaceRoot = EvalGenJobService.DefaultWorkspaceRoot();
        var jobService = new EvalGenJobService();
        JobService = jobService;
        // Slice 26: companion service for Step 5 (score panel). Default
        // CLI/A2A WorkIQ clients; the wizard's RunScore command builds
        // the request from ScoreViewModel form state.
        ScoreService = new EvalScoreJobService();

        // Slice 27: WebView2 runtime detection + bundled Evergreen
        // Bootstrapper. Singleton so the in-process "available" cache
        // is shared; the detection is cheap (static API), but keeping
        // one instance lets us swap in a fake for diagnostics.
        WebView2Runtime = new WebView2RuntimeService();

        // Slice 24: shell + jobs sidebar. MainShellViewModel owns the
        // long-lived sidebar VM (so its job list survives navigation),
        // and subscribes to the job service's JobStateChanged event for
        // auto-refresh on both start and terminal states.
        var sidebar = new JobsSidebarViewModel(
            new EvalToolkit.Jobs.JobsRepository(),
            WorkspaceRoot,
            jobService,
            UiDispatcher);
        MainShell = new MainShellViewModel(sidebar);

        // Slice 22 starts on the dataset-picker wizard. Slice 24 wraps
        // the wizard in a MainShell page that hosts a jobs sidebar to
        // the left. Routes are re-registered against MainShell's
        // inner Frame by MainShell.OnLoaded.
        Navigation.NavigateTo(typeof(Views.MainShell));
        ShellWindow.Activate();

        // Slice 28: system-tray integration. Initialize AFTER
        // ShellWindow.Activate() so AppWindow is non-null and the tray
        // can hook the window for show/hide. Tray subscribes to BOTH
        // job services' JobStateChanged events (gen + score) so
        // background-completion toasts fire regardless of pipeline
        // (GPT-5.5 code-review blocker). ExitRequested → Application.Exit()
        // so the tray menu can actually shut the app down (the
        // ShellWindow Closing handler reroutes window X clicks to
        // hide-to-tray).
        // Slice 31: build NotificationRouter BEFORE Tray so the Tray
        // can share it with the App-level cold-start activation
        // handler — both paths must dedupe through one instance to
        // prevent double-open of the job folder.
        NotificationRouter = new NotificationActionRouter(WorkspaceRoot);
        Tray = new TrayIconService(JobService, ScoreService, WorkspaceRoot, UiDispatcher, NotificationRouter);
        Tray.ExitRequested += OnTrayExitRequested;
        Tray.Initialize(ShellWindow);

        // Slice 29: jump-list integration. Subscribes to both job
        // services so the "Recent jobs" category stays fresh as work
        // completes, and registers a "New evaluation" user task. Built
        // on Win32 ICustomDestinationList so it works in BOTH
        // unpackaged dev and packaged MSIX runs (slice 30). Initial
        // refresh kicks off here; it's fire-and-forget — a slow shell
        // call must not delay first paint.
        JumpList = new JumpListService(
            new EvalToolkit.Jobs.JobsRepository(),
            WorkspaceRoot,
            UiDispatcher);
        JumpList.Initialize(JobService, ScoreService);
        _ = JumpList.RefreshAsync();

        // Slice 32 (winui-diagnostics): construct the live diagnostics
        // service AFTER Tray and JumpList so the GUI report reflects
        // the actual running state of those subsystems rather than
        // headless probe results.
        DiagnosticsService = new DiagnosticsService(
            WorkspaceRoot,
            JumpListService.DefaultAppId,
            WebView2Runtime,
            Tray,
            JumpList);
        Diagnostics = new DiagnosticsViewModel(DiagnosticsService);

        // Slice 30: file-type-association routing. Owns the dispatch
        // logic from "an MSIX FTA fired" or "--open-file <path> was
        // passed on the command line" to the right wizard step (sidecar
        // → Step 4 editor) or the right system handler (legacy CSV /
        // MD → default app). Constructed last so it can hand off to
        // Navigation safely; the cold-start race (route not yet
        // registered) is handled by the same _pendingVerbs queue that
        // slice 29 introduced for --new-evaluation.
        Router = new FileActivationRouter(
            Navigation,
            ShellOpener.OpenFile,
            warning => Debug.WriteLine($"FileActivationRouter: {warning}"));

        // Drain any activations that arrived between Program.Main
        // hooking primary.Activated and OnLaunched finishing shell
        // construction. Slice 21 only needs to bring the window to
        // front; per-payload routing (file/protocol/jump-list verbs)
        // arrives with the FTA slice.
        foreach (var pending in ActivationQueue.Drain())
        {
            HandleActivation(pending);
        }

        // Slice 31 (winui-native-plus-toasts) BLOCKER #1 from GPT-5.5
        // plan review: read the cold-start AppActivationArguments here
        // (AFTER Tray.Initialize's TryRegisterNotifications subscribed
        // + Register'd), per WAS guidance that Register must precede
        // GetActivatedEventArgs for notification activations. This is
        // the cold-start path for kinds OTHER than Launch — AppNotification
        // (toast clicked while app was closed), File (Explorer FTA
        // double-click while app was closed), and Protocol. The Launch
        // kind is delivered by the OnLaunched args.Arguments path
        // below, so we skip it here to avoid double-routing.
        try
        {
            var coldStart = Microsoft.Windows.AppLifecycle.AppInstance
                .GetCurrent()
                .GetActivatedEventArgs();
            if (coldStart is not null && coldStart.Kind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
            {
                HandleActivation(coldStart);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"App.OnLaunched: cold-start activation read failed: {ex}");
        }

        // Slice 29: also route this launch's own arguments, so launching
        // EvalToolkit.UI.exe directly with --job-id or --new-evaluation
        // (jump-list cold-start case) lands the verb instead of just
        // opening the wizard.
        if (!string.IsNullOrWhiteSpace(args.Arguments))
        {
            HandleVerb(args.Arguments);
        }
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        // Tray "Exit" was picked. ShellWindow.Closing checks
        // Tray.IsExiting before cancelling the close, so this Exit
        // call cleanly tears the app down. Dispose the tray first so
        // the icon goes away immediately even if XAML shutdown stalls.
        try { Tray?.Dispose(); } catch { /* swallow */ }
        try { JumpList?.Dispose(); } catch { /* swallow */ }
        Exit();
    }

    /// <summary>
    /// Wired in <see cref="Program.DecideSingleInstance"/> via
    /// <c>primary.Activated += App.OnReactivation</c>. Re-activations
    /// (file open from Explorer, jump-list verb, second launch) land
    /// here on a background thread; we marshal onto the UI thread and
    /// either route immediately (shell ready) or enqueue for
    /// <see cref="OnLaunched"/> to drain (shell still spinning up).
    /// </summary>
    public static void OnReactivation(object? sender, AppActivationArguments args)
    {
        var app = Current;

        // GPT-5.5 slice-21 round-2 finding #1: treat "Application
        // exists but OnLaunched hasn't run yet" as not-ready and
        // enqueue. UiDispatcher is null between `new App()` and the
        // first line of OnLaunched, which is a real race when a file
        // association activates during cold start.
        if (app is null || app.UiDispatcher is null || app.ShellWindow is null)
        {
            ActivationQueue.Enqueue(args);
            return;
        }

        if (!app.UiDispatcher.TryEnqueue(() =>
        {
            // Re-check shell readiness on the UI thread in case the
            // window was torn down between the enqueue and the marshal.
            if (app.ShellWindow is null)
            {
                ActivationQueue.Enqueue(args);
                return;
            }
            app.HandleActivation(args);
        }))
        {
            // DispatcherQueue refused the work item (shutting down or
            // saturated) — fall back to the queue so OnLaunched (if
            // still pending) or a future drain can pick it up.
            ActivationQueue.Enqueue(args);
        }
    }

    private void HandleActivation(AppActivationArguments args)
    {
        // Slice 21: bring window forward; future slices parse args.Kind
        // (File, Protocol, Launch...) and route to the appropriate view.
        // Slice 28: if the user previously hid-to-tray, BringToFront alone
        // won't restore visibility — delegate to Tray.ShowWindow which
        // calls AppWindow.Show() first.
        if (Tray is not null && Tray.IsWindowHidden)
        {
            Tray.ShowWindow();
        }
        else
        {
            ShellWindow?.BringToFront();
        }

        // Slice 29: route jump-list verbs (--job-id, --new-evaluation).
        // Slice 30: route file activations via the --open-file synthetic
        // verb so the queue / replay path is shared.
        // Slice 31: route toast-notification activations via the shared
        // NotificationActionRouter so warm-start NotificationInvoked
        // and cold-start AppNotificationActivatedEventArgs both flow
        // through the same dedupe + path-validation pipeline.
        // For Launch activations the verb arrives as a single Arguments
        // string on Microsoft.Windows.AppLifecycle.Activation.ILaunchActivatedEventArgs.
        // For File activations the path list arrives on IFileActivatedEventArgs.
        try
        {
            if (args.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notif)
            {
                if (NotificationRouter is null)
                {
                    Debug.WriteLine("App.HandleActivation(notif): NotificationRouter not initialized");
                    return;
                }
                NotificationRouter.Route(notif.Arguments, source: "cold-AppActivation");
                return;
            }

            if (args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            {
                var files = fileArgs.Files;
                if (files is not null && files.Count > 0)
                {
                    // GPT-5.5 slice-30 plan-review answer #5: first file
                    // only, log a warning when more were activated.
                    // Multi-doc wizard UX is out-of-scope for slice 30.
                    if (files.Count > 1)
                    {
                        Debug.WriteLine(
                            $"App.HandleActivation: file activation with {files.Count} files; routing only the first.");
                    }
                    var first = files[0];
                    string? path = (first as Windows.Storage.IStorageItem)?.Path;
                    if (!string.IsNullOrEmpty(path))
                    {
                        // Synthesize an --open-file verb so the queue/
                        // replay path is shared with cold-start verbs.
                        // Quote the path to survive CommandLineToArgvW
                        // re-parsing when spaces are present.
                        HandleVerb($"--open-file \"{path}\"");
                    }
                }
                return;
            }

            if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                && !string.IsNullOrWhiteSpace(launch.Arguments))
            {
                HandleVerb(launch.Arguments);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"App.HandleActivation: verb parse failed: {ex}");
        }
    }

    internal void HandleVerb(string commandLine)
    {
        HandleVerb(commandLine, allowQueueing: true);
    }

    /// <summary>
    /// Parses an EvalToolkit.UI command-line / activation argument
    /// string for slice 29's jump-list verbs and routes accordingly.
    /// Unknown / empty input is a no-op (the activation has already
    /// brought the window forward). When <paramref name="allowQueueing"/>
    /// is true and a verb requires navigation but the "Wizard" route
    /// isn't registered yet (race with MainShell.OnLoaded), the verb
    /// is queued for replay via <see cref="DrainPendingVerbs"/>.
    /// </summary>
    private void HandleVerb(string commandLine, bool allowQueueing)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return;

        string[] tokens;
        try
        {
            tokens = ParseCommandLine(commandLine);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"App.HandleVerb: CommandLineToArgvW failed: {ex}");
            return;
        }
        if (tokens.Length == 0) return;

        switch (tokens[0])
        {
            case "--new-evaluation":
                try
                {
                    bool navigated = Navigation?.NavigateTo("Wizard") ?? false;
                    if (!navigated && allowQueueing)
                    {
                        // GPT-5.5 BLOCKER #2: route may not be registered
                        // yet during cold start. Re-queue so MainShell
                        // drains it once OnLoaded registers "Wizard".
                        _pendingVerbs.Enqueue(commandLine);
                    }
                    else if (!navigated)
                    {
                        // Replayed via DrainPendingVerbs and STILL failed
                        // — drop to avoid an infinite loop, but log so
                        // the drop is debuggable (slice-30 review NON-BLOCKER #7).
                        Debug.WriteLine("App.HandleVerb(new-evaluation): navigation failed on replay; dropping verb.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"App.HandleVerb(new-evaluation): {ex}");
                }
                break;

            case "--job-id" when tokens.Length >= 2:
                var jobId = tokens[1];
                try
                {
                    var repo = new EvalToolkit.Jobs.JobsRepository();
                    var match = repo
                        .ListJobs(WorkspaceRoot)
                        .FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                    if (match is null || !Directory.Exists(match.Path))
                    {
                        Debug.WriteLine($"App.HandleVerb(job-id): no match for '{jobId}'");
                        break;
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = match.Path,
                        UseShellExecute = true,
                        Verb = "open",
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"App.HandleVerb(job-id): {ex}");
                }
                break;

            case "--open-file" when tokens.Length >= 2:
                // Slice 30: dispatch file-type-association activations.
                // Same cold-start race as --new-evaluation: if the
                // Wizard route isn't registered yet, queue for replay
                // via DrainPendingVerbs (called by MainShell.OnLoaded).
                if (Router is null)
                {
                    Debug.WriteLine("App.HandleVerb(open-file): Router not initialized");
                    break;
                }
                try
                {
                    bool dispatched = Router.Route(tokens[1], out bool needsQueue);
                    if (!dispatched && needsQueue && allowQueueing)
                    {
                        _pendingVerbs.Enqueue(commandLine);
                    }
                    else if (!dispatched && needsQueue)
                    {
                        // Replayed via DrainPendingVerbs and the Wizard
                        // route STILL isn't registered — drop to avoid
                        // an infinite loop, but log the path so the
                        // drop is debuggable (slice-30 review NON-BLOCKER #7).
                        Debug.WriteLine(
                            $"App.HandleVerb(open-file): navigation failed on replay for '{tokens[1]}'; dropping verb.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"App.HandleVerb(open-file): {ex}");
                }
                break;

            default:
                // Unknown verb: ignore so a future arg surface doesn't
                // crash older clients.
                break;
        }
    }

    /// <summary>
    /// Called by <see cref="MainShell.OnLoaded"/> after it has
    /// registered the "Wizard" route with NavigationService. Replays
    /// any verbs that arrived in OnLaunched before the route existed.
    /// Single-threaded UI-thread call.
    /// </summary>
    internal void DrainPendingVerbs()
    {
        while (_pendingVerbs.Count > 0)
        {
            var verb = _pendingVerbs.Dequeue();
            // allowQueueing=false: if it STILL fails, log and drop —
            // don't infinite-loop. Should not happen post-OnLoaded.
            HandleVerb(verb, allowQueueing: false);
        }
    }

    private static readonly char[] s_cmdSeparators = new[] { ' ', '\t' };

    /// <summary>
    /// Tokenize a Windows command-line string using the native
    /// CommandLineToArgvW. Falls back to a naive split on whitespace
    /// if the shell isn't available (extremely defensive — the verb
    /// strings produced by JumpListService have no spaces or quotes).
    /// </summary>
    private static string[] ParseCommandLine(string commandLine)
    {
        IntPtr argv = JumpListInterop.CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero)
        {
            return commandLine.Split(s_cmdSeparators, StringSplitOptions.RemoveEmptyEntries);
        }
        try
        {
            var tokens = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                tokens[i] = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            }
            return tokens;
        }
        finally
        {
            JumpListInterop.LocalFree(argv);
        }
    }
}

