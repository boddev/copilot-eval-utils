using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.Jobs;
using Microsoft.UI.Dispatching;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="IJumpListService"/> backed by the Win32
/// <c>ICustomDestinationList</c> COM API. Builds a "Recent jobs"
/// category from <see cref="IJobsRepository.ListJobs"/> (filtered to
/// <see cref="JobStatus.Complete"/>) plus a "New evaluation" user task.
/// Refreshes initially and whenever either job pipeline raises
/// <c>JobStateChanged</c>; refreshes are debounced to a 1-second window
/// so a burst of completions does not churn the shell.
/// </summary>
/// <remarks>
/// <para>
/// Slice 29 (winui-native-plus-jumplist). GPT-5.5 plan-review blockers
/// addressed:
/// </para>
/// <list type="number">
/// <item><description>
/// Each shell link sets <c>PKEY_AppUserModel.ID</c> and <c>PKEY_Title</c>
/// via <see cref="JumpListInterop.IPropertyStore"/> + commits before
/// being added — without this the shell can fail to group entries
/// under our AUMID or fail to render them at all.
/// </description></item>
/// <item><description>
/// Activation arguments use <c>--job-id &lt;jobId&gt;</c> rather than
/// raw paths so the route stays robust to spaces / quotes / Unicode in
/// job folder names. <see cref="App.HandleActivation"/> resolves the
/// JobId back to a path via <see cref="JobsRepository"/>.
/// </description></item>
/// </list>
/// <para>
/// AUMID note: the unpackaged AUMID is set in <see cref="Program"/> via
/// <c>SetCurrentProcessExplicitAppUserModelID</c>. When slice 30 ships
/// the MSIX, the package-identity AUMID takes precedence; user pins
/// under the old unpackaged AUMID may be orphaned (documented in the
/// slice-30 packaging notes).
/// </para>
/// </remarks>
public sealed class JumpListService : IJumpListService
{
    /// <summary>Default unpackaged AUMID; matches what Program sets.</summary>
    public const string DefaultAppId = "EvalToolkit.UI";

