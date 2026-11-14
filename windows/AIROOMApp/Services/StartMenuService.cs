using System.Runtime.InteropServices;
using System.Text;

namespace AIROOMApp.Services;

/// <summary>
/// The Start menu shortcut, so the app can be found by Win-key search and pinned
/// like any installed program — the Windows answer to Spotlight and Launchpad.
/// This build ships as a portable zip with no installer, so the shortcut is
/// opt-in from the settings page, points at the running exe, and only ever lands
/// in the user's own Programs folder: no registry, no elevation.
/// </summary>
public static class StartMenuService
{
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "AI Room Native.lnk");

    public static bool IsInstalled() => File.Exists(ShortcutPath);

    /// <summary>Creates the shortcut pointing at the running exe. Throws with the
    /// shell's own error when COM refuses; the toggle snaps back.</summary>
    public static void Install()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("找不到正在執行的程式位置");
        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe));
        link.SetDescription("AIROOM — AI Room Native");
        link.SetIconLocation(exe, 0);
        ((IPersistFile)link).Save(ShortcutPath, true);
    }

    public static void Uninstall()
    {
        if (IsInstalled()) File.Delete(ShortcutPath);
    }
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
sealed class ShellLink
{
}

/// <summary>IShellLinkW in full vtable order; the Get* stubs exist only to keep
/// the Set* slots we call in the right positions.</summary>
[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    void GetIDList(out IntPtr list);
    void SetIDList(IntPtr list);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int capacity);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
    void Resolve(IntPtr ownerWindow, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFile
{
    void GetClassID(out Guid classID);
    void IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}
