using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace EvalToolkit.UI;

/// <summary>
/// Owns [STAThread] Main so we can register single-instance behavior
/// with <see cref="AppInstance"/> *before* the XAML runtime spins up.
///
/// <para>
/// AppInstance flow (per WinAppSDK docs):
///   1. <see cref="AppInstance.GetCurrent"/> to learn the activation
///      args for this process invocation.
///   2. <see cref="AppInstance.FindOrRegisterForKey"/> with a stable
///      key — the first process to register wins and becomes the
///      "primary". Every subsequent process gets back a handle to the
///      primary.
///   3. If we are NOT the primary, <see cref="AppInstance.RedirectActivationToAsync"/>
///      hands the activation args (file path, protocol URI, jump-list
///      verb) to the primary and we exit. Per Microsoft's documented
///      WinUI single-instance sample the redirect must run off the
///      STA + the STA must wait via <see cref="CoWaitForMultipleObjects"/>
///      so the COM message pump keeps turning while we wait — a plain
///      <c>.GetAwaiter().GetResult()</c> can deadlock on the STA pump
///      (GPT-5.5 slice-21 review, finding #2).
///   4. If we ARE the primary, hook <see cref="AppInstance.Activated"/>
///      so re-activations land on the same shell window, then
///      <see cref="Application.Start"/> the XAML app.
/// </para>
/// </summary>
internal static class Program
{
    // Stable key for the single-instance lock. Bump this if we ever want
    // multiple side-by-side installs to coexist as separate windows.
    private const string SingleInstanceKey = "EvalToolkit.UI.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Slice 29 (winui-native-plus-jumplist): set the explicit
            // AppUserModelID BEFORE any window is shown so the unpackaged
            // EvalToolkit.UI process gets a stable taskbar identity that
            // ICustomDestinationList can target. When slice 30 ships the
            // MSIX, the package-identity AUMID takes precedence and this
            // call is a harmless no-op. Must run before ComWrappers init
            // so the shell sees a consistent identity from the first COM
            // activation onward.
            try
            {
                EvalToolkit.UI.Services.JumpListInterop
                    .SetCurrentProcessExplicitAppUserModelID(
                        EvalToolkit.UI.Services.JumpListService.DefaultAppId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetCurrentProcessExplicitAppUserModelID failed: {ex}");
            }

            ComWrappersSupport.InitializeComWrappers();

            if (!DecideSingleInstance())
            {
                return 0;
            }

            Application.Start(static initArgs =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                _ = new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            // Last-resort surface so a crash before the shell window opens
            // is visible in `dotnet run` / Event Viewer.
            Debug.WriteLine($"Fatal: {ex}");
            try
            {
                Console.Error.WriteLine(ex);
            }
            catch
            {
                // Console is unavailable in pure-GUI mode — ignore.
            }
            return 1;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if this process should continue and become
    /// the primary instance. Returns <c>false</c> if this process
    /// should exit after redirecting activation to the primary.
    /// </summary>
    private static bool DecideSingleInstance()
    {
        AppInstance primary = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (primary.IsCurrent)
        {
            // Slice 31 (winui-native-plus-toasts) BLOCKER #1 from
            // GPT-5.5 plan review: do NOT call GetActivatedEventArgs()
            // here. Per WAS notifications guidance,
            // AppNotificationManager.Default.Register() must run
            // BEFORE GetActivatedEventArgs() for cold-start toast
            // activations to be delivered. The primary path defers
            // GetActivatedEventArgs to App.OnLaunched (after
            // TrayIconService.TryRegisterNotifications has run);
            // non-Launch activations (AppNotification, File, Protocol)
            // arriving cold are routed there instead. Re-activations
            // continue to land via primary.Activated → App.OnReactivation.
            primary.Activated += App.OnReactivation;
            return true;
        }

        // Secondary instance: redirect this process's activation to
        // the primary and exit. The redirect needs the args, so
        // GetActivatedEventArgs is unavoidable here — and harmless,
        // because the secondary never calls Register and never
        // expects notifications.
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        RedirectStaSafe(primary, activation);
        return false;
    }

    private static void RedirectStaSafe(AppInstance primary, AppActivationArguments activation)
    {
        IntPtr redirectEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (redirectEvent == IntPtr.Zero)
        {
            int lastError = Marshal.GetLastWin32Error();
            // CreateEvent failed (extreme handle exhaustion). Surface
            // it before falling back so this never silently degrades
            // to the unsafe wait.
            Debug.WriteLine($"RedirectStaSafe: CreateEvent failed, lastError={lastError}; falling back to blocking wait.");
            primary.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return;
        }

        Exception? redirectError = null;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await primary.RedirectActivationToAsync(activation);
                }
                catch (Exception ex)
                {
                    // GPT-5.5 slice-21 round-2 finding #2: capture
                    // the redirect failure so the redirector process
                    // can rethrow after the STA wait — otherwise the
                    // process exits 0 even though no payload reached
                    // the primary.
                    redirectError = ex;
                }
                finally
                {
                    SetEvent(redirectEvent);
                }
            });

            const uint CWMO_DEFAULT = 0;
            const uint INFINITE = 0xFFFFFFFF;
            IntPtr[] handles = { redirectEvent };
            int hr = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, (uint)handles.Length, handles, out _);
            if (hr < 0)
            {
                throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"CoWaitForMultipleObjects failed (HRESULT 0x{hr:X8}).");
            }
        }
        finally
        {
            CloseHandle(redirectEvent);
        }

        if (redirectError is not null)
        {
            throw redirectError;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(
        uint dwFlags,
        uint dwMilliseconds,
        uint cHandles,
        [In] IntPtr[] pHandles,
        out uint lpdwIndex);
}

