using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.UI.Services;
using Microsoft.UI.Dispatching;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// State for wizard step 5 — score a generated EvalSet. Mirrors the
/// existing <see cref="ProgressViewModel"/> pattern: bindable form
/// inputs + IsRunning/IsComplete/IsFailed flags + log buffer + Open
/// buttons. The Run action is owned by the wizard VM (which holds the
/// service); this VM only exposes form state + result state.
///
/// <para>Cancellation / stale-op guard (GPT-5.5 plan-review #4): the
/// wizard tracks an incrementing <see cref="RunVersion"/> handed to
/// progress callbacks. The VM ignores any progress whose version is
/// not current, so a canceled-but-still-winding-down run cannot
/// overwrite the state of the newer run.</para>
/// </summary>
public partial class ScoreViewModel : ObservableObject
{
    public const int MaxLogLines = 500;

    private readonly DispatcherQueue _dispatcher;

    /// <summary>Sidecar (.evalgen.json) absolute path. Set by the wizard.</summary>
    [ObservableProperty]
    public partial string? EvalSetPath { get; set; }

    /// <summary>Output directory the report + scored CSV are written to.</summary>
    [ObservableProperty]
    public partial string? OutputDirectory { get; set; }

    // ---- Form inputs (Step 5 panel) ----

    [ObservableProperty]
    public partial string? ConnectorId { get; set; }

    [ObservableProperty]
    public partial string? TenantId { get; set; }

    /// <summary>Pass threshold 0..100 (default 70 mirrors Electron UI).</summary>
    [ObservableProperty]
    public partial double Threshold { get; set; } = 70;

    [ObservableProperty]
    public partial string? SystemPrompt { get; set; }

    [ObservableProperty]
    public partial string? M365AgentId { get; set; }

    [ObservableProperty]
    public partial string? JudgeAgentId { get; set; }

    /// <summary>
    /// Judge provider as a wire string (default "workiq"). The UI bows
    /// to a ComboBox; the wizard maps to <see cref="JudgeProvider"/>
    /// at run time.
    /// </summary>
    [ObservableProperty]
    public partial string JudgeProviderWire { get; set; } = "workiq";

    // ---- Run state ----

    [ObservableProperty]
    public partial string Phase { get; set; } = "Idle";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Configure scoring and run.";

