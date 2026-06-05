using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Services;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Wizard step identifier. Slice 22 only renders steps 1 + 2; later
/// slices add the remaining three steps (progress, row editor, score).
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
/// Owns wizard navigation state and exposes child VMs for each step.
/// Stays pure — no references to Views, Windows, or Frames — so it
/// can be unit-tested without spinning up XAML.
/// </summary>
public partial class WizardViewModel : ObservableObject
{
    public DatasetPickerViewModel DatasetPicker { get; }
    public DescribeViewModel Describe { get; }

    public WizardViewModel(IFileDialogService dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        DatasetPicker = new DatasetPickerViewModel(dialog);
        Describe = new DescribeViewModel();
        CurrentStep = WizardStep.Step1Dataset;

        // Recompute step visibility flags + command CanExecute whenever
        // CurrentStep flips.
        PropertyChanged += OnSelfPropertyChanged;

        // Cross-step validation: Generate enables only when both step 1
        // and step 2 are satisfied.
        DatasetPicker.PropertyChanged += OnChildPropertyChanged;
        Describe.PropertyChanged += OnChildPropertyChanged;
    }

    [ObservableProperty]
    public partial WizardStep CurrentStep { get; set; }

    public bool IsStep1Visible => CurrentStep == WizardStep.Step1Dataset;
    public bool IsStep2Visible => CurrentStep == WizardStep.Step2Describe;

    public int CurrentStepNumber => (int)CurrentStep + 1;
    public string StepHeader => $"Step {CurrentStepNumber} of 5";

    public bool CanGoNext => CurrentStep switch
    {
        WizardStep.Step1Dataset => DatasetPicker.HasSelection,
        // Steps 3..5 don't exist as VMs yet; Next is disabled past step 2
        // (Generate replaces it).
        _ => false,
    };

    public bool CanGoBack => CurrentStep == WizardStep.Step2Describe;

    public bool CanGenerate =>
        CurrentStep == WizardStep.Step2Describe
        && DatasetPicker.HasSelection
        && Describe.HasDescription
        && Describe.CountInRange;

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
        if (CurrentStep == WizardStep.Step2Describe)
        {
            CurrentStep = WizardStep.Step1Dataset;
        }
    }

    /// <summary>
    /// Slice-22 placeholder: the user clicked Generate but the
    /// pipeline isn't wired yet (that lands in slice 23). The wizard
    /// stays on step 2 — advancing to <see cref="WizardStep.Step3Progress"/>
    /// would dead-end the user because no progress panel exists yet.
    /// </summary>
    public bool IsGenerationPending { get; private set; }

    /// <summary>
    /// Bound by an InfoBar on step 2 — empty until Generate is clicked,
    /// at which point it surfaces a one-line "wired in slice 23"
    /// message so the click isn't silently dropped.
    /// </summary>
    public string GenerationPendingMessage { get; private set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        // Stay on step 2 — advancing to a step that has no panel would
        // leave the user on a blank screen with no back path. Slice 23
        // will replace this with the actual pipeline invocation and
        // navigation to a populated progress panel.
        IsGenerationPending = true;
        GenerationPendingMessage =
            "Your selections were captured. Pipeline execution will land in the next update.";
        OnPropertyChanged(nameof(IsGenerationPending));
        OnPropertyChanged(nameof(GenerationPendingMessage));
    }

    partial void OnCurrentStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(IsStep1Visible));
        OnPropertyChanged(nameof(IsStep2Visible));
        OnPropertyChanged(nameof(CurrentStepNumber));
        OnPropertyChanged(nameof(StepHeader));
        RaiseNavigationCanExecuteChanged();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Already handled in OnCurrentStepChanged; no-op here.
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DatasetPickerViewModel
            && (e.PropertyName == nameof(DatasetPickerViewModel.HasSelection)))
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

    private void RaiseNavigationCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGenerate));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }
}
