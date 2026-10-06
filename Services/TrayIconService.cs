using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using XrayUI.Helpers;

namespace XrayUI.Services;

internal interface ITrayIconApi
{
    IntPtr LoadIcon(string path);
    void DestroyIcon(IntPtr icon);
    bool Notify(uint command, ref TrayIconInterop.IconData data);
}

/// <summary>UI-thread-owned Shell icon. Registration is confirmed by Windows, and is retryable.</summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly ITrayIconApi _api;
    private readonly IntPtr _window;
    private readonly uint _id;
    private IntPtr _icon;
    private string _iconPath;
    private string _tooltip;
    private bool _registered;
    private bool _version4;
    private bool _disposed;

    internal TrayIconService(IntPtr window, uint id, string iconPath, string tooltip)
        : this(window, id, iconPath, tooltip, new NativeApi()) { }

    internal TrayIconService(IntPtr window, uint id, string iconPath, string tooltip, ITrayIconApi api)
    {
        if (id > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(id));
        _window = window;
        _id = id;
        _api = api;
        _iconPath = iconPath;
        _tooltip = tooltip;
        _icon = api.LoadIcon(iconPath);
    }

    internal bool EnsureRegistered()
    {
        if (_disposed) return false;
        var data = CreateData();
        if (_registered && _api.Notify(TrayIconInterop.Modify, ref data)) return true;

        // Explorer can be absent at logon or lose all icons when it restarts.
        // Modify is also a fallback when an earlier Add already reached the Shell.
        _registered = _api.Notify(TrayIconInterop.Add, ref data)
            || _api.Notify(TrayIconInterop.Modify, ref data);
        if (_registered)
        {
            data.Version = 4;
            _version4 = _api.Notify(TrayIconInterop.SetVersion, ref data);
        }
        return _registered;
    }

    internal bool OnTaskbarCreated()
    {
        _registered = false;
        return EnsureRegistered();
    }

    internal void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        EnsureRegistered();
    }

    internal void SetIcon(string path)
    {
        if (_disposed || path == _iconPath) return;
        var icon = _api.LoadIcon(path);
        var oldIcon = _icon;
        _icon = icon;
        _iconPath = path;
        try { EnsureRegistered(); }
        finally { _api.DestroyIcon(oldIcon); }
    }

    internal bool TryGetAction(nuint wParam, nint lParam, out bool openContextMenu)
    {
        var value = unchecked((ulong)lParam.ToInt64());
        var id = _version4 ? (uint)((value >> 16) & 0xffff) : unchecked((uint)wParam);
        var message = _version4 ? (uint)(value & 0xffff) : (uint)value;
        openContextMenu = message is 0x007b or 0x0205; // WM_CONTEXTMENU / legacy WM_RBUTTONUP
        return !_disposed && id == _id && (openContextMenu || message is 0x0400 or 0x0401 or 0x0202);
    }

    internal bool TryGetRect(out TrayIconInterop.Rect rect)
    {
        var identifier = new TrayIconInterop.IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<TrayIconInterop.IconIdentifier>(), Window = _window, Id = _id
        };
        return TrayIconInterop.GetIconRect(ref identifier, out rect) == 0;
    }

    private TrayIconInterop.IconData CreateData()
    {
        var data = new TrayIconInterop.IconData
        {
            Size = (uint)Marshal.SizeOf<TrayIconInterop.IconData>(),
            Window = _window, Id = _id, Icon = _icon,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
            Callback = TrayIconInterop.CallbackMessage
        };
        data.SetTooltip(_tooltip);
        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData();
        _api.Notify(TrayIconInterop.Delete, ref data);
        _api.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _registered = false;
    }

    private sealed class NativeApi : ITrayIconApi
    {
        public IntPtr LoadIcon(string path)
        {
            var icon = TrayIconInterop.LoadImage(IntPtr.Zero, path, 1,
                TrayIconInterop.GetSystemMetrics(49), TrayIconInterop.GetSystemMetrics(50), 0x10);
            if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return icon;
        }

        public void DestroyIcon(IntPtr icon) => TrayIconInterop.DestroyIcon(icon);
        public bool Notify(uint command, ref TrayIconInterop.IconData data) => TrayIconInterop.NotifyIcon(command, ref data);
    }
}
