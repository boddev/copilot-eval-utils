using System.Collections.Generic;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Centralizes routing for toast-notification activation arguments.
/// Slice 31 (winui-native-plus-toasts) extracted this from
/// <see cref="TrayIconService"/> so the App's cold-start activation
/// path (<see cref="App.HandleActivation"/> when
/// <c>args.Data is AppNotificationActivatedEventArgs</c>) and the
/// warm-start <see cref="Microsoft.Windows.AppNotifications.AppNotificationManager.NotificationInvoked"/>
/// handler can share a single dedupe + path-validation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// GPT-5.5 slice-31 plan-review BLOCKER #3: cold-start notification
/// activations arrive through both <c>AppActivationArguments</c> (the
/// activation pipeline) AND <c>NotificationInvoked</c> (the manager's
/// in-process event) when subscribe-before-register is honored. Routing
/// both through a single helper with timestamp-based dedupe collapses
/// the double-fire into a single Explorer-open without dropping
/// either source.
/// </para>
/// <para>
/// Path validation (NON-BLOCKER #10): only paths that resolve under
/// the configured workspace root are routed. Toast args are essentially
/// user-controlled (a tampered notification.xml could contain a
/// poisoned payload), so the router refuses anything that resolves
/// outside the workspace tree.
/// </para>
/// </remarks>
public interface INotificationActionRouter
{
    /// <summary>
    /// Dispatch the toast action described by <paramref name="arguments"/>.
    /// Returns <c>true</c> when an action handler consumed the args,
    /// <c>false</c> when the args were rejected (validation failure,
    /// dedupe drop, or unknown action).
    /// </summary>
    /// <param name="arguments">
    /// The argument dictionary from the activated toast — keys typically
    /// include <c>action</c> and (for <c>action=open-job</c>) <c>path</c>.
    /// </param>
    /// <param name="source">
    /// Human-readable source label for diagnostics. Use values like
    /// <c>"warm-NotificationInvoked"</c> or <c>"cold-AppActivation"</c>
    /// so dedupe drops can be traced.
    /// </param>
    bool Route(IDictionary<string, string> arguments, string source);
}
