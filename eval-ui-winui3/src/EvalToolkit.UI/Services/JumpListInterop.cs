using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Win32 COM interop for the Windows taskbar custom destination list
/// (a.k.a. "jump list"). We use <c>ICustomDestinationList</c> rather
/// than <c>Windows.UI.StartScreen.JumpList</c> because the WinAppSDK
/// API requires package identity — and slice 29 must work in BOTH
/// unpackaged dev runs and packaged MSIX builds. Slice 30
/// (<c>msix-packaging</c>) keeps using the same code path.
/// </summary>
/// <remarks>
/// <para>
/// References:
/// </para>
/// <list type="bullet">
/// <item><description><c>ICustomDestinationList</c>: shobjidl_core.h</description></item>
/// <item><description><c>IShellLinkW</c>: shobjidl.h</description></item>
/// <item><description><c>IPropertyStore</c>: propsys.h — required so the
///   shell can group entries under our explicit AppUserModelID
///   (GPT-5.5 slice-29 plan review BLOCKER #1).</description></item>
/// </list>
/// <para>
/// All COM apartment threading is the caller's responsibility —
/// <see cref="JumpListService"/> marshals onto the UI thread's STA
/// dispatcher before any of these calls.
/// </para>
/// </remarks>
internal static class JumpListInterop
{
    // CLSIDs
    public static readonly Guid CLSID_DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    public static readonly Guid CLSID_EnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    // IIDs (used as ref Guid for BeginList / GetAt)
    public static Guid IID_IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    public static Guid IID_IObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");
    public static Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    // PROPERTYKEY values from propkey.h
    // PKEY_AppUserModel_ID: required so the shell groups items under
    // our explicit AUMID (matches SetCurrentProcessExplicitAppUserModelID).
    public static PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    // PKEY_Title: drives the visible label of the shell link in the jump list.
    public static PROPERTYKEY PKEY_Title = new()
    {
        fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        pid = 2,
    };

    private const ushort VT_LPWSTR = 31;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    [DllImport("ole32.dll", PreserveSig = false)]
    public static extern void PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Sets a string-typed property (VT_LPWSTR) on the link's property
    /// store and frees the COM-allocated value buffer. The caller is
    /// expected to call <see cref="IPropertyStore.Commit"/> once all
    /// properties on the link are set (saves one round-trip per value).
    /// </summary>
    public static void SetStringProperty(IPropertyStore store, PROPERTYKEY key, string value)
    {
        ArgumentNullException.ThrowIfNull(store);
        var pv = new PROPVARIANT
        {
            vt = VT_LPWSTR,
            pwszVal = Marshal.StringToCoTaskMemUni(value ?? string.Empty),
        };
        try
        {
            int hr = store.SetValue(ref key, ref pv);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            // PropVariantClear releases pwszVal (it knows the vt-tagged
            // discriminated union layout). DO NOT also Marshal.FreeCoTaskMem
            // — that would be a double-free.
            try { PropVariantClear(ref pv); } catch { /* swallow */ }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        // Discriminated union — for VT_LPWSTR this is a CoTaskMem-allocated
        // wide-string pointer. PROPVARIANT is 16 bytes on x86 + 24 on x64;
        // declaring two pointer fields gives us the right size on both.
        public IntPtr unionField1;
        public IntPtr unionField2;

        // Helper alias for the VT_LPWSTR slot — first pointer of the union.
        public IntPtr pwszVal
        {
            readonly get => unionField1;
            set => unionField1 = value;
        }
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    public class CDestinationList
    {
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        [PreserveSig]
        int BeginList(out uint pcMinSlots, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory,
            [MarshalAs(UnmanagedType.Interface)] IObjectArray poa);

        [PreserveSig]
        int AppendKnownCategory(int category);

        [PreserveSig]
        int AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray poa);

        [PreserveSig]
        int CommitList();

        void GetRemovedDestinations(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        [PreserveSig]
        int AbortList();
    }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    public class EnumerableObjectCollection
    {
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectCollection
    {
        // IObjectArray members (IObjectCollection extends IObjectArray)
        [PreserveSig]
        int GetCount(out uint pcObjects);
        [PreserveSig]
        int GetAt(uint uiIndex, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        // IObjectCollection-specific members
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object pvObject);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectArray
    {
        [PreserveSig]
        int GetCount(out uint pcObjects);
        [PreserveSig]
        int GetAt(uint uiIndex, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    public class CShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath,
            int iIcon);

        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPathRel,
            uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);

        [PreserveSig]
        int Commit();
    }
}
