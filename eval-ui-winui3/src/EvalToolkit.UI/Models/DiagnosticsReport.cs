using System;

namespace EvalToolkit.UI.Models;

/// <summary>
/// Health classification for a diagnostics section or the overall
/// report. CI consumers map these to exit codes (Green/Yellow → 0,
/// Red → 1). The UI uses them to pick status glyph + color.
/// </summary>
/// <remarks>
/// GPT-5.5 slice-diagnostics plan-review NON-BLOCKER #5: encoding
/// health in the model means consumers don't have to duplicate
/// app-specific rules like "Notifications=false is okay in unpackaged
/// dev". The producer records the policy here once.
/// </remarks>
public enum DiagnosticsHealth
{
    /// <summary>All checks passed. CI exit 0.</summary>
    Green = 0,
    /// <summary>Non-blocking issue (e.g. notifications unregistered in unpackaged dev). CI exit 0.</summary>
    Yellow = 1,
    /// <summary>Blocking issue (e.g. workspace not writable, WebView2 missing AND no bundled installer). CI exit 1.</summary>
    Red = 2,
}

/// <summary>
/// Snapshot of one diagnostics collection. Snapshots are immutable;
/// the UI rebinds to a fresh snapshot when the user clicks Refresh.
/// </summary>
public sealed record DiagnosticsReport(
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    WorkspaceStatus Workspace,
    WebView2Status WebView2,
    NotificationsStatus Notifications,
    JumpListStatus JumpList,
    ProcessStatus Process)
{
    /// <summary>
    /// Computed worst-of-sections health. Determines the CI exit code
    /// when the report is produced by the headless <c>--diagnostics</c>
    /// path.
    /// </summary>
    public DiagnosticsHealth OverallHealth
    {
        get
        {
            var worst = DiagnosticsHealth.Green;
            if (Workspace.Health > worst) worst = Workspace.Health;
            if (WebView2.Health > worst) worst = WebView2.Health;
            if (Notifications.Health > worst) worst = Notifications.Health;
            if (JumpList.Health > worst) worst = JumpList.Health;
            if (Process.Health > worst) worst = Process.Health;
            return worst;
        }
    }
}

public sealed record WorkspaceStatus(
    string Path,
    bool Exists,
    bool Writable,
    bool Creatable,
    DiagnosticsHealth Health,
    string? Note);

public sealed record WebView2Status(
    bool RuntimeAvailable,
    bool BundledInstallerPresent,
    string BundledInstallerPath,
    string ManualInstallerUrl,
    DiagnosticsHealth Health,
    string? Note);

public sealed record NotificationsStatus(
    bool Registered,
    DiagnosticsHealth Health,
    string Note);

public sealed record JumpListStatus(
    bool Initialized,
    bool LastRefreshSucceeded,
    DateTimeOffset? LastRefreshUtc,
    DiagnosticsHealth Health,
    string? Note);

public sealed record ProcessStatus(
    int Pid,
    string ExePath,
    string ConfiguredAumid,
    string? ActualAumid,
    string? AumidError,
    DiagnosticsHealth Health);
