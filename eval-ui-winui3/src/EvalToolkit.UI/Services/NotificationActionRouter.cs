using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="INotificationActionRouter"/>. Owns:
/// <list type="bullet">
/// <item><description>Process-wide per-key dedupe of identical args within a 2-second window.</description></item>
/// <item><description>Workspace-root path validation (open-job action) with reparse-point rejection.</description></item>
/// <item><description>Folder open via ShellExecute (UseShellExecute = true).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Slice 31 code-review NON-BLOCKER #1: dedupe is keyed per
/// (action|canonical-path) and pruned to entries within
/// <see cref="DedupeWindow"/>. A single-slot last-seen cache could
/// miss the sequence "A warm → B warm → A cold" because B would
/// overwrite the slot for A. A bounded dictionary preserves the
/// dedupe guarantee for each distinct toast click.
/// </remarks>
public sealed class NotificationActionRouter : INotificationActionRouter
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(2);

    private readonly string _workspaceRoot;
    private readonly Action<string> _openFolder;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public NotificationActionRouter(string workspaceRoot)
        : this(workspaceRoot, DefaultOpenFolder)
    {
    }

    /// <summary>
    /// Test-friendly overload that lets a fake replace the
    /// ShellExecute side effect. The action receives the canonical
    /// (validated) path; if it isn't called, the router rejected the
    /// input.
    /// </summary>
    public NotificationActionRouter(string workspaceRoot, Action<string> openFolder)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root required.", nameof(workspaceRoot));
        }

        // Canonicalize once so per-route comparisons are cheap and
        // free of trailing-slash inconsistencies. Use the OS-resolved
        // full path so a relative or "..\\..\\" workspace argument
        // can't escape its own root.
        _workspaceRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _openFolder = openFolder ?? throw new ArgumentNullException(nameof(openFolder));
    }

    public bool Route(IDictionary<string, string> arguments, string source)
    {
        if (arguments is null) return false;

        if (!arguments.TryGetValue("action", out var action)
            || string.IsNullOrWhiteSpace(action))
        {
            Debug.WriteLine($"NotificationActionRouter[{source}]: no action in args.");
            return false;
        }

        if (!string.Equals(action, "open-job", StringComparison.Ordinal))
        {
            Debug.WriteLine($"NotificationActionRouter[{source}]: unknown action '{action}'.");
            return false;
        }

        if (!arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            Debug.WriteLine($"NotificationActionRouter[{source}]: open-job missing path.");
            return false;
        }

        if (!TryValidateJobPath(path, out var canonical))
        {
            Debug.WriteLine($"NotificationActionRouter[{source}]: rejected path '{path}' (outside workspace, missing, relative, or reparse point).");
            return false;
        }

        // Dedupe AFTER validation — a rejected path shouldn't lock out
        // a subsequent valid attempt that happens to have the same key.
        // Per-key map (NON-BLOCKER #1): a single-slot cache would miss
        // "A warm → B warm → A cold" because B would overwrite the slot.
        string key = $"{action}|{canonical}";
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneRecentLocked(now);
            if (_recent.TryGetValue(key, out var lastAt)
                && now - lastAt < DedupeWindow)
            {
                Debug.WriteLine($"NotificationActionRouter[{source}]: deduped within {DedupeWindow.TotalSeconds:0.#}s for '{key}'.");
                return false;
            }
            _recent[key] = now;
        }

        _openFolder(canonical);
        return true;
    }

    private void PruneRecentLocked(DateTimeOffset now)
    {
        // Cheap O(n) sweep on every Route. n is the count of distinct
        // job paths the user has clicked in the last 2 seconds — in
        // practice 1-2 entries, never large enough to warrant a more
        // sophisticated structure.
        if (_recent.Count == 0) return;
        List<string>? expired = null;
        foreach (var kv in _recent)
        {
            if (now - kv.Value >= DedupeWindow)
            {
                expired ??= new List<string>(_recent.Count);
                expired.Add(kv.Key);
            }
        }
        if (expired is null) return;
        foreach (var key in expired) _recent.Remove(key);
    }

    /// <summary>
    /// Validate that <paramref name="rawPath"/> resolves to an existing
    /// directory under the configured workspace root and is not a
    /// reparse point (symlink / junction) that could redirect outside.
    /// </summary>
    private bool TryValidateJobPath(string rawPath, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            // Slice 31 NON-BLOCKER #3: refuse relative paths. App-
            // generated job paths are always absolute; a relative path
            // in a toast arg suggests a tampered payload or a bug, and
            // letting Path.GetFullPath resolve it against CWD would
            // be non-deterministic.
            if (!Path.IsPathFullyQualified(rawPath)) return false;

            // Path.GetFullPath rejects null bytes and collapses
            // "..\\" sequences, then we strip the trailing separator
            // for consistent prefix comparison.
            var full = Path.GetFullPath(rawPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Case-insensitive prefix check is the right semantic on
            // Windows. The extra separator guards against a partial
            // root collision (workspace "C:\\eval" vs. "C:\\evalbad").
            string rootWithSep = _workspaceRoot + Path.DirectorySeparatorChar;
            bool insideRoot = full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, _workspaceRoot, StringComparison.OrdinalIgnoreCase);

            if (!insideRoot) return false;
            if (!Directory.Exists(full)) return false;

            // Slice 31 NON-BLOCKER #2: a symlink or junction inside the
            // workspace could re-target Explorer to anywhere on disk.
            // Reject the leaf directory itself if it has the
            // ReparsePoint attribute — the simplest cheap defense.
            // (We don't walk parents because the workspace root itself
            // is trusted; if the user intentionally placed the
            // workspace under a symlinked tree, that's their call.)
            var attrs = File.GetAttributes(full);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return false;

            canonical = full;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NotificationActionRouter.TryValidateJobPath: {ex}");
            return false;
        }
    }

    private static void DefaultOpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NotificationActionRouter.DefaultOpenFolder: {ex}");
        }
    }
}

