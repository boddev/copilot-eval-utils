namespace EvalToolkit.UI.Models;

/// <summary>
/// Kind of filesystem entry the user selected for an evaluation set.
/// </summary>
public enum DatasetPathKind
{
    File,
    Folder,
}

/// <summary>
/// One row of the user's dataset selection. We deliberately do NOT
/// expand folders into individual files at selection time — that
/// happens inside <c>DatasetReader</c> when the pipeline runs in
/// slice 23. Storing the original folder path here lets the user see
/// what they picked / can remove the whole folder in one click.
/// </summary>
/// <param name="Path">Absolute filesystem path.</param>
/// <param name="Kind">Whether the path points at a single file or a folder.</param>
public sealed record DatasetPath(string Path, DatasetPathKind Kind);
