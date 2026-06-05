using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Services;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// State for wizard step 3 — live progress while
/// <see cref="IEvalGenJobService"/> runs, plus completion summary +
/// Open buttons. Pure CLR; the wizard VM constructs and updates
/// it from a UI-thread <see cref="System.Progress{T}"/> callback so
/// no DispatcherQueue plumbing is needed here.
/// </summary>
public partial class ProgressViewModel : ObservableObject
{
    /// <summary>Soft cap on the log buffer so long jobs don't grow without bound.</summary>
    public const int MaxLogLines = 500;

    [ObservableProperty]
    public partial string Phase { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial int? Percent { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsComplete { get; set; }

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    [ObservableProperty]
    public partial string? OutputCsvPath { get; set; }

    [ObservableProperty]
    public partial string? OutputSidecarPath { get; set; }

    [ObservableProperty]
    public partial string? OutputReviewPath { get; set; }

    [ObservableProperty]
    public partial string? OutputDirectory { get; set; }

    public ObservableCollection<string> LogLines { get; } = new();

    public ProgressViewModel()
    {
        Phase = "Idle";
        StatusMessage = "Waiting…";
    }

    /// <summary>
    /// XAML-friendly derived flags. Bind ProgressBar's
    /// <c>IsIndeterminate</c> to <see cref="IsIndeterminate"/> and
    /// <c>Value</c> to <see cref="PercentValue"/> so we don't hand a
    /// nullable straight to a non-nullable XAML property.
    /// </summary>
    public bool IsIndeterminate => IsRunning && Percent is null;

    /// <summary>0..100 clamp of <see cref="Percent"/> for ProgressBar binding.</summary>
    public double PercentValue => Percent ?? 0;

    /// <summary>
    /// True when at least one Open button should be visible
    /// (success or partial — failed jobs still leave a job folder
    /// behind that the user may want to inspect).
    /// </summary>
    public bool HasOutputs => !string.IsNullOrEmpty(OutputDirectory);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    partial void OnPercentChanged(int? value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(PercentValue));
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(HasOutputs));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputCsvPathChanged(string? value)
    {
        OpenCsvCommand.NotifyCanExecuteChanged();
        RevealCsvCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputReviewPathChanged(string? value)
    {
        OpenReviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Append a single log line, evicting the oldest if we hit the
    /// soft cap to keep the ItemsRepeater responsive.
    /// </summary>
    public void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (LogLines.Count >= MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
        LogLines.Add(line);
    }

    [RelayCommand(CanExecute = nameof(CanOpenCsv))]
    private void OpenCsv()
    {
        if (!string.IsNullOrEmpty(OutputCsvPath)) ShellOpener.OpenFile(OutputCsvPath);
    }
    private bool CanOpenCsv() => !string.IsNullOrEmpty(OutputCsvPath);

    [RelayCommand(CanExecute = nameof(CanOpenReview))]
    private void OpenReview()
    {
        if (!string.IsNullOrEmpty(OutputReviewPath)) ShellOpener.OpenFile(OutputReviewPath);
    }
    private bool CanOpenReview() => !string.IsNullOrEmpty(OutputReviewPath);

    [RelayCommand(CanExecute = nameof(CanRevealCsv))]
    private void RevealCsv()
    {
        if (!string.IsNullOrEmpty(OutputCsvPath)) ShellOpener.RevealInFolder(OutputCsvPath);
    }
    private bool CanRevealCsv() => !string.IsNullOrEmpty(OutputCsvPath);

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputDirectory)) ShellOpener.OpenFolder(OutputDirectory);
    }
    private bool CanOpenFolder() => !string.IsNullOrEmpty(OutputDirectory);
}
