using System;
using System.ComponentModel;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EvalToolkit.UI.Views;

/// <summary>
/// Renders a <see cref="DiagnosticsReport"/> snapshot as a stack of
/// per-subsystem cards. Computed text properties live here (not on the
/// VM) so the VM stays a pure
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/> shell
/// around <see cref="IDiagnosticsService"/>.
/// </summary>
public sealed partial class DiagnosticsView : Page
{
    public DiagnosticsViewModel ViewModel { get; private set; } = null!;

    public DiagnosticsView()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = App.Current.Diagnostics
            ?? throw new InvalidOperationException("App.Diagnostics not initialized.");
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Trigger initial collection so the view is populated on first open.
        if (ViewModel.Report is null && !ViewModel.IsRefreshing)
        {
            try { await System.Threading.Tasks.Task.Yield(); ViewModel.RefreshCommand.Execute(null); } catch { /* ignore */ }
        }
        RaiseAll();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any VM change → re-evaluate every computed display property.
        // The grid is small (six cards) so blanket invalidation is fine.
        //
        // GPT-5.5 slice-32 code-review non-blocker #4: marshal to the
        // UI thread defensively. The current `RefreshAsync` path uses
        // `ConfigureAwait(true)` so the VM raises PropertyChanged on the
        // UI thread today, but future callers (e.g. background poll, a
        // service-driven refresh) could fire from a worker. Calling
        // `Bindings.Update()` off-thread throws RPC_E_WRONG_THREAD.
        var dq = DispatcherQueue;
        if (dq is null || dq.HasThreadAccess)
        {
            RaiseAll();
        }
        else
        {
            dq.TryEnqueue(RaiseAll);
        }
    }

    private void RaiseAll()
    {
        Bindings.Update();
    }

    public bool HasReport => ViewModel?.Report is not null;
    public bool HasError => !string.IsNullOrEmpty(ViewModel?.ErrorMessage);

    public string OverallHeader
    {
        get
        {
            var r = ViewModel?.Report;
            if (r is null) return "Awaiting probe…";
            return r.OverallHealth switch
            {
                DiagnosticsHealth.Green => "All subsystems healthy",
                DiagnosticsHealth.Yellow => "Non-blocking warnings",
                _ => "Blocking failures detected",
            };
        }
    }

    public string OverallGlyph => GlyphFor(ViewModel?.Report?.OverallHealth ?? DiagnosticsHealth.Yellow);

    public string GeneratedAtDisplay => ViewModel?.Report is { } r
        ? $"Generated: {r.GeneratedAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
        : string.Empty;

    public string AppVersionDisplay => ViewModel?.Report is { } r
        ? $"App version: {r.AppVersion}"
        : string.Empty;

    public string WorkspaceLine1
    {
        get
        {
            var w = ViewModel?.Report?.Workspace;
            if (w is null) return string.Empty;
            return $"{GlyphFor(w.Health)}  {w.Path}";
        }
    }

    public string WorkspaceLine2
    {
        get
        {
            var w = ViewModel?.Report?.Workspace;
            if (w is null) return string.Empty;
            string state = w.Exists ? (w.Writable ? "exists + writable" : "exists, write FAILED")
                                    : (w.Creatable ? "missing — will be created on first job"
                                                    : "missing + parent not writable");
            return w.Note is null ? state : $"{state}. {w.Note}";
        }
    }

    public string WebView2Line1
    {
        get
        {
            var w = ViewModel?.Report?.WebView2;
            if (w is null) return string.Empty;
            return $"{GlyphFor(w.Health)}  Runtime {(w.RuntimeAvailable ? "available" : "MISSING")}";
        }
    }

    public string WebView2Line2
    {
        get
        {
            var w = ViewModel?.Report?.WebView2;
            if (w is null) return string.Empty;
            string installer = w.BundledInstallerPresent
                ? $"Bundled bootstrapper: {w.BundledInstallerPath}"
                : $"No bundled bootstrapper. Manual: {w.ManualInstallerUrl}";
            return w.Note is null ? installer : $"{w.Note} {installer}";
        }
    }

    public string NotificationsLine
    {
        get
        {
            var n = ViewModel?.Report?.Notifications;
            if (n is null) return string.Empty;
            return $"{GlyphFor(n.Health)}  Registered={n.Registered}. {n.Note}";
        }
    }

    public string JumpListLine1
    {
        get
        {
            var j = ViewModel?.Report?.JumpList;
            if (j is null) return string.Empty;
            return $"{GlyphFor(j.Health)}  Initialized={j.Initialized}, LastRefreshSucceeded={j.LastRefreshSucceeded}";
        }
    }

    public string JumpListLine2
    {
        get
        {
            var j = ViewModel?.Report?.JumpList;
            if (j is null) return string.Empty;
            string ts = j.LastRefreshUtc is { } at
                ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : "never";
            return j.Note is null ? $"Last refresh: {ts}" : $"Last refresh: {ts}. {j.Note}";
        }
    }

    public string ProcessLine1
    {
        get
        {
            var p = ViewModel?.Report?.Process;
            if (p is null) return string.Empty;
            return $"{GlyphFor(p.Health)}  PID {p.Pid}";
        }
    }

    public string ProcessLine2
    {
        get
        {
            var p = ViewModel?.Report?.Process;
            if (p is null) return string.Empty;
            string aumid = p.ActualAumid is null
                ? $"AUMID actual=(error: {p.AumidError ?? "unknown"})"
                : $"AUMID actual={p.ActualAumid}";
            return $"{p.ExePath}\n{aumid}, configured={p.ConfiguredAumid}";
        }
    }

    private static string GlyphFor(DiagnosticsHealth h) => h switch
    {
        DiagnosticsHealth.Green => "\u2705",   // ✅
        DiagnosticsHealth.Yellow => "\u26A0\uFE0F", // ⚠️
        _ => "\u274C",                          // ❌
    };
}
