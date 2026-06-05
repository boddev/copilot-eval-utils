using System.Diagnostics;
using System.IO;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Thin wrappers around Win32 shell operations for the wizard's
/// "Open CSV / Open review / Show in folder" buttons. Kept static for
/// the same reason as <see cref="DragDropHelper"/> — stateless, no DI
/// value, and the calls are easy to mock out in tests by gating
/// behind <c>#if !TESTING</c> if it ever matters.
/// </summary>
public static class ShellOpener
{
    /// <summary>
    /// Open a file with its default Windows associated application.
    /// Uses <c>ShellExecute</c> so e.g. .csv opens in Excel / Notepad
    /// and .md opens in the user's Markdown viewer of choice.
    /// </summary>
    public static void OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Open a folder in Explorer.
    /// </summary>
    public static void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        Process.Start("explorer.exe", folderPath);
    }

    /// <summary>
    /// Open Explorer with the given file pre-selected
    /// (<c>explorer.exe /select,"path"</c>).
    /// </summary>
    public static void RevealInFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }
}
