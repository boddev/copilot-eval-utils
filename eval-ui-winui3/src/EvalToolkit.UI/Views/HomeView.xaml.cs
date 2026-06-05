using EvalToolkit.Core;
using Microsoft.UI.Xaml.Controls;

namespace EvalToolkit.UI.Views;

/// <summary>
/// Placeholder landing page for slice 21. Confirms the shell is alive,
/// MVVM scaffolding is in place, and displays the engine version so the
/// developer can see at a glance which build of EvalToolkit they're
/// running against. Replaced by the dataset picker in slice 22.
/// </summary>
public sealed partial class HomeView : Page
{
    public HomeView()
    {
        InitializeComponent();
        VersionText.Text = $"engine version: {CoreInfo.Version}";
    }
}
