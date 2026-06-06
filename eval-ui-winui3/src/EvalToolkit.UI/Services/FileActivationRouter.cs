using System;
using System.Diagnostics;
using System.IO;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="IFileActivationRouter"/>. Classifies the path
/// by extension and either navigates the wizard (sidecar) or hands off
/// to a non-app-owned shell handler (legacy or alias-companion files).
///
/// Slice 30 design notes (GPT-5.5 plan-review BLOCKER #1 fix):
/// - App-owned alias extensions (<c>.evalgenset</c>, <c>.evalscoreresults</c>,
///   <c>.evalreport</c>) MUST NOT be passed back to
///   <see cref="ShellOpener.OpenFile(string)"/> directly, because once slice
///   31 registers them as FTAs, ShellExecute would re-launch EvalToolkit
///   into a loop. We always route the alias to its legacy companion
///   (<c>.csv</c>, <c>.md</c>) before invoking the shell.
/// - Legacy artifact suffixes (<c>-results.csv</c>, <c>-report.md</c>) are
///   NOT app-owned and stay safe with ShellOpener.
/// - Unknown extensions fall through to ShellOpener as well — the worst
///   case is a "no app to open this" system dialog, never a loop.
/// </summary>
public sealed class FileActivationRouter : IFileActivationRouter
{
    private readonly NavigationService _navigation;
    private readonly Action<string> _openInDefaultApp;
    private readonly Action<string>? _onWarning;

    private const string SidecarLegacySuffix = ".evalgen.json";
    private const string SidecarAliasExtension = ".evalgenset";
    private const string ScoredCsvAliasExtension = ".evalscoreresults";
    private const string ReportAliasExtension = ".evalreport";
    private const string LegacyScoredCsvSuffix = "-results.csv";
    private const string LegacyReportSuffix = "-report.md";

    public FileActivationRouter(
        NavigationService navigation,
        Action<string> openInDefaultApp,
        Action<string>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(openInDefaultApp);
        _navigation = navigation;
        _openInDefaultApp = openInDefaultApp;
        _onWarning = onWarning;
    }

    public bool Route(string filePath, out bool needsNavigationQueue)
    {
        needsNavigationQueue = false;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _onWarning?.Invoke("FileActivationRouter: empty path");
            return true;
        }

        var kind = Classify(filePath);
        switch (kind)
        {
            case FileKind.Sidecar:
                return RouteSidecar(filePath, out needsNavigationQueue);

            case FileKind.ScoredCsvAlias:
                RouteAliasCompanion(filePath, ScoredCsvAliasExtension, ".csv");
                return true;

            case FileKind.ReportAlias:
                RouteAliasCompanion(filePath, ReportAliasExtension, ".md");
                return true;

            case FileKind.LegacyScoredCsv:
            case FileKind.LegacyReport:
            case FileKind.Unknown:
            default:
                SafeOpen(filePath);
                return true;
        }
    }

    private bool RouteSidecar(string sidecarPath, out bool needsNavigationQueue)
    {
        needsNavigationQueue = false;
        string csvPath = DeriveCsvFromSidecar(sidecarPath);
        if (!File.Exists(csvPath))
        {
            _onWarning?.Invoke(
                $"FileActivationRouter: sidecar '{sidecarPath}' has no CSV companion at '{csvPath}'; opening sidecar in default app instead.");
            // Sidecar legacy is .json — safe for ShellOpener. Sidecar alias
            // (.evalgenset) is app-owned — we'd loop. So skip the shell call
            // for the alias case and just log.
            if (!sidecarPath.EndsWith(SidecarAliasExtension, StringComparison.OrdinalIgnoreCase))
            {
                SafeOpen(sidecarPath);
            }
            return true;
        }

        string outputDir = Path.GetDirectoryName(csvPath) ?? string.Empty;
        var request = new OpenEvalSetRequest(sidecarPath, csvPath, outputDir);
        bool navigated = _navigation.NavigateTo("Wizard", request);
        if (!navigated)
        {
            needsNavigationQueue = true;
            return false;
        }
        return true;
    }

    private void RouteAliasCompanion(string aliasPath, string aliasExtension, string companionExtension)
    {
        string companion = ReplaceTrailingExtension(aliasPath, aliasExtension, companionExtension);
        if (File.Exists(companion))
        {
            SafeOpen(companion);
            return;
        }
        _onWarning?.Invoke(
            $"FileActivationRouter: alias '{aliasPath}' has no companion at '{companion}'; cannot open.");
    }

    private void SafeOpen(string path)
    {
        try
        {
            _openInDefaultApp(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FileActivationRouter.SafeOpen failed for '{path}': {ex}");
            _onWarning?.Invoke($"FileActivationRouter: shell-open failed for '{path}': {ex.Message}");
        }
    }

    internal enum FileKind
    {
        Unknown,
        Sidecar,
        ScoredCsvAlias,
        ReportAlias,
        LegacyScoredCsv,
        LegacyReport,
    }

    internal static FileKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return FileKind.Unknown;

        // Order matters: legacy suffixes (e.g. "-results.csv") must be
        // checked BEFORE the bare ".csv" fall-through. App-owned alias
        // extensions are explicit, so order between aliases is moot.
        if (path.EndsWith(SidecarLegacySuffix, StringComparison.OrdinalIgnoreCase)) return FileKind.Sidecar;
        if (path.EndsWith(SidecarAliasExtension, StringComparison.OrdinalIgnoreCase)) return FileKind.Sidecar;
        if (path.EndsWith(ScoredCsvAliasExtension, StringComparison.OrdinalIgnoreCase)) return FileKind.ScoredCsvAlias;
        if (path.EndsWith(ReportAliasExtension, StringComparison.OrdinalIgnoreCase)) return FileKind.ReportAlias;
        if (path.EndsWith(LegacyScoredCsvSuffix, StringComparison.OrdinalIgnoreCase)) return FileKind.LegacyScoredCsv;
        if (path.EndsWith(LegacyReportSuffix, StringComparison.OrdinalIgnoreCase)) return FileKind.LegacyReport;
        return FileKind.Unknown;
    }

    /// <summary>
    /// Derive the CSV companion for either legacy sidecar
    /// (<c>foo.evalgen.json</c> → <c>foo.csv</c>) or alias sidecar
    /// (<c>foo.evalgenset</c> → <c>foo.csv</c>). Uses suffix stripping
    /// rather than <see cref="Path.ChangeExtension(string?, string?)"/>
    /// because the legacy form is a double-extension that the BCL
    /// helper would not handle correctly.
    /// </summary>
    internal static string DeriveCsvFromSidecar(string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        if (sidecarPath.EndsWith(SidecarLegacySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                sidecarPath.AsSpan(0, sidecarPath.Length - SidecarLegacySuffix.Length),
                ".csv");
        }
        if (sidecarPath.EndsWith(SidecarAliasExtension, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(
                sidecarPath.AsSpan(0, sidecarPath.Length - SidecarAliasExtension.Length),
                ".csv");
        }
        throw new ArgumentException(
            $"Not a recognised sidecar extension: '{sidecarPath}'.", nameof(sidecarPath));
    }

    internal static string ReplaceTrailingExtension(string path, string trailing, string replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(trailing);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!path.EndsWith(trailing, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Path '{path}' does not end with '{trailing}'.", nameof(path));
        }
        return string.Concat(
            path.AsSpan(0, path.Length - trailing.Length),
            replacement);
    }
}
