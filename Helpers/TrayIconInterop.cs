using System;
using System.Runtime.InteropServices;

namespace XrayUI.Helpers;

internal static partial class TrayIconInterop
{
    internal const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4;
    internal const uint CallbackMessage = 0x8052;

    // Fixed UTF-16 buffers keep the Shell structure blittable for NativeAOT.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct IconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;

        public void SetTooltip(string value)
        {
            fixed (char* tip = Tip)
            {
                var buffer = new Span<char>(tip, 128);
                buffer.Clear();
                value.AsSpan(0, Math.Min(value.Length, 127)).CopyTo(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool NotifyIcon(uint command, ref IconData data);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    internal static partial int GetIconRect(ref IconIdentifier identifier, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr LoadImage(IntPtr instance, string path, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilter);

#if LOCALIZATION_SMOKE_TEST
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(IntPtr window, uint message, nuint wParam, nint lParam);
#endif
}
