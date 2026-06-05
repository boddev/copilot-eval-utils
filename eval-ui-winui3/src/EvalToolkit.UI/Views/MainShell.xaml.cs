using EvalToolkit.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EvalToolkit.UI.Views;

/// <summary>
/// Shell page hosting the jobs sidebar (left) and a content
/// <see cref="Frame"/> (right) for the wizard / future detail views.
/// Owns the long-lived <see cref="MainShellViewModel"/> from
/// <see cref="App"/>.
/// </summary>
public sealed partial class MainShell : Page
{
    public MainShellViewModel ViewModel { get; }

    public MainShell()
    {
        InitializeComponent();
        ViewModel = App.Current.MainShell!;
        DataContext = ViewModel;

        // Assign ItemsSource from code-behind to side-step a WMC9999
        // XAML-compiler crash with x:Bind to ObservableCollection on
        // certain WindowsAppSDK 2.1 builds when combined with
        // DataTemplate x:DataType in the same ListView.
        JobsList.ItemsSource = ViewModel.Sidebar.Jobs;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnSidebarPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(JobsSidebarViewModel.HasJobs))
        {
            UpdateEmptyHint();
        }
    }

    private void UpdateEmptyHint()
    {
        // Inline visibility update instead of x:Bind binding so we don't
        // need a converter / INotifyPropertyChanged hookup just for one
        // text block.
        var empty = !ViewModel.Sidebar.HasJobs ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyHintText is not null)
        {
            EmptyHintText.Visibility = empty;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Register the wizard route now that the shell's frame exists,
        // then navigate. App.OnLaunched previously did this directly
        // against ShellWindow.RootFrame; in the new shell-based layout
        // the wizard lives inside MainShell's ContentFrame.
        App.Current.Navigation.Rebind(ContentFrame);
        App.Current.Navigation.Register("Wizard", typeof(WizardView));
        App.Current.Navigation.NavigateTo(typeof(WizardView));

        ViewModel.Sidebar.PropertyChanged += OnSidebarPropertyChanged;

        // Kick off initial population so the user sees any pre-existing
        // jobs from prior sessions immediately.
        await ViewModel.Sidebar.RefreshAsync();
        UpdateEmptyHint();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // GPT-5.5 review #3: prevent handler leak if MainShell is
        // recreated (the sidebar VM is long-lived in App).
        ViewModel.Sidebar.PropertyChanged -= OnSidebarPropertyChanged;
    }

    private void JobsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JobSummaryViewModel job)
        {
            ViewModel.Sidebar.OpenJobFolderCommand.Execute(job);
        }
    }

    private void JobsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe &&
            fe.DataContext is JobSummaryViewModel job)
        {
            var menu = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = "Open folder" };
            open.Click += (_, _) => ViewModel.Sidebar.OpenJobFolderCommand.Execute(job);
            menu.Items.Add(open);

            var reveal = new MenuFlyoutItem { Text = "Reveal job.json" };
            reveal.Click += (_, _) => ViewModel.Sidebar.RevealJobFolderCommand.Execute(job);
            menu.Items.Add(reveal);

            menu.ShowAt(fe, e.GetPosition(fe));
            e.Handled = true;
        }
    }
}
