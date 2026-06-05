using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Pure helper that converts a XAML <see cref="DataPackageView"/> into
/// a list of <see cref="DatasetPath"/> records. We deliberately do
/// NOT enumerate folder contents here — that's the pipeline's job in
/// slice 23. We just remember whether each dropped item was a file or
/// a folder.
///
/// <para>
/// Virtual / cloud-provider-backed items can return an empty
/// <c>Path</c>; those entries are silently skipped (the user can
/// always re-drop from a real filesystem location).
/// </para>
/// </summary>
public static class DragDropHelper
{
    public static async Task<IReadOnlyList<DatasetPath>> ExtractPathsAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return Array.Empty<DatasetPath>();
        }

        var items = await view.GetStorageItemsAsync();
        if (items is null || items.Count == 0)
        {
            return Array.Empty<DatasetPath>();
        }

        var result = new List<DatasetPath>(items.Count);
        foreach (var item in items)
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            DatasetPathKind kind = item switch
            {
                StorageFolder => DatasetPathKind.Folder,
                StorageFile => DatasetPathKind.File,
                // Fall back to filesystem inspection when the WinRT type
                // is something exotic (e.g. virtual items that still
                // resolve to a real path).
                _ => Directory.Exists(path) ? DatasetPathKind.Folder : DatasetPathKind.File,
            };

            result.Add(new DatasetPath(path, kind));
        }
        return result;
    }
}
