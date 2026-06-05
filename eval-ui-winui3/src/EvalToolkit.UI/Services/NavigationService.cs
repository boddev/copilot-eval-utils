using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Minimal navigation service over a single root <see cref="Frame"/>.
/// Slice-21 scope: navigate-by-Type plus a string key registry so view
/// models / file activation handlers can navigate without taking a
/// hard dep on the View Type. Drilldown / stacked navigation
/// (back-stack policy, deep-link routing) lands with the step views.
/// </summary>
public sealed class NavigationService
{
    private Frame _frame;
    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal);

    public NavigationService(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>
    /// Re-target the service onto a new <see cref="Frame"/>. Used by
    /// <see cref="Views.MainShell"/> so the wizard lives inside the
    /// shell's content frame rather than the bare ShellWindow root —
    /// without forcing every existing call site to re-resolve the
    /// service. Registered routes are preserved.
    /// </summary>
    public void Rebind(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public void Register(string key, Type viewType)
    {
        _routes[key] = viewType ?? throw new ArgumentNullException(nameof(viewType));
    }

    public bool NavigateTo(string key, object? parameter = null)
    {
        return _routes.TryGetValue(key, out var viewType)
            && NavigateTo(viewType, parameter);
    }

    public bool NavigateTo(Type viewType, object? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(viewType);

        // SuppressNavigationTransitionInfo for slice 21 — the step views
        // will pick a per-step transition (slide from right for forward,
        // slide from left for back) once they exist.
        return _frame.Navigate(viewType, parameter, new SuppressNavigationTransitionInfo());
    }

    public bool CanGoBack => _frame.CanGoBack;

    public void GoBack()
    {
        if (_frame.CanGoBack)
        {
            _frame.GoBack();
        }
    }
}