    /// <summary>Recent-job category cap. Five fits the typical jump-list height.</summary>
    private const int MaxRecentJobs = 5;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1);

    private readonly IJobsRepository _repository;
    private readonly string _workspaceRoot;
    private readonly DispatcherQueue _uiDispatcher;
    private readonly string _appId;
    private readonly object _gate = new();

    private IEvalGenJobService? _genJobs;
    private IEvalScoreJobService? _scoreJobs;
    private bool _genSubscribed;
    private bool _scoreSubscribed;
    private bool _refreshPending;
    private bool _disposed;
    private bool _initialized;
    private bool _lastRefreshSucceeded;
    private DateTimeOffset? _lastRefreshUtc;

    public JumpListService(
        IJobsRepository repository,
        string workspaceRoot,
        DispatcherQueue uiDispatcher,
        string appId = DefaultAppId)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workspaceRoot = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot
            : throw new ArgumentException("Workspace root required.", nameof(workspaceRoot));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _appId = !string.IsNullOrWhiteSpace(appId) ? appId : DefaultAppId;
    }

    public void Initialize(IEvalGenJobService? genJobs, IEvalScoreJobService? scoreJobs)
    {
        _genJobs = genJobs;
        _scoreJobs = scoreJobs;
        if (_genJobs is not null)
        {
            _genJobs.JobStateChanged += OnJobStateChanged;
            _genSubscribed = true;
        }
        if (_scoreJobs is not null)
        {
            _scoreJobs.JobStateChanged += OnJobStateChanged;
            _scoreSubscribed = true;
        }
        lock (_gate) { _initialized = true; }
    }

    public bool Initialized
    {
        get { lock (_gate) { return _initialized; } }
    }

    public bool LastRefreshSucceeded
    {
        get { lock (_gate) { return _lastRefreshSucceeded; } }
    }

    public DateTimeOffset? LastRefreshUtc
    {
        get { lock (_gate) { return _lastRefreshUtc; } }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        // GPT-5.5 code-review non-blocker #1: always enqueue rather
        // than running synchronously when called from the UI thread.
        // Keeps RefreshAsync truly non-blocking from a caller's PoV
        // (e.g. `_ = JumpList.RefreshAsync()` in OnLaunched should
        // not delay shell paint by a synchronous COM round-trip).
        _uiDispatcher.TryEnqueue(RefreshCore);
        return Task.CompletedTask;
    }

    public Task<bool> RefreshAndWaitAsync(CancellationToken cancellationToken = default)
    {
        // GPT-5.5 slice-diagnostics plan-review BLOCKER #2: diagnostics
        // needs to await the actual COM result, not just enqueue.
        // Bridge the dispatcher-driven RefreshCore through a TCS.
        if (_disposed) return Task.FromResult(false);
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // GPT-5.5 slice-32 code-review non-blocker #3: register the
        // CancellationToken so callers can break out if the UI
        // dispatcher accepts the enqueue but shuts down before the
        // lambda runs (e.g. during app teardown). Without this the
        // diagnostics refresh could hang forever.
        CancellationTokenRegistration ctr = default;
        if (cancellationToken.CanBeCanceled)
        {
            ctr = cancellationToken.Register(static state =>
            {
                var t = (TaskCompletionSource<bool>)state!;
                t.TrySetCanceled();
            }, tcs);
        }
        // Dispose the registration when the task completes (any state)
        // so we don't leak it on long-lived tokens.
        _ = tcs.Task.ContinueWith(static (_, state) =>
        {
            ((CancellationTokenRegistration)state!).Dispose();
        }, ctr, TaskScheduler.Default);

        bool queued = _uiDispatcher.TryEnqueue(() =>
        {
            try
            {
                bool ok = RefreshCoreReportingSuccess();
                tcs.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!queued)
        {
            // Dispatcher not running (e.g. headless --diagnostics path
            // hasn't created a UI thread). Run inline on the caller
            // thread so we still capture a real success/failure result.
            try
            {
                bool ok = RefreshCoreReportingSuccess();
                tcs.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_genSubscribed && _genJobs is not null)
        {
            try { _genJobs.JobStateChanged -= OnJobStateChanged; } catch { /* swallow */ }
            _genSubscribed = false;
        }
        if (_scoreSubscribed && _scoreJobs is not null)
        {
            try { _scoreJobs.JobStateChanged -= OnJobStateChanged; } catch { /* swallow */ }
            _scoreSubscribed = false;
        }
        // Per docs the shell clears jump-list state on process exit; we
        // don't call DeleteList here because a fresh launch should pick
        // up where the user left off, not start from an empty list.
    }

    // ----- internals -----

    private void OnJobStateChanged(object? sender, JobStateChangedEventArgs e)
    {
        if (e.Status is not (JobStatus.Complete or JobStatus.Failed or JobStatus.Cancelled))
        {
            return;
        }
        ScheduleDebouncedRefresh();
    }

    private void ScheduleDebouncedRefresh()
    {
        lock (_gate)
        {
            if (_refreshPending || _disposed) return;
            _refreshPending = true;
        }

        // Fire-and-forget delay → marshal back to UI. Errors in the
        // delay or marshal must NOT crash the app; swallow at the top.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceWindow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JumpListService.Debounce delay: {ex}");
            }

            lock (_gate) { _refreshPending = false; }
            if (_disposed) return;

            try
            {
                _uiDispatcher.TryEnqueue(RefreshCore);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JumpListService.Debounce enqueue: {ex}");
            }
        });
    }

    private void RefreshCore()
    {
        _ = RefreshCoreReportingSuccess();
    }

    private bool RefreshCoreReportingSuccess()
    {
        if (_disposed) return false;
        bool ok;
        try
        {
            // GPT-5.5 slice-32 code-review BLOCKER #1: take the actual
            // success bool from BuildJumpList. The previous version
            // returned true as long as BuildJumpList didn't throw,
            // even when BeginList / CommitList failed (the function
            // swallows COM HRESULT failures internally and only logs).
            // Diagnostics could then report `jumpList.health = green`
            // for a refresh the shell actually rejected.
            ok = BuildJumpList();
        }
        catch (Exception ex)
        {
            // CoCreateInstance can fail on stripped SKUs (Server Core) or
            // when the shell COM service is unavailable. The app must
            // keep working; the jump list is a nice-to-have.
            Debug.WriteLine($"JumpListService.RefreshCore: {ex}");
            ok = false;
        }
        lock (_gate)
        {
            _lastRefreshSucceeded = ok;
            _lastRefreshUtc = DateTimeOffset.UtcNow;
        }
        return ok;
    }

    private bool BuildJumpList()
    {
        IReadOnlyList<JobSummary> recent;
        try
        {
            recent = _repository
                .ListJobs(_workspaceRoot)
                .Where(j => j.Status == JobStatus.Complete)
                .Take(MaxRecentJobs)
                .ToList();
        }
        catch (Exception ex)
        {
            // Missing / unreadable workspace shouldn't blank the jump
            // list of its New-evaluation task — fall through with an
            // empty recent list.
            Debug.WriteLine($"JumpListService.ListJobs failed: {ex}");
            recent = Array.Empty<JobSummary>();
        }

        var destObj = Activator.CreateInstance(Type.GetTypeFromCLSID(JumpListInterop.CLSID_DestinationList)!);
        if (destObj is not JumpListInterop.ICustomDestinationList destList)
        {
            Debug.WriteLine("JumpListService: CDestinationList does not implement ICustomDestinationList.");
            if (destObj is not null) Marshal.ReleaseComObject(destObj);
            return false;
        }

        bool committed = false;
        try
        {
            destList.SetAppID(_appId);

            // BeginList -> we don't actually consume the removed array,
            // but we still have to honor the contract or CommitList
            // returns E_INVALIDARG.
            var iidArr = JumpListInterop.IID_IObjectArray;
            int hr = destList.BeginList(out _, ref iidArr, out _);
            if (hr < 0)
            {
                Debug.WriteLine($"JumpListService: BeginList failed 0x{hr:X8}");
                return false;
            }

            // User tasks: New evaluation
            var tasks = CreateObjectCollection();
            if (tasks is not null)
            {
                var newEvalLink = CreateShellLink(
                    arguments: "--new-evaluation",
                    title: "New evaluation",
                    description: "Start a new EvalToolkit dataset generation.");
                if (newEvalLink is not null)
                {
                    try { tasks.AddObject(newEvalLink); }
                    catch (Exception ex) { Debug.WriteLine($"JumpListService.AddObject(task): {ex}"); }
                }
                if (tasks is JumpListInterop.IObjectArray tasksArr)
                {
                    int taskHr = destList.AddUserTasks(tasksArr);
                    if (taskHr < 0) Debug.WriteLine($"JumpListService: AddUserTasks failed 0x{taskHr:X8}");
                }
            }

            // Recent jobs category
            if (recent.Count > 0)
            {
                var jobsColl = CreateObjectCollection();
                if (jobsColl is not null)
                {
                    foreach (var j in recent)
                    {
                        // GPT-5.5 code-review non-blocker #3: quote
                        // the JobId even though current generated IDs
                        // are slug-safe. Cheap hardening for legacy /
                        // imported folders whose IDs may contain
                        // whitespace.
                        var link = CreateShellLink(
                            arguments: $"--job-id \"{j.JobId}\"",
                            title: TruncateTitle(j.DisplayName),
                            description: $"Open job folder: {j.JobId}");
                        if (link is not null)
                        {
                            try { jobsColl.AddObject(link); }
                            catch (Exception ex) { Debug.WriteLine($"JumpListService.AddObject(job): {ex}"); }
                        }
                    }
                    if (jobsColl is JumpListInterop.IObjectArray jobsArr)
                    {
                        int catHr = destList.AppendCategory("Recent jobs", jobsArr);
                        if (catHr < 0) Debug.WriteLine($"JumpListService: AppendCategory failed 0x{catHr:X8}");
                    }
                }
            }

            int commitHr = destList.CommitList();
            if (commitHr < 0)
            {
                Debug.WriteLine($"JumpListService: CommitList failed 0x{commitHr:X8}");
            }
            // GPT-5.5 code-review non-blocker #4: only treat as
            // "committed" when CommitList actually succeeded. A
            // failed CommitList leaves the build transaction active,
            // so let the finally call AbortList to release it.
            committed = commitHr >= 0;
            return committed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JumpListService.BuildJumpList: {ex}");
            return false;
        }
        finally
        {
            if (!committed)
            {
                // GPT-5.5 plan-review suggestion #4: always abort if we
                // started a list but failed before committing — otherwise
                // a subsequent BeginList will return E_UNEXPECTED.
                try { destList.AbortList(); } catch { /* swallow */ }
            }
            try { Marshal.ReleaseComObject(destList); } catch { /* swallow */ }
        }
    }

    private static JumpListInterop.IObjectCollection? CreateObjectCollection()
    {
        try
        {
            var obj = Activator.CreateInstance(
                Type.GetTypeFromCLSID(JumpListInterop.CLSID_EnumerableObjectCollection)!);
            return obj as JumpListInterop.IObjectCollection;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JumpListService.CreateObjectCollection: {ex}");
            return null;
        }
    }

    private object? CreateShellLink(string arguments, string title, string description)
    {
        object? link = null;
        bool ok = false;
        try
        {
            link = (JumpListInterop.IShellLinkW)Activator.CreateInstance(
                Type.GetTypeFromCLSID(JumpListInterop.CLSID_ShellLink)!)!;

            string exePath = Environment.ProcessPath ?? string.Empty;
            ((JumpListInterop.IShellLinkW)link).SetPath(exePath);
            ((JumpListInterop.IShellLinkW)link).SetArguments(arguments);
            ((JumpListInterop.IShellLinkW)link).SetIconLocation(exePath, 0);
            ((JumpListInterop.IShellLinkW)link).SetDescription(description);

            if (link is JumpListInterop.IPropertyStore store)
            {
                JumpListInterop.SetStringProperty(store, JumpListInterop.PKEY_AppUserModel_ID, _appId);
                JumpListInterop.SetStringProperty(store, JumpListInterop.PKEY_Title, title);
                int commitHr = store.Commit();
                if (commitHr < 0)
                {
                    // GPT-5.5 code-review non-blocker #2: if the
                    // property store commit fails, the link will
                    // lack our AUMID/title and the shell may render
                    // it inconsistently. Drop the link entirely.
                    Debug.WriteLine($"JumpListService.CreateShellLink: store.Commit failed 0x{commitHr:X8} — dropping link.");
                    return null;
                }
            }
            else
            {
                Debug.WriteLine("JumpListService.CreateShellLink: IShellLinkW did not QI for IPropertyStore — dropping link.");
                return null;
            }

            ok = true;
            return link;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JumpListService.CreateShellLink: {ex}");
            return null;
        }
        finally
        {
            if (!ok && link is not null)
            {
                try { Marshal.ReleaseComObject(link); } catch { /* swallow */ }
            }
        }
    }

    /// <summary>
    /// Truncate to the shell's documented MAX_PATH-ish budget for jump-
    /// list titles (260 chars). Long auto-generated descriptions can run
    /// past this; clipping prevents the shell from rejecting the entry.
    /// </summary>
    private static string TruncateTitle(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        const int max = 256;
        if (s.Length <= max) return s;
        return string.Create(CultureInfo.InvariantCulture, $"{s.AsSpan(0, max - 1)}…");
    }
}
