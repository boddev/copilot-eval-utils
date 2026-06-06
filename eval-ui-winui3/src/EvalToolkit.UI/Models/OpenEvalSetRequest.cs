namespace EvalToolkit.UI.Models;

/// <summary>
/// Navigation parameter delivered to <see cref="Views.WizardView"/> by
/// <see cref="Services.FileActivationRouter"/> when the user opens an
/// existing eval-set file (sidecar JSON or its app-owned alias). The
/// wizard hydrates Progress + Score state from these paths and lands
/// the user in Step 4 (row editor) with the CSV pre-loaded.
/// </summary>
public sealed record OpenEvalSetRequest(
    string SidecarPath,
    string CsvPath,
    string OutputDirectory);
