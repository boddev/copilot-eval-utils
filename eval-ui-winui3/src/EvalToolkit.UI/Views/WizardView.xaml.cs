using System;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.Services;
using EvalToolkit.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EvalToolkit.UI.Views;

/// <summary>
/// Wizard page hosting steps 1 + 2. Code-behind is intentionally thin:
/// only drag-drop event plumbing (which can't live on a VM) and a
/// schema-browse click handler that needs the dialog service.
/// </summary>
public sealed partial class WizardView : Page
{
    // CA1861: avoid allocating the filter array on each schema-browse click.
    private static readonly string[] SchemaFileTypes = { ".json" };

    public WizardViewModel ViewModel { get; }

    public WizardView()
    {
        InitializeComponent();

        // VM is created here once so step 1/2 state survives navigation
        // away and back; we don't recreate on Loaded.
        ViewModel = new WizardViewModel(
            App.Current.FileDialog,
            App.Current.JobService,
            App.Current.WorkspaceRoot,
            DispatcherQueue);
        DataContext = ViewModel;

        // Slice 23: auto-scroll the live log to the latest line as
        // entries arrive. Hook the VM's collection so the ScrollViewer
        // pins to the bottom (mirrors terminal tailing behavior).
        ViewModel.Progress.LogLines.CollectionChanged += (_, _) =>
        {
            // ScrollViewer.ChangeView must run on the UI thread; the
            // ObservableCollection raises on whichever context added
            // the item — which is the UI thread because the Progress<T>
            // callback marshals there. Still, defer via DispatcherQueue
            // so the ItemsRepeater has a chance to materialize the new
            // item before we measure ScrollableHeight.
            DispatcherQueue.TryEnqueue(() =>
            {
                LogScrollViewer?.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
            });
        };
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        // Copy semantics are the only ones that make sense for opening
        // files into an eval set — we never want to "move" the file.
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Add to dataset";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
            e.Handled = true;
        }
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        // GetStorageItemsAsync may block on shell verbs; take a deferral
        // so XAML knows we're still processing.
        var deferral = e.GetDeferral();
        try
        {
            var paths = await DragDropHelper.ExtractPathsAsync(e.DataView);
            if (paths.Count > 0)
            {
                ViewModel.DatasetPicker.AppendFromDrop(paths);
            }
        }
        catch (Exception ex)
        {
            // Slice 22 has no error surface yet; log to Debug so it's
            // visible in dotnet run. Slice 23 introduces a toast/log
            // sink the VM can publish to.
            System.Diagnostics.Debug.WriteLine($"Drag-drop failed: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DatasetPath path)
        {
            ViewModel.DatasetPicker.RemoveCommand.Execute(path);
        }
    }

    private async void BrowseSchema_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await App.Current.FileDialog.PickFilesAsync(SchemaFileTypes);
            if (files.Count > 0)
            {
                ViewModel.Describe.ConnectorSchemaPath = files[0];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Schema browse failed: {ex}");
        }
    }
}
