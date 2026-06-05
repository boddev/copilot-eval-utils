using System;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Owns the long-lived <see cref="JobsSidebarViewModel"/> so the
/// sidebar list survives navigation away from / back to the wizard
/// page. The wizard itself stays in its own VM created by
/// <see cref="Views.WizardView"/> — keeping wizard state out of the
/// shell so navigation logic doesn't have to thread it through.
/// </summary>
public sealed class MainShellViewModel
{
    public MainShellViewModel(JobsSidebarViewModel sidebar)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        Sidebar = sidebar;
    }

    public JobsSidebarViewModel Sidebar { get; }
}
