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
public sealed partial class SubscriptionGroupControl : UserControl
{
    public ServerListGroup Model { get => (ServerListGroup)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ServerListGroup), typeof(SubscriptionGroupControl), new PropertyMetadata(null, (d, _) => { var control = (SubscriptionGroupControl)d; if (control.IsLoaded) control.Bindings.Update(); }));
    public FrameworkElement HeaderAnchor => AnchorHeader;
    private ServerListControl? _browser;
    public SubscriptionGroupControl()
    {
        InitializeComponent();
        GroupExpander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty,
            (_, _) => ApplyExpanded(GroupExpander.IsExpanded));
        Loaded += (_, _) => { _browser = ServerListControl.FindBrowser(this); _browser?.RegisterHeader(AnchorHeader); };
        Unloaded += (_, _) => { _browser?.UnregisterHeader(AnchorHeader); _browser = null; };
        GroupExpander.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { if (IsHeaderInput(e.OriginalSource)) _browser?.PrepareGroupToggle(Model); }), true);
        GroupExpander.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (IsHeaderInput(e.OriginalSource) && e.Key is VirtualKey.Space or VirtualKey.Enter) _browser?.PrepareGroupToggle(Model); }), true);
    }
    private bool IsHeaderInput(object source)
    {
        for (var p = source as DependencyObject; p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (ReferenceEquals(p, Rows)) return false;
            if (ReferenceEquals(p, GroupExpander)) return true;
        }
        return false;
    }
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    public ServerRowControl GetRow(int index) => (ServerRowControl)Rows.GetOrCreateElement(index);
    internal void PrepareCollapse()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) return;
        for (var p = focused; p != null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, Rows)) { FindChild<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(GroupExpander)?.Focus(FocusState.Programmatic); break; }
    }
    private void ApplyExpanded(bool expanded)
    {
        if (Model == null || Model.IsExpanded == expanded) return;
        _browser?.ViewModel.SetGroupExpanded(Model, expanded);
        if (Model.IsExpanded != expanded) GroupExpander.IsExpanded = Model.IsExpanded;
    }
    private static void Caption(object sender, string key)
    {
        if (sender is FrameworkElement element) LocalizationBindings.Bind(element, "GroupCaption", () =>
        { ToolTipService.SetToolTip(element, Loc.GetString(key)); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, Loc.GetString(key)); });
    }
    private void AutoRefreshDisabled_Loaded(object sender, RoutedEventArgs e) => Caption(sender, "Groups_AutoRefreshDisabled");
    private void GroupRefresh_Loaded(object sender, RoutedEventArgs e) => Caption(sender, "Groups_Refresh");
    private void GroupTest_Loaded(object sender, RoutedEventArgs e) => Caption(sender, "Groups_Test");
    private void GroupMenu_Loaded(object sender, RoutedEventArgs e) => Caption(sender, "Groups_Actions");
}
