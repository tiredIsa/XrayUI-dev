using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using WinUIEx.Messaging;
using XrayUI.Helpers;

namespace XrayUI.Views;

/// <summary>Hosts the tray flyout without showing the main window.</summary>
internal sealed class TrayMenuHost : IDisposable
{
    private readonly Window _window = new();
    private readonly Grid _root = new();
    private readonly WindowMessageMonitor _monitor;
    private MenuFlyout? _flyout;
    private bool _disposed;

#if LOCALIZATION_SMOKE_TEST
    internal bool IsOpen => _flyout?.IsOpen == true;
    internal void Hide() => _flyout?.Hide();
#endif

    internal TrayMenuHost()
    {
        _window.Content = _root;
        _window.SetWindowStyle(WindowStyle.Popup);
        _window.SetIsAlwaysOnTop(true);
        _window.AppWindow.IsShownInSwitchers = false;
        _monitor = new WindowMessageMonitor(_window);
        _monitor.WindowMessageReceived += OnMessage;
    }

    internal void Show(MenuFlyout flyout, TrayIconInterop.Rect rect, ElementTheme theme)
    {
        if (_disposed) return;
        _flyout?.Hide();
        _flyout = flyout;
        flyout.ShouldConstrainToRootBounds = false;
        _root.RequestedTheme = theme;
        _root.ContextFlyout = flyout;
        flyout.Closed += OnFlyoutClosed;
        _window.AppWindow.MoveAndResize(new RectInt32(rect.Left, rect.Top, 1, 1));
        _window.Activate();
        _window.AppWindow.Show();
        _window.SetForegroundWindow();
        // Let the popup's XamlRoot finish loading before opening the flyout.
        _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || _flyout != flyout) return;
            var scale = _root.XamlRoot?.RasterizationScale ?? 1;
            flyout.ShowAt(_root, new FlyoutShowOptions
            {
                Position = new Point(0, 0),
                ExclusionRect = new Rect(0, 0, (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale)
            });
        });
    }

    private void OnMessage(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId == 0x0006 && (e.Message.WParam & 0xffff) == 0)
            _root.DispatcherQueue.TryEnqueue(() => _flyout?.Hide());
    }

    private void OnFlyoutClosed(object? sender, object args)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Closed -= OnFlyoutClosed;
        if (!_disposed && flyout == _flyout) _window.AppWindow.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_flyout is not null) _flyout.Closed -= OnFlyoutClosed;
        _flyout?.Hide();
        _flyout = null;
        _monitor.WindowMessageReceived -= OnMessage;
        _monitor.Dispose();
        _window.Close();
    }
}
