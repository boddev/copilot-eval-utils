using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;
using EvalToolkit.UI.Services;
using EvalToolkit.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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

    /// <summary>
    /// Slice 30 (FTA): when navigated to with an
    /// <see cref="OpenEvalSetRequest"/> parameter (set by
    /// <see cref="FileActivationRouter"/>), hydrate the wizard from
    /// the on-disk eval set and land in Step 4 (editor) with the CSV
    /// loaded. Fire-and-forget the async hydration on the UI thread —
    /// OnNavigatedTo cannot be async-awaited by the navigation system.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is OpenEvalSetRequest request)
        {
            _ = ViewModel.OpenExistingEvalSetAsync(request);
        }
    }

    // Slice 27: render generation counter so a stale render that
    // resolves after a newer ReportHtml has arrived doesn't overwrite
    // the latest HTML in the WebView2. Reentrancy on the install
    // button itself is guarded by IsEnabled in the click handler.
    private long _renderGeneration;

    private async void OnScoreVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScoreViewModel.ReportHtml)) return;
        // Delegate to the shared render path so install/retry can also
        // re-render the current HTML without re-firing PropertyChanged.
        await RenderCurrentReportHtmlAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Render the current <see cref="ScoreViewModel.ReportHtml"/> into
    /// the WebView2, falling back to the native XAML pane when the
    /// WebView2 Runtime is missing. Re-entrant: a generation counter
    /// drops stale renders if a newer one starts while this one is
    /// awaiting. Idempotent — safe to call from event handlers, the
    /// install success path, and any future retry button.
    /// </summary>
    private async Task RenderCurrentReportHtmlAsync()
    {
        string? html = ViewModel.Score.ReportHtml;
        if (string.IsNullOrEmpty(html))
        {
            return;
        }
        long myGen = Interlocked.Increment(ref _renderGeneration);
        try
        {
            // Detect runtime BEFORE EnsureCoreWebView2Async (which would
            // throw an unhelpful loader exception when the runtime is
            // missing). Falls back to a XAML panel when the runtime is
            // absent (per GPT-5.5 slice-27 review — blocker on showing
            // RenderError HTML in a non-functional WebView2 control).
            bool runtimeOk = await App.Current.WebView2Runtime
                .IsRuntimeAvailableAsync()
                .ConfigureAwait(true);
            // Drop stale renders: if a newer ReportHtml event has
            // already arrived while we were awaiting, let it own
            // the WebView2.
            if (myGen != Interlocked.Read(ref _renderGeneration)) return;
            if (!runtimeOk)
            {
                ShowWebView2Fallback();
                return;
            }

            HideWebView2Fallback();
            // EnsureCoreWebView2Async is a no-op after the first call; safe to
            // invoke on every render. Required because WebView2 lazy-initializes.
            await ReportWebView.EnsureCoreWebView2Async();
            // Re-check generation after the (slower) Ensure call.
            if (myGen != Interlocked.Read(ref _renderGeneration)) return;
            // No flag toggle needed — NavigateToString always navigates the
            // document to about:blank, which is the only URI the
            // NavigationStarting handler permits (see ReportWebView_NavigationStarting).
            ReportWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 render failed: {ex}");
            // Generation check in catch too — a stale failed render
            // must not overwrite a newer successful render with the
            // fallback panel (GPT-5.5 slice-27 review must-fix #2).
            if (myGen != Interlocked.Read(ref _renderGeneration)) return;
            // Loader exceptions during Ensure are typically the
            // runtime-missing case; show the fallback panel rather than
            // leaving a silent blank pane.
            ShowWebView2Fallback($"WebView2 initialization failed: {ex.Message}");
        }
    }

    private void ShowWebView2Fallback(string? status = null)
    {
        ReportWebView.Visibility = Visibility.Collapsed;
        WebView2FallbackPanel.Visibility = Visibility.Visible;
        WebView2FallbackStatus.Text = status ?? string.Empty;
        // If the bundled bootstrapper is absent (dev builds, slim
        // packaging), the "Install" button can't do anything useful —
        // disable it and rely on "Get installer" to take the user to
        // the public download URL.
        WebView2InstallButton.IsEnabled = App.Current.WebView2Runtime.IsBundledInstallerAvailable;
    }

    private void HideWebView2Fallback()
    {
        WebView2FallbackPanel.Visibility = Visibility.Collapsed;
        ReportWebView.Visibility = Visibility.Visible;
    }

    private async void WebView2InstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WebView2InstallButton.IsEnabled = false;
            WebView2RetryButton.IsEnabled = false;
            WebView2FallbackStatus.Text = "Installing WebView2 Runtime…";
            var progress = new Progress<string>(msg =>
            {
                DispatcherQueue.TryEnqueue(() => WebView2FallbackStatus.Text = msg);
            });
            bool installed = await App.Current.WebView2Runtime
                .TryRunBundledBootstrapperAsync(progress)
                .ConfigureAwait(true);
            if (installed)
            {
                WebView2FallbackStatus.Text = "WebView2 installed — re-rendering report…";
                // Re-render the current HTML through the shared path
                // rather than synthesizing a property-change event
                // (GPT-5.5 slice-27 review must-fix #3).
                await RenderCurrentReportHtmlAsync().ConfigureAwait(true);
            }
            else
            {
                WebView2FallbackStatus.Text =
                    "Install did not complete. You can retry, get the installer, or open the report file directly.";
                WebView2InstallButton.IsEnabled = App.Current.WebView2Runtime.IsBundledInstallerAvailable;
                WebView2RetryButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            WebView2FallbackStatus.Text = $"Install failed: {ex.Message}";
            WebView2InstallButton.IsEnabled = App.Current.WebView2Runtime.IsBundledInstallerAvailable;
            WebView2RetryButton.IsEnabled = true;
        }
    }

    private async void WebView2RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // After a manual install via the public fwlink the user comes
        // back to the app — re-probe the runtime and re-render. This
        // is the only honest way to recover when the bundled installer
        // is absent (GPT-5.5 slice-27 review must-fix #1).
        WebView2RetryButton.IsEnabled = false;
        WebView2FallbackStatus.Text = "Checking for WebView2 Runtime…";
        try
        {
            await RenderCurrentReportHtmlAsync().ConfigureAwait(true);
        }
        finally
        {
            // RenderCurrentReportHtmlAsync flips visibility itself; if
            // the panel is still shown, the runtime is still missing and
            // we re-enable the retry button for the next attempt.
            if (WebView2FallbackPanel.Visibility == Visibility.Visible)
            {
                WebView2RetryButton.IsEnabled = true;
                if (string.IsNullOrEmpty(WebView2FallbackStatus.Text) ||
                    WebView2FallbackStatus.Text == "Checking for WebView2 Runtime…")
                {
                    WebView2FallbackStatus.Text =
                        "WebView2 Runtime is still not detected. If you've installed it, try again or restart the app.";
                }
            }
        }
    }

    private async void WebView2OpenManualButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Current.WebView2Runtime.OpenManualInstallerAsync().ConfigureAwait(true);
            WebView2FallbackStatus.Text =
                "Installer opened in your browser. After installing WebView2, return here and click \"Install WebView2 (recommended)\" or scroll the report panel back into view to retry.";
        }
        catch (Exception ex)
        {
            WebView2FallbackStatus.Text = $"Couldn't open the installer URL: {ex.Message}";
        }
    }

    private void WebView2OpenReportFromFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        // Delegate to the existing ScoreViewModel command so the file
        // open uses the same shell-execute path as the main toolbar.
        if (ViewModel.Score.OpenReportCommand.CanExecute(null))
        {
            ViewModel.Score.OpenReportCommand.Execute(null);
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
