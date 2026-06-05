using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.Services;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// State for wizard step 1 — dataset selection. Wraps an
/// <see cref="ObservableCollection{T}"/> of selected paths so the
/// XAML <see cref="ListView"/> updates incrementally without rebinding.
/// Selection is additive: pickers and drag-drop append to the list;
/// the user can clear the whole thing or remove individual rows.
/// </summary>
public partial class DatasetPickerViewModel : ObservableObject
{
    private readonly IFileDialogService _dialog;

    public ObservableCollection<DatasetPath> Selection { get; } = new();

    public DatasetPickerViewModel(IFileDialogService dialog)
    {
        _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
        Selection.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Summary));
            ClearCommand.NotifyCanExecuteChanged();
        };
    }

    public bool HasSelection => Selection.Count > 0;

    public string Summary
    {
        get
        {
            if (Selection.Count == 0)
            {
                return "No dataset selected yet.";
            }
            var files = Selection.Count(p => p.Kind == DatasetPathKind.File);
            var folders = Selection.Count(p => p.Kind == DatasetPathKind.Folder);
            return $"{files} file{(files == 1 ? "" : "s")}, {folders} folder{(folders == 1 ? "" : "s")} selected.";
        }
    }

    [RelayCommand]
    private async Task BrowseFilesAsync()
    {
        var picked = await _dialog.PickFilesAsync();
        foreach (var path in picked)
        {
            AddPath(path, DatasetPathKind.File);
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var picked = await _dialog.PickFolderAsync();
        if (!string.IsNullOrEmpty(picked))
        {
            AddPath(picked, DatasetPathKind.Folder);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Clear()
    {
        Selection.Clear();
    }

    [RelayCommand]
    private void Remove(DatasetPath? path)
    {
        if (path is not null)
        {
            Selection.Remove(path);
        }
    }

    /// <summary>
    /// Called by the View after a drag-drop has been parsed into
    /// <see cref="DatasetPath"/> records. Public so the View
    /// code-behind can append without needing a command parameter
    /// converter for the entire drop payload.
    /// </summary>
    public void AppendFromDrop(IEnumerable<DatasetPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var p in paths)
        {
            AddPath(p.Path, p.Kind);
        }
    }

    private void AddPath(string path, DatasetPathKind kind)
    {
        // Normalize and dedupe — two pickers can return the same file,
        // and a drag-drop can re-add a folder the user already picked.
        var normalized = TryNormalize(path);
        if (normalized is null)
        {
            return;
        }
        foreach (var existing in Selection)
        {
            if (string.Equals(existing.Path, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        Selection.Add(new DatasetPath(normalized, kind));
    }

    private static string? TryNormalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return null;
        }
    }
}
