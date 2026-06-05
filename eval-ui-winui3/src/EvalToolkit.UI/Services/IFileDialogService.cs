using System.Collections.Generic;
using System.Threading.Tasks;

namespace EvalToolkit.UI.Services;

/// <summary>
/// UI-free file-dialog abstraction so view models can show pickers
/// without taking a hard dependency on WinUI's <c>FileOpenPicker</c> /
/// <c>FolderPicker</c> / <c>InitializeWithWindow</c>. The concrete
/// <see cref="FileDialogService"/> is HWND-bound; tests can supply a
/// fake that returns canned paths.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Show a multi-file picker. Returns an empty list when the user
    /// cancels.
    /// </summary>
    /// <param name="fileTypes">File extensions (with leading dot, e.g. ".csv") to filter by, or null for all files.</param>
    Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<string>? fileTypes = null);

    /// <summary>
    /// Show a folder picker. Returns <c>null</c> when the user cancels.
    /// </summary>
    Task<string?> PickFolderAsync();
}
