using System;
using System.ComponentModel;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.Services;
using EvalToolkit.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
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

        ViewModel = new WizardViewModel(
            App.Current.FileDialog,
            App.Current.JobService,
            App.Current.ScoreService,
            App.Current.WorkspaceRoot,
            DispatcherQueue);
        DataContext = ViewModel;

        // Auto-scroll the progress log to the latest line as
        // entries arrive (terminal tailing behavior).
        ViewModel.Progress.LogLines.CollectionChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LogScrollViewer?.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
            });
        };

        // Slice 26: push report HTML into the WebView2 when the VM
        // produces a new rendering. ReportHtml is set on the UI thread
        // by ScoreViewModel.ApplySuccess, so we don't need to dispatch.
        ViewModel.Score.PropertyChanged += OnScoreVmPropertyChanged;
    }

    private async void OnScoreVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScoreViewModel.ReportHtml)) return;
        string? html = ViewModel.Score.ReportHtml;
        if (string.IsNullOrEmpty(html))
        {
            return;
        }
        try
        {
            // EnsureCoreWebView2Async is a no-op after the first call; safe to
            // invoke on every render. Required because WebView2 lazy-initializes.
            await ReportWebView.EnsureCoreWebView2Async();
            // No flag toggle needed — NavigateToString always navigates the
            // document to about:blank, which is the only URI the
            // NavigationStarting handler permits (see ReportWebView_NavigationStarting).
            ReportWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 render failed: {ex}");
            // Fall back: leave WebView blank; report-open button still works.
        }
    }

    // XAML event handlers must be instance methods (the XAML loader
    // binds the handler against the page instance), so CA1822 doesn't
    // apply even though the body has no instance access.
#pragma warning disable CA1822 // Mark members as static
    private void ReportWebView_NavigationStarting(
        Microsoft.UI.Xaml.Controls.WebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        // GPT-5.5 plan-review #4 defense-in-depth.
        //
        // Primary filter is URI-based, not state-based: WebView2's
        // NavigateToString *always* navigates the document to
        // "about:blank" (it injects the HTML body into that blank
        // document), so any NavigationStarting event whose Uri is not
        // "about:blank" is some other navigation — a markdown link
        // click, an iframe redirect, a JS-driven navigation, etc. —
        // and we always cancel those. This is fully stateless and
        // immune to flag-toggle races on re-renders.
        //
        // The CSP served by MarkdownReportRenderer already blocks
        // scripts, framing, and external resources, but this handler
        // is the belt to that suspenders: even if a future renderer
        // change relaxes the CSP, link navigations stay blocked.
        string? uri = args.Uri;
        if (string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        args.Cancel = true;
    }
#pragma warning restore CA1822

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
