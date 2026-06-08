using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Services;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Wizard step identifier. Slice 22 renders steps 1+2, slice 23 adds
/// step 3 (progress). Later slices add steps 4 (row editor) and
/// 5 (score).
/// </summary>
public enum WizardStep
{
    Step1Dataset,
    Step2Describe,
    Step3Progress,
    Step4Editor,
    Step5Score,
}

/// <summary>
/// Owns wizard navigation state, exposes child VMs for each step, and
/// orchestrates the actual eval-generation job via
/// <see cref="IEvalGenJobService"/>. Stays pure (no Views, Windows,
/// Frames) so it can be unit-tested with a mock job service.
/// </summary>
public partial class WizardViewModel : ObservableObject, IDisposable
{
    private readonly IEvalGenJobService _jobService;
    private readonly IEvalScoreJobService _scoreService;
    private readonly string _workspaceRoot;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _scoreCts;
    private bool _disposed;

    public DatasetPickerViewModel DatasetPicker { get; }
    public DescribeViewModel Describe { get; }
    public ProgressViewModel Progress { get; }
    public EvalEditorViewModel Editor { get; }
    public ScoreViewModel Score { get; }

    public WizardViewModel(
        IFileDialogService dialog,
        IEvalGenJobService jobService,
        IEvalScoreJobService scoreService,
        string workspaceRoot,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(jobService);
        ArgumentNullException.ThrowIfNull(scoreService);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _jobService = jobService;
        _scoreService = scoreService;
        _workspaceRoot = workspaceRoot;

        DatasetPicker = new DatasetPickerViewModel(dialog);
        Describe = new DescribeViewModel();
        Progress = new ProgressViewModel();
        Editor = new EvalEditorViewModel(dispatcher);
        Score = new ScoreViewModel(dispatcher);
        CurrentStep = WizardStep.Step1Dataset;

        DatasetPicker.PropertyChanged += OnChildPropertyChanged;
        Describe.PropertyChanged += OnChildPropertyChanged;
        Progress.PropertyChanged += OnProgressPropertyChanged;
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Score.PropertyChanged += OnScorePropertyChanged;
    }

    [ObservableProperty]
    public partial WizardStep CurrentStep { get; set; }

    public bool IsStep1Visible => CurrentStep == WizardStep.Step1Dataset;
    public bool IsStep2Visible => CurrentStep == WizardStep.Step2Describe;
    public bool IsStep3Visible => CurrentStep == WizardStep.Step3Progress;
    public bool IsStep4Visible => CurrentStep == WizardStep.Step4Editor;
    public bool IsStep5Visible => CurrentStep == WizardStep.Step5Score;

    public int CurrentStepNumber => (int)CurrentStep + 1;
    public string StepHeader => $"Step {CurrentStepNumber} of 5";

    public bool CanGoNext => CurrentStep switch
    {
        WizardStep.Step1Dataset => DatasetPicker.HasSelection,
        _ => false,
    };

    public bool CanGoBack => CurrentStep switch
    {
        WizardStep.Step2Describe => true,
        // From step 3 (progress) — allow back once the job is finished
        // or failed (not while running, to avoid orphaned background work).
        WizardStep.Step3Progress => !Progress.IsRunning,
        // From step 4 (editor) — always allow back to progress (the job
        // is already complete by the time the user is in the editor).
        // The view confirms unsaved-changes via ContentDialog before
        // actually invoking GoBack.
        WizardStep.Step4Editor => !Editor.IsSaving && !Editor.IsLoading,
        // From step 5 (score) — allow back to the editor once scoring
        // is finished or hasn't started (not while running).
        WizardStep.Step5Score => !Score.IsRunning,
        _ => false,
    };

    public bool CanGenerate =>
        CurrentStep == WizardStep.Step2Describe
        && DatasetPicker.HasSelection
        && Describe.HasDescription
        && Describe.CountInRange
        && !Progress.IsRunning;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void GoNext()
    {
        if (CurrentStep == WizardStep.Step1Dataset)
        {
            CurrentStep = WizardStep.Step2Describe;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        switch (CurrentStep)
        {
            case WizardStep.Step2Describe:
                CurrentStep = WizardStep.Step1Dataset;
                break;
            case WizardStep.Step3Progress:
                // Only reachable when CanGoBack==true, i.e. not running.
                CurrentStep = WizardStep.Step2Describe;
                break;
            case WizardStep.Step4Editor:
                CurrentStep = WizardStep.Step3Progress;
                break;
            case WizardStep.Step5Score:
                CurrentStep = WizardStep.Step4Editor;
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToEditor))]
    private async Task GoToEditorAsync()
    {
        if (string.IsNullOrWhiteSpace(Progress.OutputCsvPath)) return;
        // GPT-5.5 finding #3: If the user already opened the editor for
        // this CSV, edited rows (now dirty), and clicked Back to Step 3,
        // re-entering must NOT reload from disk — that would silently
        // discard the dirty edits. Only load when the editor is empty
        // or pointing at a different file.
        bool alreadyLoaded = Editor.IsLoaded
            && string.Equals(Editor.CsvPath, Progress.OutputCsvPath, StringComparison.OrdinalIgnoreCase);
        if (!alreadyLoaded)
        {
            await Editor.LoadAsync(Progress.OutputCsvPath).ConfigureAwait(true);
        }
        CurrentStep = WizardStep.Step4Editor;
    }
    private bool CanGoToEditor() =>
        Progress.IsComplete
        && !string.IsNullOrWhiteSpace(Progress.OutputCsvPath)
        && !Editor.IsLoading
        && !Editor.IsSaving;

