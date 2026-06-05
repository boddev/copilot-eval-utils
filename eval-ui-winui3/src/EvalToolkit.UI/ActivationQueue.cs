using System;
using System.Collections.Concurrent;
using Microsoft.Windows.AppLifecycle;

namespace EvalToolkit.UI;

/// <summary>
/// Buffers activation payloads delivered before the shell window is
/// ready, so file/protocol/jump-list activations that race the
/// primary's startup are not silently dropped (GPT-5.5 slice-21 review,
/// finding #1).
///
/// <para>
/// Producer: <see cref="App.OnReactivation"/> calls <see cref="Enqueue"/>
/// from the dispatcher queue (it has already been marshaled to the UI
/// thread).
/// </para>
/// <para>
/// Consumer: <see cref="App.OnLaunched"/> calls <see cref="Drain"/>
/// once the shell window, navigation, and theme services are ready.
/// </para>
/// <para>
/// Slice 21 just drains-and-discards (only effect of re-activation
/// today is bringing the window to the front, which a single
/// <see cref="App.BringShellToFront"/> handles). Slice 18 / 22 / 26 will
/// add real per-payload routing (parse activation kind → choose view).
/// </para>
/// </summary>
internal static class ActivationQueue
{
    private static readonly ConcurrentQueue<AppActivationArguments> _pending = new();

    public static void Enqueue(AppActivationArguments args) => _pending.Enqueue(args);

    /// <summary>
    /// Removes and returns every queued activation in FIFO order.
    /// Returns an empty array if none are pending. Safe to call from
    /// the UI thread.
    /// </summary>
    public static AppActivationArguments[] Drain()
    {
        if (_pending.IsEmpty)
        {
            return Array.Empty<AppActivationArguments>();
        }

        var result = new System.Collections.Generic.List<AppActivationArguments>();
        while (_pending.TryDequeue(out var args))
        {
            result.Add(args);
        }
        return result.ToArray();
    }
}
