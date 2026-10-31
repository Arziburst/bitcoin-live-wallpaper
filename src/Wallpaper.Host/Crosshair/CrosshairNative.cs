using System.Runtime.InteropServices;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>The Win32 calls of the crosshair (D-35), written from the Win32 API documentation. Kept apart from
/// <see cref="Native"/>, so the crosshair's footprint stays in its own folder.</summary>
internal static unsafe partial class CrosshairNative
{
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    /// <summary>LVM_FIRST + 18: the item at a client point (lParam: an LVHITTESTINFO in the list view's process).</summary>
    public const uint LVM_HITTEST = 0x1000 + 18;

    /// <summary>LVM_FIRST + 61: the hot (hovered) item's index, or -1.</summary>
    public const uint LVM_GETHOTITEM = 0x1000 + 61;

    public const uint SMTO_ABORTIFHUNG = 0x2;

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public Native.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    /// <summary>The same layout in 32- and 64-bit processes (no pointers).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public Native.POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nuint, nint, nint> callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(Native.POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref Native.POINT point);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint written);
}