    /// <summary>
    /// Advance from Step 4 (editor) into Step 5 (score). Blocks if
    /// dirty edits are pending (GPT-5.5 plan-review #1 / blocker #3 —
    /// silently scoring stale sidecar would lose user edits). The
    /// service's CSV-merge step then overlays the saved CSV onto the
    /// sidecar rows so user edits are reflected in scoring.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToScore))]
    private void GoToScore()
    {
        if (string.IsNullOrWhiteSpace(Progress.OutputSidecarPath))
        {
            return;
        }
        if (Editor.HasDirtyRows)
        {
            Editor.ErrorMessage = "Save your edits in the row editor before scoring.";
            return;
        }
        Score.EvalSetPath = Progress.OutputSidecarPath;
        Score.OutputDirectory = Progress.OutputDirectory;
        // Carry tenant id from Step 2 if user already supplied one.
        if (string.IsNullOrWhiteSpace(Score.TenantId) && !string.IsNullOrWhiteSpace(Describe.M365TenantId))
        {
            Score.TenantId = Describe.M365TenantId;
        }
        CurrentStep = WizardStep.Step5Score;
    }
    private bool CanGoToScore() =>
        Progress.IsComplete
        && !string.IsNullOrWhiteSpace(Progress.OutputSidecarPath)
        && !Score.IsRunning
        && !Editor.IsSaving
        && !Editor.IsLoading;

    /// <summary>
    /// Slice 30 (FTA): hydrate wizard state from an existing on-disk
    /// eval set and land in Step 4 (row editor) with the CSV loaded.
    /// Called by <see cref="Views.WizardView.OnNavigatedTo"/> when
    /// <see cref="Services.FileActivationRouter"/> navigates with an
    /// <see cref="Models.OpenEvalSetRequest"/> parameter.
    ///
    /// Hydration covers BOTH Progress (so the wizard considers
    /// generation "complete" — required by <see cref="CanGoToEditor"/>
    /// AND <see cref="CanGoToScore"/>) AND Score (so the Step 5 form
    /// is pre-populated when the user advances). Per GPT-5.5
    /// plan-review BLOCKER #2: just calling Editor.LoadAsync without
    /// hydrating Progress would land the user in Step 4 but trap them
    /// there (GoToScore would be disabled).
    /// </summary>
    public async Task OpenExistingEvalSetAsync(EvalToolkit.UI.Models.OpenEvalSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Reset/clear any in-flight generation state so the wizard
        // doesn't think it's running a job. The user opened this from
        // Explorer — there's no live generation tied to it.
        Progress.IsRunning = false;
        Progress.IsFailed = false;
        Progress.Percent = 100;
        Progress.Phase = "Loaded";
        Progress.StatusMessage = "Opened from file.";
        Progress.OutputCsvPath = request.CsvPath;
        Progress.OutputSidecarPath = request.SidecarPath;
        Progress.OutputDirectory = request.OutputDirectory;
        // Set IsComplete LAST so the CanGo* re-evaluation that fires on
        // its property-changed sees all the path fields populated.
        Progress.IsComplete = true;

        Score.EvalSetPath = request.SidecarPath;
        Score.OutputDirectory = request.OutputDirectory;

        bool loadOk = false;
        try
        {
            await Editor.LoadAsync(request.CsvPath).ConfigureAwait(true);
            loadOk = Editor.IsLoaded;
        }
        catch (Exception ex)
        {
            // LoadAsync surfaces its own ErrorMessage on the editor,
            // but trace here too so the activation path is debuggable
            // when the editor fails to mount.
            System.Diagnostics.Debug.WriteLine(
                $"WizardViewModel.OpenExistingEvalSetAsync: LoadAsync failed for '{request.CsvPath}': {ex}");
        }

        // GPT-5.5 slice-30 code review NON-BLOCKER #4: when the editor
        // load fails, the wizard's Progress still claims "Loaded" which
        // weakens the "opened with CSV pre-loaded" promise. Reflect the
        // failure into Progress.StatusMessage so the user understands
        // why the editor is empty. Step 5 scoring stays available
        // because the sidecar IS loaded — only the editor view failed.
        if (!loadOk)
        {
            Progress.StatusMessage =
                $"Opened sidecar from file, but CSV failed to load: '{request.CsvPath}'.";
        }

        CurrentStep = WizardStep.Step4Editor;
    }

