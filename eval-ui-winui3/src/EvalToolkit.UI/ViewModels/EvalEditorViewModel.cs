using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Editor;
using Microsoft.UI.Dispatching;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Step 4 row editor. Loads a 4-column EvalScore CSV from
/// <see cref="CsvPath"/>, exposes editable rows, and atomically saves
/// back. All I/O is dispatched off the UI thread; observable property
/// and ObservableCollection mutations happen on the UI thread via the
/// captured <see cref="DispatcherQueue"/>.
///
/// Dirty tracking uses a per-row <see cref="EvalRowViewModel.DirtyChanged"/>
/// event so the aggregate <see cref="DirtyCount"/> updates in O(1) per
/// edit instead of an O(N) PropertyChanged scan.
/// </summary>
public sealed partial class EvalEditorViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private int _operationVersion;
    private bool _disposed;

    public EvalEditorViewModel(DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ioGate.Dispose();
    }

    public ObservableCollection<EvalRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    public partial string? CsvPath { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial int DirtyCount { get; set; }

    public bool HasDirtyRows => DirtyCount > 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public int RowCount => Rows.Count;

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnDirtyCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasDirtyRows));
        SaveCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Load <paramref name="csvPath"/> into the editor. Safe to call
    /// from any thread; observable state mutations marshal back to the
    /// UI thread. Each call bumps <see cref="_operationVersion"/>; if a
    /// later Load or Reset is issued while this one is in flight, the
    /// in-flight completion is silently dropped so it can't repopulate
    /// rows belonging to a CSV the user has already navigated away from.
    /// </summary>
    public async Task LoadAsync(string csvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        int myVersion = Interlocked.Increment(ref _operationVersion);

        if (!await _ioGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await EnqueueAsync(() =>
            {
                if (myVersion != Volatile.Read(ref _operationVersion)) return;
                IsLoading = true;
                ErrorMessage = null;
                ClearRowsInternal();
            }).ConfigureAwait(false);

            IReadOnlyList<EvalRowRecord> records;
            try
            {
                records = await Task.Run(() => EvalCsvEditor.ReadFlat(csvPath))
                                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await EnqueueAsync(() =>
                {
                    if (myVersion != Volatile.Read(ref _operationVersion)) return;
                    ErrorMessage = $"Failed to load CSV: {ex.Message}";
                    IsLoading = false;
                    IsLoaded = false;
                    CsvPath = csvPath;
                }).ConfigureAwait(false);
                return;
            }

            await EnqueueAsync(() =>
            {
                if (myVersion != Volatile.Read(ref _operationVersion)) return;
                ClearRowsInternal();
                foreach (var rec in records)
                {
                    AddRowInternal(new EvalRowViewModel(rec));
                }
                CsvPath = csvPath;
                IsLoaded = true;
                IsLoading = false;
                DirtyCount = 0;
                OnPropertyChanged(nameof(RowCount));
            }).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Reset the editor (used when the wizard restarts). Bumps the
    /// operation version so any in-flight Load/Save callback is dropped.
    /// </summary>
    public void Reset()
    {
        Interlocked.Increment(ref _operationVersion);
        if (_dispatcher.HasThreadAccess)
        {
            ResetCore();
        }
        else
        {
            _dispatcher.TryEnqueue(ResetCore);
        }

        void ResetCore()
        {
            ClearRowsInternal();
            CsvPath = null;
            IsLoaded = false;
            IsLoading = false;
            IsSaving = false;
            DirtyCount = 0;
            ErrorMessage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CsvPath)) return;
        string path = CsvPath;
        int myVersion = Interlocked.Increment(ref _operationVersion);

        if (!await _ioGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await EnqueueAsync(() =>
            {
                if (myVersion != Volatile.Read(ref _operationVersion)) return;
                IsSaving = true;
                ErrorMessage = null;
            }).ConfigureAwait(false);

            // Snapshot rows on the UI thread (ObservableCollection is
            // not thread-safe; user could still type in a textbox while
            // we serialize the snapshot).
            List<EvalRowRecord> snapshot = await EnqueueWithResultAsync(() =>
            {
                var list = new List<EvalRowRecord>(Rows.Count);
                foreach (var r in Rows) list.Add(r.ToRecord());
                return list;
            }).ConfigureAwait(false);

            try
            {
                await Task.Run(() => EvalCsvEditor.WriteFlat(path, snapshot))
                          .ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // Most common cause: Excel has the file open and is
                // holding an exclusive lock. Surface a clear hint.
                await EnqueueAsync(() =>
                {
                    if (myVersion != Volatile.Read(ref _operationVersion)) return;
                    ErrorMessage = $"Save failed: {ex.Message} (close the file in Excel/other apps and retry)";
                    IsSaving = false;
                }).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await EnqueueAsync(() =>
                {
                    if (myVersion != Volatile.Read(ref _operationVersion)) return;
                    ErrorMessage = $"Save failed: {ex.Message}";
                    IsSaving = false;
                }).ConfigureAwait(false);
                return;
            }

            await EnqueueAsync(() =>
            {
                if (myVersion != Volatile.Read(ref _operationVersion)) return;
                foreach (var r in Rows)
                {
                    r.AcceptChanges();
                }
                DirtyCount = 0;
                IsSaving = false;
            }).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }
    private bool CanSave() => IsLoaded && HasDirtyRows && !IsSaving && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private void RevertAll()
    {
        foreach (var r in Rows) r.Revert();
        // OnRowDirtyChanged will decrement DirtyCount on each row
        // transitioning from dirty -> clean. Explicit zero for safety.
        DirtyCount = 0;
    }
    private bool CanRevertAll() => HasDirtyRows && !IsSaving && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (string.IsNullOrWhiteSpace(CsvPath)) return;
        await LoadAsync(CsvPath).ConfigureAwait(false);
    }
    private bool CanReload() => !string.IsNullOrWhiteSpace(CsvPath) && !IsSaving && !IsLoading;

    private void OnRowDirtyChanged(object? sender, EventArgs e)
    {
        if (sender is not EvalRowViewModel row) return;
        // Recompute aggregate on dispatcher; row events can fire from
        // any thread that mutates the row (in practice it's always UI,
        // but better safe than sorry).
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => DirtyCount += row.IsDirty ? 1 : -1);
        }
        else
        {
            DirtyCount += row.IsDirty ? 1 : -1;
        }
    }

    private void AddRowInternal(EvalRowViewModel row)
    {
        row.DirtyChanged += OnRowDirtyChanged;
        Rows.Add(row);
    }

    private void ClearRowsInternal()
    {
        foreach (var r in Rows)
        {
            r.DirtyChanged -= OnRowDirtyChanged;
        }
        Rows.Clear();
        OnPropertyChanged(nameof(RowCount));
    }

    private Task EnqueueAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("DispatcherQueue.TryEnqueue returned false."));
        }
        return tcs.Task;
    }

    private Task<T> EnqueueWithResultAsync<T>(Func<T> func)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return Task.FromResult(func());
        }
        var tcs = new TaskCompletionSource<T>();
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("DispatcherQueue.TryEnqueue returned false."));
        }
        return tcs.Task;
    }
}
