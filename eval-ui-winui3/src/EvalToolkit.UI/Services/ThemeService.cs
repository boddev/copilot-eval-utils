using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using EvalToolkit.UI.Views;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Applies the Mica system backdrop and keeps the shell theme in sync
/// with XAML's <c>ActualTheme</c>. Uses the high-level
/// <see cref="MicaBackdrop"/> API rather than the lower-level
/// <c>MicaController</c> + <c>SystemBackdropConfiguration</c> pair —
/// the high-level API:
///
/// <list type="bullet">
///   <item>Handles MicaController lifetime, IsInputActive tracking,
///         and SystemBackdropConfiguration plumbing internally.</item>
///   <item>No GC / disposal traps (GPT-5.5 slice-21 review, finding #3).</item>
///   <item>Survives App theme changes correctly without our heuristic.</item>
/// </list>
///
/// <para>
/// Theme detection uses <see cref="FrameworkElement.ActualThemeChanged"/>
/// rather than UISettings color-value heuristics. ActualTheme is XAML's
/// canonical answer for "what theme are we rendering in right now",
/// accounts for high-contrast and app-level overrides, and fires on the
/// UI thread so no dispatcher marshaling is needed.
/// </para>
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly ShellWindow _window;
    private FrameworkElement? _root;
    private bool _disposed;

    public ThemeService(ShellWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public void Apply(ShellWindow window)
    {
        // Mica via the high-level Window.SystemBackdrop API. Falls back
        // automatically on pre-22H2 / unsupported hardware (per Microsoft
        // docs the backdrop just no-ops; we don't need to gate on
        // MicaController.IsSupported).
        window.SystemBackdrop = new MicaBackdrop();

        // ActualTheme is the right hook for system + app theme changes;
        // listen on the root visual.
        _root = window.Content as FrameworkElement;
        if (_root is not null)
        {
            _root.ActualThemeChanged += OnActualThemeChanged;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Future hook: notify view models / per-view backdrops. Slice 21
        // doesn't have any subscribers, but the wiring is in place.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_root is not null)
        {
            _root.ActualThemeChanged -= OnActualThemeChanged;
            _root = null;
        }
        _window.SystemBackdrop = null;
    }
}