    [RelayCommand(CanExecute = nameof(CanRunScore))]
    private async Task RunScoreAsync()
    {
        EvalScoreRequest request;
        try
        {
            request = Score.BuildRequest();
        }
        catch (Exception ex)
        {
            Score.ApplyFailure(Score.RunVersion, ex.Message);
            return;
        }

        long version = Score.BumpRunVersion();
        Score.ResetRunState();
        Score.IsRunning = true;

        _scoreCts?.Dispose();
        _scoreCts = new CancellationTokenSource();
        var ct = _scoreCts.Token;

        // ScoreViewModel.ApplyProgress filters by version and marshals
        // to the dispatcher itself, so we don't need a UI-thread
        // Progress<T> capture here.
        var progress = new Progress<JobProgress>(p => Score.ApplyProgress(version, p));

        try
        {
            var result = await Task.Run(
                () => _scoreService.RunAsync(request, progress, ct),
                ct).ConfigureAwait(true);
            // Stale-op guard: if a newer run kicked off mid-flight,
            // ApplySuccess will no-op and the new run owns the state.
            string html;
            try
            {
                string md = await File.ReadAllTextAsync(result.ReportPath, ct).ConfigureAwait(true);
                html = MarkdownReportRenderer.RenderToHtml(md);
            }
            catch (OperationCanceledException)
            {
                // GPT-5.5 slice-26 IMPORTANT #2: cancellation must
                // propagate to the outer handler so the run shows as
                // cancelled, not as a stale-success with an error pane.
                throw;
            }
            catch (Exception ex)
            {
                html = MarkdownReportRenderer.RenderError(
                    $"Failed to read report at {result.ReportPath}: {ex.Message}");
            }
            Score.ApplySuccess(version, result, html);
        }
        catch (OperationCanceledException)
        {
            Score.ApplyCancelled(version);
        }
        catch (Exception ex)
        {
            Score.ApplyFailure(version, ex.Message);
        }
        finally
        {
            RaiseNavigationCanExecuteChanged();
        }
    }
    private bool CanRunScore() =>
        !Score.IsRunning
        && !string.IsNullOrWhiteSpace(Score.EvalSetPath);

    [RelayCommand(CanExecute = nameof(CanCancelScore))]
    private void CancelScore()
    {
        _scoreCts?.Cancel();
    }
    private bool CanCancelScore() => Score.IsRunning;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        // Snapshot state at click-time so subsequent UI edits don't affect a running job.
        var request = new JobRequest
        {
            Paths = DatasetPicker.Selection.ToList(),
            Description = Describe.Description,
            Count = Describe.Count,
            Extensions = Describe.Extensions,
            Provider = Describe.Provider,
            Model = Describe.EffectiveModel,
            M365TenantId = string.IsNullOrWhiteSpace(Describe.M365TenantId) ? null : Describe.M365TenantId,
            ConnectorSchemaPath = string.IsNullOrWhiteSpace(Describe.ConnectorSchemaPath) ? null : Describe.ConnectorSchemaPath,
            WorkspaceRoot = _workspaceRoot,
        };

        // Reset progress VM for a fresh run.
        Progress.LogLines.Clear();
        Progress.IsRunning = true;
        Progress.IsComplete = false;
        Progress.IsFailed = false;
        Progress.Percent = null;
        Progress.Phase = "Starting";
        Progress.StatusMessage = "Starting…";
        Progress.OutputCsvPath = null;
        Progress.OutputSidecarPath = null;
        Progress.OutputReviewPath = null;
        Progress.OutputDirectory = null;
        Progress.AppendLog($"[{DateTime.Now:HH:mm:ss}] Starting job…");

        CurrentStep = WizardStep.Step3Progress;

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        // Progress<T> captures SynchronizationContext.Current at construction,
        // so creating it on the UI thread auto-marshals callbacks back to UI.
        var progress = new Progress<JobProgress>(p =>
        {
            Progress.Phase = p.Phase;
            if (p.Percent is not null) Progress.Percent = p.Percent;
            // Capture the job directory as soon as the service reports it
            // (in the "Starting" tick) so the user can open the partial
            // output folder even if the job is cancelled or fails before
            // the success path runs.
            if (!string.IsNullOrEmpty(p.JobDirectory))
            {
                Progress.OutputDirectory = p.JobDirectory;
            }
            if (!string.IsNullOrEmpty(p.Message))
            {
                Progress.StatusMessage = p.Message;
                Progress.AppendLog($"[{DateTime.Now:HH:mm:ss}] {p.Phase}: {p.Message}");
            }
        });

