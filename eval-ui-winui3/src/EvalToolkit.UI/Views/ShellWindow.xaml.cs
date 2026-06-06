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

        // Slice 28: route the window X click through the tray service
        // so the app stays alive in the notification area. The tray's
        // Exit menu sets Tray.IsExiting before raising ExitRequested,
        // and App.OnTrayExitRequested calls Application.Exit() — at
        // that point Closing fires again with IsExiting=true and we
        // let it proceed.
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        // Tray may be null in very early teardown or if a future refactor
        // bypasses initialization; in that case allow the close to
        // proceed so the app can still exit normally.
        var tray = App.Current.Tray;
        if (tray is null || tray.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        tray.HideToTray();
        tray.ShowFirstHideHint();
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
