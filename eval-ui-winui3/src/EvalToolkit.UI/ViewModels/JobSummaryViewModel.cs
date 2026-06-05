using System;
using System.Globalization;
using EvalToolkit.Jobs;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// Thin view-projection of <see cref="JobSummary"/> for the sidebar list.
/// Pre-formats display strings so XAML bindings don't need converters
/// for every column.
/// </summary>
public sealed class JobSummaryViewModel
{
    public JobSummaryViewModel(JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
    }

    public JobSummary Summary { get; }

    public string JobId => Summary.JobId;
    public string FolderPath => Summary.Path;
    public string DisplayName => Summary.DisplayName;
    public JobStatus Status => Summary.Status;
    public bool HasWarnings => Summary.HasWarnings;

    /// <summary>Local-time formatted timestamp shown beneath the title.</summary>
    public string CreatedDisplay =>
        Summary.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// Single-character status icon. Keeps the sidebar legible without
    /// pulling in a converter library or icon font for slice 24.
    /// </summary>
    public string StatusGlyph => Summary.Status switch
    {
        JobStatus.InProgress => "⏳",
        JobStatus.Complete => "✓",
        JobStatus.Failed => "✗",
        JobStatus.Cancelled => "⊘",
        _ => "?",
    };

    public string CountsDisplay
    {
        get
        {
            if (Summary.RecordsRead is null && Summary.ItemsGenerated is null)
            {
                return string.Empty;
            }
            string read = Summary.RecordsRead?.ToString(CultureInfo.CurrentCulture) ?? "—";
            string gen = Summary.ItemsGenerated?.ToString(CultureInfo.CurrentCulture) ?? "—";
            return $"{read} read · {gen} generated";
        }
    }
}