        try
        {
            // Reader is synchronous; push the whole job off the UI thread.
            var result = await Task.Run(
                () => _jobService.RunAsync(request, progress, ct),
                ct).ConfigureAwait(true);

            Progress.IsRunning = false;
            Progress.IsComplete = true;
            Progress.Percent = 100;
            Progress.Phase = "Complete";
            Progress.StatusMessage = $"Generated {result.ItemsGenerated} item(s).";
            Progress.OutputCsvPath = result.CsvPath;
            Progress.OutputSidecarPath = result.SidecarPath;
            Progress.OutputReviewPath = result.ReviewPath;
            Progress.OutputDirectory = result.JobDirectory;
            Progress.AppendLog($"[{DateTime.Now:HH:mm:ss}] Complete: {result.ItemsGenerated} item(s) in {result.JobDirectory}");
        }
        catch (OperationCanceledException)
        {
            Progress.IsRunning = false;
            Progress.IsFailed = false;
            Progress.Phase = "Cancelled";
            Progress.StatusMessage = "Cancelled by user.";
            Progress.AppendLog($"[{DateTime.Now:HH:mm:ss}] Cancelled.");
        }
        catch (Exception ex)
        {
            Progress.IsRunning = false;
            Progress.IsFailed = true;
            Progress.Phase = "Failed";
            Progress.StatusMessage = ex.Message;
            Progress.AppendLog($"[{DateTime.Now:HH:mm:ss}] Failed: {ex.Message}");
        }
        finally
        {
            RaiseNavigationCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _runCts?.Cancel();
    }
    private bool CanCancel() => Progress.IsRunning;

    [RelayCommand]
    private void StartOver()
    {
        if (Progress.IsRunning)
        {
            _runCts?.Cancel();
        }
        if (Score.IsRunning)
        {
            _scoreCts?.Cancel();
        }
        Editor.Reset();
        Score.BumpRunVersion();
        Score.ResetRunState();
        Score.EvalSetPath = null;
        Score.OutputDirectory = null;
        Score.IsRunning = false;
        CurrentStep = WizardStep.Step1Dataset;
    }

    partial void OnCurrentStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(IsStep1Visible));
        OnPropertyChanged(nameof(IsStep2Visible));
        OnPropertyChanged(nameof(IsStep3Visible));
        OnPropertyChanged(nameof(IsStep4Visible));
        OnPropertyChanged(nameof(IsStep5Visible));
        OnPropertyChanged(nameof(CurrentStepNumber));
        OnPropertyChanged(nameof(StepHeader));
        RaiseNavigationCanExecuteChanged();
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DatasetPickerViewModel
            && e.PropertyName == nameof(DatasetPickerViewModel.HasSelection))
        {
            RaiseNavigationCanExecuteChanged();
        }
        else if (sender is DescribeViewModel
            && (e.PropertyName == nameof(DescribeViewModel.HasDescription)
                || e.PropertyName == nameof(DescribeViewModel.CountInRange)))
        {
            RaiseNavigationCanExecuteChanged();
        }
    }

    private void OnProgressPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgressViewModel.IsRunning)
            || e.PropertyName == nameof(ProgressViewModel.IsComplete)
            || e.PropertyName == nameof(ProgressViewModel.OutputCsvPath))
        {
            RaiseNavigationCanExecuteChanged();
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EvalEditorViewModel.IsSaving)
            || e.PropertyName == nameof(EvalEditorViewModel.IsLoading)
            || e.PropertyName == nameof(EvalEditorViewModel.HasDirtyRows))
        {
            RaiseNavigationCanExecuteChanged();
        }
    }

    private void OnScorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScoreViewModel.IsRunning)
            || e.PropertyName == nameof(ScoreViewModel.IsComplete)
            || e.PropertyName == nameof(ScoreViewModel.EvalSetPath))
        {
            RaiseNavigationCanExecuteChanged();
        }
    }

    private void RaiseNavigationCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGenerate));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        GoToEditorCommand.NotifyCanExecuteChanged();
        GoToScoreCommand.NotifyCanExecuteChanged();
        RunScoreCommand.NotifyCanExecuteChanged();
        CancelScoreCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _scoreCts?.Cancel();
        _scoreCts?.Dispose();
        _scoreCts = null;
        GC.SuppressFinalize(this);
    }
}
