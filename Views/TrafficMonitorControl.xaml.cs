using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using XrayUI.Helpers;

namespace XrayUI.Views;

public sealed partial class TrafficMonitorControl : UserControl
{
    public TrafficMonitorViewModel ViewModel { get; set; } = null!;
    private DispatcherQueueTimer? _timer;
    public TrafficMonitorControl()
    {
        InitializeComponent();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, Loc.GetString("Traffic_SearchAutomationName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FilterBox, Loc.GetString("Traffic_FilterAutomationName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortBox, Loc.GetString("Traffic_SortAutomationName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ChartWindow, Loc.GetString("Traffic_WindowAutomationName"));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_timer != null) return;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Refresh();
        Chart.SetSamples(ViewModel.Samples, ViewModel.WindowMinutes);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += OnTick;
        _timer.Start();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;
        _timer.Stop(); _timer.Tick -= OnTick; _timer = null;
        ViewModel.PropertyChanged -= OnViewModelChanged;
    }
    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (ViewModel.HasSource && Visibility == Visibility.Visible) ViewModel.Refresh();
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrafficMonitorViewModel.Samples) or nameof(TrafficMonitorViewModel.WindowMinutes))
            Chart.SetSamples(ViewModel.Samples, ViewModel.WindowMinutes);
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.Clear();
    private async void Details_Click(object sender, RoutedEventArgs e) => await ShowDetailsAsync();
    private async void AccessList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await ShowDetailsAsync();
    private bool _detailsOpen;
    private async System.Threading.Tasks.Task ShowDetailsAsync()
    {
        if (_detailsOpen || ViewModel.SelectedRow is not { } row) return;
        _detailsOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = Loc.GetString("Traffic_DetailsTitle"),
                Content = new TextBlock { Text = row.Details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                PrimaryButtonText = Loc.GetString("Traffic_CopyAddress"), CloseButtonText = Loc.GetString("Traffic_Close"),
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new DataPackage(); package.SetText(row.Destination); Clipboard.SetContent(package);
            }
        }
        finally { _detailsOpen = false; }
    }
}
