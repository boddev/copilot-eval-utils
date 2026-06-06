using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.Services;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// View-model for <see cref="Views.DiagnosticsView"/>. Wraps
/// <see cref="IDiagnosticsService"/> with a refresh command and a
/// <see cref="SemaphoreSlim"/>-guarded re-entrancy gate so concurrent
/// button clicks can't run two probes in parallel (GPT-5.5
/// slice-diagnostics plan-review NON-BLOCKER #11).
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly IDiagnosticsService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    [ObservableProperty]
    public partial DiagnosticsReport? Report { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public DiagnosticsViewModel(IDiagnosticsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        // Bail fast if a refresh is already in flight; the CanExecute
        // gate disables the button but defensive double-check keeps
        // the semaphore from ever being held twice on one VM.
        if (!await _gate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            ErrorMessage = null;
            RefreshCommand.NotifyCanExecuteChanged();

            DiagnosticsReport result = await _service.CollectAsync().ConfigureAwait(true);
            Report = result;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRefresh() => !IsRefreshing;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

