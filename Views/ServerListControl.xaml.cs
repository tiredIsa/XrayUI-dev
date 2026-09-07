using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class ServerListControl
    {
        public ServerListViewModel ViewModel { get; set; } = null!;
        public IAsyncRelayCommand? SwitchToSelectedServerCommand { get; set; }

        public ServerListControl()
        {
            this.InitializeComponent();
            Loaded += (_, _) => { ViewModel.GroupToggling += PrepareGroupToggle; ViewModel.PropertyChanged += OnModelChanged; QueueInitialScroll(); };
            Unloaded += (_, _) => { ViewModel.GroupToggling -= PrepareGroupToggle; ViewModel.PropertyChanged -= OnModelChanged; _anchor = null; };
            BrowserScroll.AnchorRequested += (_, e) => { if (_anchor is { IsLoaded: true } anchor && IsHeaderVisible(anchor)) e.Anchor = anchor; };

        }

        private FrameworkElement? _anchor;
        private bool _initialScrollDone;
        private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(ServerListViewModel.SelectedServer)) QueueInitialScroll(); }
        private void QueueInitialScroll()
        {
            if (_initialScrollDone || ViewModel.SelectedServer is not { } selected) return;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_initialScrollDone || !IsLoaded) return;
                var row = ViewModel.NavigableRows.FirstOrDefault(r => r.Server.Id == selected.Id);
                if (row != null) { BringRowIntoView(row, false); _initialScrollDone = true; }
            });
        }
        private bool IsHeaderVisible(FrameworkElement header)
        {
            var y = header.TransformToVisual(BrowserScroll).TransformPoint(new Point()).Y;
            return y >= 0 && y < BrowserScroll.ActualHeight;
        }
        internal void RegisterHeader(FrameworkElement header) => BrowserScroll.RegisterAnchorCandidate(header);
        internal void UnregisterHeader(FrameworkElement header)
        { BrowserScroll.UnregisterAnchorCandidate(header); if (ReferenceEquals(_anchor, header)) _anchor = null; }
        internal void PrepareGroupToggle(ServerListGroup group)
        {
            var index = ViewModel.Groups.IndexOf(group);
            if (index < 0 || GroupRepeater.TryGetElement(index) is not SubscriptionGroupControl control) return;
            // Only visible headers can be native anchors. An offscreen header may
            // need to receive focus when a group is collapsed from a command.
            _anchor = IsHeaderVisible(control.HeaderAnchor) ? control.HeaderAnchor : null;
            if (_anchor != null)
            {
                BrowserScroll.InvalidateArrange();
                BrowserScroll.UpdateLayout();
            }
            control.PrepareCollapse();
        }
        internal void BringRowIntoView(ServerRowViewModel row, bool focus = true)
        {
            var group = ViewModel.Groups.FirstOrDefault(g => g.IsExpanded && g.Rows.Contains(row));
            if (group == null) return;
            _anchor = null;
            var control = (SubscriptionGroupControl)GroupRepeater.GetOrCreateElement(ViewModel.Groups.IndexOf(group));
            control.UpdateLayout();
            var element = control.GetRow(group.Rows.IndexOf(row));
            element.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            if (focus) element.FocusRow();
        }
        internal static ServerListControl? FindBrowser(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ServerListControl browser) return browser;
            return null;
        }

        internal async void ServerItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            if (element is not ServerRowControl { Model.Server: var server })
                return;

            if (!ReferenceEquals(ViewModel.SelectedServer, server))
                ViewModel.SelectedServer = server;

            var command = SwitchToSelectedServerCommand;
            if (command is null || !command.CanExecute(null))
                return;

            e.Handled = true;
            await command.ExecuteAsync(null);
        }

        internal void ServerItem_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            if (ViewModel.HasMultipleSelectedServers)
            {
                e.Handled = true;
                return;
            }

            if (sender is not FrameworkElement element)
            {
                e.Handled = true;
                return;
            }

            if (!ReferenceEquals((element as ServerRowControl)?.Model.Server, ViewModel.SelectedServer))
            {
                e.Handled = true;
                return;
            }

            var flyout = CreateSelectedServerContextFlyout();

            if (e.TryGetPosition(element, out Point point))
            {
                flyout.ShowAt(element, new FlyoutShowOptions {
                    Position = point
                });
            }
            else
            {
                flyout.ShowAt(element);
            }

            e.Handled = true;
        }

        private MenuFlyout CreateSelectedServerContextFlyout()
        {
            var flyout = new MenuFlyout();

            var editItem = CreateMenuItem(L.ServerList_Edit, "");
            editItem.IsEnabled = ViewModel.CanEditSelectedServer;
            editItem.Click += (_, _) => ViewModel.EditServerCommand.Execute(null);

            var isFavorite = ViewModel.SelectedServer?.IsFavorite == true;
            var favoriteItem = CreateMenuItem(
                isFavorite ? L.ServerList_RemoveFavorite : L.ServerList_AddFavorite,
                isFavorite ? "\uE8D9" : "\uE734");
            favoriteItem.Click += (_, _) => ViewModel.ToggleFavoriteCommand.Execute(null);

            var deleteItem = CreateMenuItem(L.ServerList_Delete, "");
            deleteItem.IsEnabled = ViewModel.CanRemoveSelectedServer;
            deleteItem.Click += (_, _) => ViewModel.RemoveServerCommand.Execute(null);

            var shareItem = CreateMenuItem(L.ServerList_Share, "");
            shareItem.Click += (_, _) => ViewModel.ShareServerCommand.Execute(null);

            flyout.Items.Add(editItem);
            flyout.Items.Add(favoriteItem);
            flyout.Items.Add(deleteItem);
            flyout.Items.Add(shareItem);
            var testItem = CreateMenuItem(Loc.GetString("Groups_TestServer"), "\uEC4A");
            testItem.IsEnabled = !ViewModel.IsTestingLatencies;
            var testedServer = ViewModel.SelectedServer;
            testItem.Click += async (_, _) => { if (testedServer != null) await ViewModel.TestServerAsync(testedServer); };
            flyout.Items.Add(testItem);

            return flyout;
        }

        private static MenuFlyoutItem CreateMenuItem(string text, string glyph)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };
        }

        public static double ActiveBadgeOpacity(bool isActive)
            => isActive ? 1.0 : 0.0;

        public static Vector3 ActiveBadgeScale(bool isActive)
            => isActive ? Vector3.One : new Vector3(0.92f, 0.92f, 1f);

        // Foreground for the per-row latency number, keyed off the measured value:
        // failed probe (negative, e.g. -1) → critical, ≥200 ms → caution, else success.
        public static Brush LatencyForeground(int? milliseconds)
        {
            var key = milliseconds switch
            {
                < 0   => "LatencyFailBrush",
                < 200 => "LatencyGoodBrush",
                _     => "LatencyHighBrush",
            };
            return (Brush)Application.Current.Resources[key];
        }
    }
}