    [ObservableProperty]
    public partial int? Percent { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsComplete { get; set; }

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? ReportPath { get; set; }

    [ObservableProperty]
    public partial string? ScoredCsvPath { get; set; }

    [ObservableProperty]
    public partial string? ReportHtml { get; set; }

    [ObservableProperty]
    public partial int TotalScored { get; set; }

    [ObservableProperty]
    public partial int PassCount { get; set; }

    [ObservableProperty]
    public partial int FailCount { get; set; }

    [ObservableProperty]
    public partial double AverageScore { get; set; }

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>
    /// Monotonically incrementing version; bumped each time the wizard
    /// kicks off a new run. Progress / completion callbacks check this
    /// before mutating UI state so a stale run can't overwrite the
    /// newer run's report path or status.
    /// </summary>
    public long RunVersion { get; private set; }

    public ScoreViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>True when the report .md has been written and we have a path to open.</summary>
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    public bool HasCsv => !string.IsNullOrEmpty(ScoredCsvPath);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasOutputDirectory => !string.IsNullOrEmpty(OutputDirectory);

    public bool IsIndeterminate => IsRunning && Percent is null;

    public double PercentValue => Percent ?? 0;

    partial void OnReportPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasReport));
        OpenReportCommand.NotifyCanExecuteChanged();
    }

    partial void OnScoredCsvPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCsv));
        OpenScoredCsvCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(HasOutputDirectory));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    partial void OnPercentChanged(int? value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(PercentValue));
    }

    /// <summary>Reserve a new <see cref="RunVersion"/>; called by the wizard.</summary>
    public long BumpRunVersion() => ++RunVersion;

    /// <summary>Reset transient run state at the start of a new run.</summary>
    public void ResetRunState()
    {
        IsComplete = false;
        IsFailed = false;
        ErrorMessage = null;
        ReportPath = null;
        ScoredCsvPath = null;
        ReportHtml = null;
        TotalScored = 0;
        PassCount = 0;
        FailCount = 0;
        AverageScore = 0;
        Percent = null;
        StatusMessage = "Starting…";
        Phase = "Starting";
        LogLines.Clear();
    }

    /// <summary>
    /// Marshal a progress event onto the UI thread, ignoring it if the
    /// run that produced it has been superseded.
    /// </summary>
    public void ApplyProgress(long runVersion, JobProgress p)
    {
        if (runVersion != RunVersion) return;
        if (_dispatcher.HasThreadAccess)
        {
            ApplyProgressUiThread(runVersion, p);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyProgressUiThread(runVersion, p));
        }
    }

    private void ApplyProgressUiThread(long runVersion, JobProgress p)
    {
        if (runVersion != RunVersion) return;
        if (!string.IsNullOrEmpty(p.Phase)) Phase = p.Phase;
        if (p.Percent.HasValue) Percent = p.Percent;
        if (!string.IsNullOrEmpty(p.Message))
        {
            StatusMessage = p.Message!;
            AppendLog(p.Message!);
        }
    }

    public void ApplySuccess(long runVersion, EvalScoreResult result, string? renderedHtml)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ApplySuccessUiThread(runVersion, result, renderedHtml);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplySuccessUiThread(runVersion, result, renderedHtml));
        }
    }

    private void ApplySuccessUiThread(long runVersion, EvalScoreResult result, string? renderedHtml)
    {
        if (runVersion != RunVersion) return;
        IsRunning = false;
        IsComplete = true;
        IsFailed = false;
        ErrorMessage = null;
        ReportPath = result.ReportPath;
        ScoredCsvPath = result.ScoredCsvPath;
        ReportHtml = renderedHtml;
        TotalScored = result.TotalScored;
        PassCount = result.PassCount;
        FailCount = result.FailCount;
        AverageScore = result.AverageScore;
        Percent = 100;
        Phase = "Complete";
        StatusMessage = $"Done. Pass={result.PassCount} Fail={result.FailCount} Avg={result.AverageScore:0.##}";
    }

    public void ApplyFailure(long runVersion, string message)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ApplyFailureUiThread(runVersion, message);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyFailureUiThread(runVersion, message));
        }
    }

    private void ApplyFailureUiThread(long runVersion, string message)
    {
        if (runVersion != RunVersion) return;
        IsRunning = false;
        IsComplete = false;
        IsFailed = true;
        Phase = "Failed";
        StatusMessage = message;
        ErrorMessage = message;
        AppendLog("❌ " + message);
    }

    public void ApplyCancelled(long runVersion)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ApplyCancelledUiThread(runVersion);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyCancelledUiThread(runVersion));
        }
    }

    private void ApplyCancelledUiThread(long runVersion)
    {
        if (runVersion != RunVersion) return;
        IsRunning = false;
        IsComplete = false;
        IsFailed = false;
        Phase = "Cancelled";
        StatusMessage = "Scoring cancelled.";
        AppendLog("Cancelled by user.");
    }

    public void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (LogLines.Count >= MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
        LogLines.Add(line);
    }

    [RelayCommand(CanExecute = nameof(CanOpenReport))]
    private void OpenReport()
    {
        if (!string.IsNullOrEmpty(ReportPath)) ShellOpener.OpenFile(ReportPath);
    }
    private bool CanOpenReport() => HasReport;

    [RelayCommand(CanExecute = nameof(CanOpenScoredCsv))]
    private void OpenScoredCsv()
    {
        if (!string.IsNullOrEmpty(ScoredCsvPath)) ShellOpener.OpenFile(ScoredCsvPath);
    }
    private bool CanOpenScoredCsv() => HasCsv;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputDirectory)) ShellOpener.OpenFolder(OutputDirectory);
    }
    private bool CanOpenFolder() => HasOutputDirectory;

    /// <summary>
    /// Build a service request from the current form state. Throws
    /// <see cref="InvalidOperationException"/> if required fields are
    /// missing; the wizard surfaces the message as an InfoBar error.
    /// </summary>
    public EvalScoreRequest BuildRequest()
    {
        if (string.IsNullOrWhiteSpace(EvalSetPath))
        {
            throw new InvalidOperationException("No EvalSet sidecar selected.");
        }
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new InvalidOperationException("No output directory selected.");
        }
        if (!File.Exists(EvalSetPath))
        {
            throw new InvalidOperationException($"EvalSet sidecar not found: {EvalSetPath}");
        }
        JudgeProvider judge;
        try
        {
            judge = JudgeProviders.FromWireString(JudgeProviderWire);
        }
        catch (NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Unknown judge provider: '{JudgeProviderWire}'. Expected workiq | azure-openai | github-copilot.");
        }
        return new EvalScoreRequest
        {
            EvalSetPath = EvalSetPath!,
            OutputDir = OutputDirectory!,
            Threshold = Threshold,
            ConnectorId = string.IsNullOrWhiteSpace(ConnectorId) ? null : ConnectorId,
            TenantId = string.IsNullOrWhiteSpace(TenantId) ? null : TenantId,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
            JudgeProvider = judge,
            M365AgentId = string.IsNullOrWhiteSpace(M365AgentId) ? null : M365AgentId,
            JudgeAgentId = string.IsNullOrWhiteSpace(JudgeAgentId) ? null : JudgeAgentId,
            // UI default: skip preflight until slice 28 plumbs an EULA
            // ContentDialog (see EvalScoreJobService docs).
            SkipPreflight = true,
        };
    }
}
