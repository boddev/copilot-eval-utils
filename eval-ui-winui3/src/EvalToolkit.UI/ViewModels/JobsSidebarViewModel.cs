using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.Jobs;
using EvalToolkit.UI.Services;
using Microsoft.UI.Dispatching;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Sidebar list of past jobs. Reads the workspace via
/// <see cref="IJobsRepository"/> on a worker thread (Task.Run because
/// the impl is synchronous I/O) and marshals collection updates back to
/// the UI thread via the captured <see cref="DispatcherQueue"/>.
/// Subscribes to <see cref="EvalGenJobService.JobStateChanged"/> to
/// refresh on both job start and terminal states.
/// </summary>
public sealed partial class JobsSidebarViewModel : ObservableObject, IDisposable
{
    private readonly IJobsRepository _repository;
    private readonly string _workspaceRoot;
    private readonly EvalGenJobService? _jobService;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _disposed;

    public JobsSidebarViewModel(
        IJobsRepository repository,
        string workspaceRoot,
        EvalGenJobService? jobService,
        DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _repository = repository;
        _workspaceRoot = workspaceRoot;
        _jobService = jobService;
        _dispatcher = dispatcher;

        if (_jobService is not null)
        {
            _jobService.JobStateChanged += OnJobStateChanged;
        }
    }

    public ObservableCollection<JobSummaryViewModel> Jobs { get; } = new();

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool HasJobs { get; set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // GPT-5.5 review #1+#2: marshal the entire VM-state-change path
        // onto the UI thread so IsRefreshing and other observable props
        // are never mutated from a worker thread, and dispose-races are
        // checked at every await boundary.
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = RefreshAsyncUiThread());
            return;
        }
        await RefreshAsyncUiThread().ConfigureAwait(true);
    }

    private async Task RefreshAsyncUiThread()
    {
        if (_disposed) return;
        // Non-blocking gate: if a refresh is already in flight, skip.
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            IsRefreshing = true;
            var jobs = await Task.Run(() => _repository.ListJobs(_workspaceRoot)).ConfigureAwait(true);
            if (_disposed) return;
            Jobs.Clear();
            foreach (var j in jobs)
            {
                Jobs.Add(new JobSummaryViewModel(j));
            }
            HasJobs = Jobs.Count > 0;
        }
        finally
        {
            if (!_disposed)
            {
                IsRefreshing = false;
                try { _refreshGate.Release(); }
                catch (ObjectDisposedException) { /* disposed mid-refresh */ }
            }
        }
    }

    [RelayCommand]
#pragma warning disable CA1822 // [RelayCommand] target must be an instance method.
    public void OpenJobFolder(JobSummaryViewModel? job)
    {
        if (job is null) return;
        ShellOpener.OpenFolder(job.FolderPath);
    }

    [RelayCommand]
    public void RevealJobFolder(JobSummaryViewModel? job)
    {
        if (job is null) return;
        // GPT-5.5 review #12: reveal job.json if it exists (gives the
        // user an obvious entry point); otherwise fall back to revealing
        // the folder itself.
        string metaPath = Path.Combine(job.FolderPath, JobMetadataStore.FileName);
        if (File.Exists(metaPath))
        {
            ShellOpener.RevealInFolder(metaPath);
        }
        else
        {
            ShellOpener.OpenFolder(job.FolderPath);
        }
    }
#pragma warning restore CA1822

    private void OnJobStateChanged(object? sender, JobStateChangedEventArgs e)
    {
        // GPT-5.5 review #2: observe fire-and-forget failures so a
        // transient IO exception during refresh does not crash the
        // process via an unobserved task exception.
        _ = SafeRefreshAsync();
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* disposed mid-flight */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"JobsSidebar refresh failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_jobService is not null)
        {
            _jobService.JobStateChanged -= OnJobStateChanged;
        }
        _refreshGate.Dispose();
    }
}
