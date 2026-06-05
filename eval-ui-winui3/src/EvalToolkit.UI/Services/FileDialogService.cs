using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EvalToolkit.UI.Services;

/// <summary>
/// HWND-bound implementation of <see cref="IFileDialogService"/>.
///
/// <para>
/// WinUI 3 unpackaged apps MUST attach an HWND to every picker via
/// <see cref="InitializeWithWindow.Initialize"/> before calling
/// <c>Pick*Async</c>, otherwise the call fails with a
/// "no_window_handle" exception. We resolve the HWND lazily through a
/// <see cref="Func{TResult}"/> so this service can be constructed before
/// the ShellWindow exists (and so the VM never has to touch a Window
/// instance).
/// </para>
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    private readonly Func<IntPtr> _hwndProvider;

    public FileDialogService(Func<IntPtr> hwndProvider)
    {
        _hwndProvider = hwndProvider ?? throw new ArgumentNullException(nameof(hwndProvider));
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<string>? fileTypes = null)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Desktop,
        };

        AddFileTypes(picker, fileTypes);
        InitializeWithWindow.Initialize(picker, _hwndProvider());

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return Array.Empty<string>();
        }

        return files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Desktop,
        };

        // FolderPicker requires at least one FileTypeFilter; "*" means
        // "show me all subitems for selection purposes".
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwndProvider());

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static void AddFileTypes(FileOpenPicker picker, IEnumerable<string>? fileTypes)
    {
        var added = false;
        if (fileTypes is not null)
        {
            foreach (var raw in fileTypes)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                var ext = raw.Trim();
                if (!ext.StartsWith('.'))
                {
                    ext = "." + ext;
                }
                picker.FileTypeFilter.Add(ext.ToLowerInvariant());
                added = true;
            }
        }
        if (!added)
        {
            // FileOpenPicker also requires at least one filter; "*" means
            // any extension.
            picker.FileTypeFilter.Add("*");
        }
    }
}
