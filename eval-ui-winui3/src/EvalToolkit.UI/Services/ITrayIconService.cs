using System;
using Microsoft.UI.Xaml;

namespace EvalToolkit.UI.Services;

/// <summary>
/// System-tray (notification-area) integration for the WinUI 3 shell.
/// Slice 28 (winui-native-plus-tray) introduces this service so the
/// application can keep running in the tray after the user dismisses
/// the main window, surface terminal-status toasts for background
/// jobs, and offer a context-menu quit affordance.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: created once in <see cref="App.OnLaunched"/> after the
/// shell window is constructed (so the service can intercept the
/// window's <see cref="Microsoft.UI.Windowing.AppWindow.Closing"/>
/// event and hide-to-tray instead of exiting). Disposed when the user
/// chooses <em>Exit</em> from the tray menu — see
/// <see cref="ExitRequested"/>.
/// </para>
/// <para>
/// GPT-5.5 plan-review (slice 28) flagged four blockers; all are
/// addressed by this interface contract:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>Close</c> interception must allow a real exit when the user picks
/// <em>Exit</em> from the tray. The shell window's
/// <see cref="Microsoft.UI.Windowing.AppWindow.Closing"/> handler
/// checks <see cref="IsExiting"/> and only cancels the close while
/// <c>false</c>.
/// </description></item>
/// <item><description>
/// <see cref="ShowWindow"/> calls <see cref="Window.Activate"/> +
/// <c>AppWindow.Show()</c> + foreground promotion so a hidden window
/// becomes visible on tray-left-click and single-instance
/// reactivation.
/// </description></item>
/// <item><description>
/// Toast routing: <see cref="TrayIconService"/> registers a
/// <c>NotificationInvoked</c> handler that parses the toast's
/// <c>action=open-job</c> + <c>path=...</c> arguments and brings the
/// job folder into focus.
/// </description></item>
/// <item><description>
/// <see cref="IDisposable.Dispose"/> unsubscribes from job-service
/// events and tears down the native <c>TaskbarIcon</c> resources
/// rather than leaving cleanup to process teardown.
/// </description></item>
/// </list>
/// </remarks>
public interface ITrayIconService : IDisposable
{
    /// <summary>
    /// Wires the tray icon to the shell window and starts listening
    /// for job-service terminal-status events. Must be called exactly
    /// once after the window is constructed and activated.
    /// </summary>
    void Initialize(Window shellWindow);

    /// <summary>
    /// Brings the shell window back from a hidden / minimized state
    /// and gives it focus.
    /// </summary>
    void ShowWindow();

    /// <summary>
    /// Hides the shell window into the tray. The process keeps
    /// running; the tray icon remains the only UI affordance until
    /// <see cref="ShowWindow"/> is called.
    /// </summary>
    void HideToTray();

    /// <summary>True iff the shell window is currently hidden in the tray.</summary>
    bool IsWindowHidden { get; }

    /// <summary>
    /// True iff the user has chosen <em>Exit</em> from the tray menu
    /// and the application is in the middle of shutting down. The
    /// shell window's close handler reads this to decide whether to
    /// cancel the close (normal X click → hide to tray) or let it
    /// proceed (Exit → real shutdown).
    /// </summary>
    bool IsExiting { get; }

    /// <summary>
    /// Raised when the user picks <em>Exit</em> from the tray context
    /// menu. The handler is responsible for finishing the shutdown
    /// (typically calling <see cref="Application.Exit"/>).
    /// </summary>
    event EventHandler? ExitRequested;

    /// <summary>
    /// On first hide-to-tray, surfaces a one-time balloon explaining
    /// that the application is still running and how to fully exit.
    /// Subsequent calls are no-ops. State is in-memory only for slice
    /// 28; a future settings slice can persist the suppression flag.
    /// </summary>
    void ShowFirstHideHint();
}
