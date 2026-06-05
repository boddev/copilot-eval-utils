using EvalToolkit.UI.Services;
using EvalToolkit.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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
    public string WorkspaceRoot { get; private set; } = null!;

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
        // can swap to an imported workspace via a first-run wizard.
        WorkspaceRoot = EvalGenJobService.DefaultWorkspaceRoot();
        JobService = new EvalGenJobService();

        // Slice 22 starts on the dataset-picker wizard. Slice 26
        // (jobs sidebar) replaces this with a job-list landing page
        // that opens the wizard via "New evaluation".
        Navigation.Register("Wizard", typeof(Views.WizardView));
        Navigation.NavigateTo(typeof(Views.WizardView));
        ShellWindow.Activate();

        // Drain any activations that arrived between Program.Main
        // hooking primary.Activated and OnLaunched finishing shell
        // construction. Slice 21 only needs to bring the window to
        // front; per-payload routing (file/protocol/jump-list verbs)
        // arrives with the FTA slice.
        foreach (var pending in ActivationQueue.Drain())
        {
            HandleActivation(pending);
        }
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
        ShellWindow?.BringToFront();
        _ = args;
    }
}

