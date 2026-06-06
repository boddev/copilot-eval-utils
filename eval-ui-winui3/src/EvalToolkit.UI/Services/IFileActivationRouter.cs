namespace EvalToolkit.UI.Services;

/// <summary>
/// Routes a single file path (delivered via MSIX FTA activation or the
/// <c>--open-file</c> command-line verb) to the appropriate view or
/// system handler. Implementations must avoid invoking shell-execute
/// on app-owned alias extensions to prevent an activation loop once
/// the MSIX FTAs are registered (slice 31).
/// </summary>
public interface IFileActivationRouter
{
    /// <summary>
    /// Dispatch the file path to the right destination.
    /// </summary>
    /// <param name="filePath">Absolute path to the activated file.</param>
    /// <param name="needsNavigationQueue">
    /// Set to <c>true</c> when dispatch requires the "Wizard" route but
    /// the route isn't registered yet (cold-start race with
    /// <see cref="Views.MainShell.OnLoaded"/>). Caller should enqueue the
    /// activation for replay.
    /// </param>
    /// <returns>
    /// <c>true</c> when the request was fully dispatched. <c>false</c>
    /// when dispatch was deferred — combined with
    /// <paramref name="needsNavigationQueue"/> it tells the caller
    /// whether to retry later.
    /// </returns>
    bool Route(string filePath, out bool needsNavigationQueue);
}
