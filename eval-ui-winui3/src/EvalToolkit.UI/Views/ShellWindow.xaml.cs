using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace EvalToolkit.UI.Views;

/// <summary>
/// Top-level window that hosts the navigation Frame and serves as the
/// drag region / system backdrop target. Slice-21 scope: just the Frame
/// and an "ExtendsContentIntoTitleBar" title surface so Mica reads
/// correctly. Per-view chrome and the breadcrumb bar are added in
/// later slices.
/// </summary>
public sealed partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();

        // Extend client area into the title bar so the system backdrop
        // (Mica) paints continuously behind both. The legacy Win32 title
        // bar would otherwise leave a strip of solid color above the
        // backdrop.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Title = "EvalToolkit";

        // A reasonable default size; the user's last position is restored
        // by a window-state service introduced in winui-jobs-history.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

        // Slice 28 originally rerouted the window X to hide-to-tray. The
        // app now exits on close per user request: cancel this close and
        // request a graceful, full shutdown on the next dispatcher turn.
        // Enqueueing avoids re-entering this handler synchronously while
        // Application.Exit() (driven by ExitRequested) closes the window;
        // that re-raised Closing sees IsExiting==true above and proceeds.
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        var tray = App.Current.Tray;
        if (tray is null || tray.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        DispatcherQueue.TryEnqueue(tray.RequestExit);
    }

    /// <summary>
    /// Direct access to the root navigation frame. <c>NavFrame</c> is
    /// declared in XAML with <c>x:Name="NavFrame"</c>; the XAML
    /// compiler emits a strongly-typed field of the same name on the
    /// generated partial class.
    /// </summary>
    public Frame RootFrame => NavFrame;

    /// <summary>
    /// Forward HWND helper for theme service + activation routing.
    /// </summary>
    public IntPtr HWnd => WindowNative.GetWindowHandle(this);

    /// <summary>
    /// Brings the window forward in response to a second-instance
    /// activation. Restores from minimized, brings to foreground.
    /// </summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        // Activate is the documented same-process foreground call for
        // WinAppSDK windows; bypasses Win32 SetForegroundWindow's focus
        // stealing heuristics.
        Activate();
        AppWindow.MoveInZOrderAtTop();
    }
}
