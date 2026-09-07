using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using XrayUI.Helpers;
using XrayUI.ViewModels;
using Windows.System;
namespace XrayUI.Views;
public sealed partial class ServerRowControl : UserControl
{
    public ServerRowViewModel Model { get => (ServerRowViewModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ServerRowViewModel), typeof(ServerRowControl), new PropertyMetadata(null, (d, args) =>
    {
        var control = (ServerRowControl)d;
        if (control.IsLoaded) { control.Bindings.Update(); _ = control.Model?.EnsureCountryAsync(); }
    }));
    public ServerRowControl()
    {
        InitializeComponent();
        Loaded += (_, _) => { _ = Model?.EnsureCountryAsync(); };
    }
    public void FocusRow() => Row.Focus(FocusState.Keyboard);
    private static bool Held(VirtualKey key) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        ServerListControl.FindBrowser(this)?.ViewModel.SelectRow(Model, Held(VirtualKey.Control), Held(VirtualKey.Shift));
        Row.Focus(FocusState.Pointer); e.Handled = true;
    }
    private void RowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ServerListControl.FindBrowser(this) is not { } browser) return;
        if (e.Key == VirtualKey.A && Held(VirtualKey.Control))
        { browser.ViewModel.SelectAllRows(); e.Handled = true; return; }
        var rows = browser.ViewModel.NavigableRows;
        var index = Array.IndexOf(rows, Model);
        var next = e.Key switch { VirtualKey.Up => index - 1, VirtualKey.Down => index + 1, VirtualKey.Home => 0, VirtualKey.End => rows.Length - 1, VirtualKey.PageUp => index - 8, VirtualKey.PageDown => index + 8, _ => -999 };
        if (next != -999 && rows.Length > 0)
        {
            var target = rows[Math.Clamp(next, 0, rows.Length - 1)];
            if (!Held(VirtualKey.Control) || Held(VirtualKey.Shift)) browser.ViewModel.SelectRow(target, Held(VirtualKey.Control), Held(VirtualKey.Shift));
            browser.BringRowIntoView(target); e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space)
        { browser.ViewModel.SelectRow(Model, Held(VirtualKey.Control), Held(VirtualKey.Shift)); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter)
        { browser.ViewModel.SelectRow(Model); if (browser.SwitchToSelectedServerCommand?.CanExecute(null) == true) browser.SwitchToSelectedServerCommand.Execute(null); e.Handled = true; }
    }
    private void ServerItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ServerListControl.FindBrowser(this)?.ServerItem_DoubleTapped(this, e);
    private void ServerItem_ContextRequested(UIElement sender, ContextRequestedEventArgs e) => ServerListControl.FindBrowser(this)?.ServerItem_ContextRequested(this, e);
    private void ActiveBadge_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (sender is UIElement element) element.CenterPoint = new System.Numerics.Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0); }
}
