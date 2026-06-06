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
    private readonly string _workspaceRoot;
    private CancellationTokenSource? _runCts;
    private bool _disposed;

    public DatasetPickerViewModel DatasetPicker { get; }
    public DescribeViewModel Describe { get; }
    public ProgressViewModel Progress { get; }
    public EvalEditorViewModel Editor { get; }

    public WizardViewModel(
        IFileDialogService dialog,
        IEvalGenJobService jobService,
        string workspaceRoot,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(jobService);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _jobService = jobService;
        _workspaceRoot = workspaceRoot;

        DatasetPicker = new DatasetPickerViewModel(dialog);
        Describe = new DescribeViewModel();
        Progress = new ProgressViewModel();
        Editor = new EvalEditorViewModel(dispatcher);
        CurrentStep = WizardStep.Step1Dataset;

        DatasetPicker.PropertyChanged += OnChildPropertyChanged;
        Describe.PropertyChanged += OnChildPropertyChanged;
        Progress.PropertyChanged += OnProgressPropertyChanged;
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    [ObservableProperty]
    public partial WizardStep CurrentStep { get; set; }

    public bool IsStep1Visible => CurrentStep == WizardStep.Step1Dataset;
    public bool IsStep2Visible => CurrentStep == WizardStep.Step2Describe;
    public bool IsStep3Visible => CurrentStep == WizardStep.Step3Progress;
    public bool IsStep4Visible => CurrentStep == WizardStep.Step4Editor;

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
            Model = Describe.Model,
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
        Editor.Reset();
        CurrentStep = WizardStep.Step1Dataset;
    }

    partial void OnCurrentStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(IsStep1Visible));
        OnPropertyChanged(nameof(IsStep2Visible));
        OnPropertyChanged(nameof(IsStep3Visible));
        OnPropertyChanged(nameof(IsStep4Visible));
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
            || e.PropertyName == nameof(EvalEditorViewModel.IsLoading))
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        GC.SuppressFinalize(this);
    }
}
