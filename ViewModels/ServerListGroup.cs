using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.ViewModels;

public sealed partial class ServerListGroup : ObservableObject, IDisposable
{
    private readonly ServerListViewModel _owner;
    public string Id { get; }
    public SubscriptionEntry? Subscription { get; private set; }
    public System.Collections.ObjectModel.ObservableCollection<ServerRowViewModel> Rows { get; } = new();
    public bool IsManual => Id.Length == 0;
    public string Title => Subscription is { } sub
        ? (string.IsNullOrWhiteSpace(sub.Name) ? L.ServerList_UnnamedSub : sub.Name)
        : IsManual ? L.ServerList_Ungrouped : L.ServerList_OrphanSub;
    public string Subtitle => Subscription is { } sub
        ? $"{Count} · {sub.LastUpdatedText} · {sub.AutoRefreshSummaryText}".TrimEnd(' ', '·')
        : Count.ToString();
    public string Usage
    {
        get
        {
            if (Subscription is not { } sub) return "";
            var used = (double)Math.Max(0, sub.Upload ?? 0) + Math.Max(0, sub.Download ?? 0);
            var traffic = sub.Upload.HasValue || sub.Download.HasValue || sub.HasTraffic
                ? TrafficPresentation.Bytes(used) + (sub.HasTraffic ? " / " + TrafficPresentation.Bytes(sub.Total!.Value)
                    : sub.Total == 0 ? " / ∞" : "") : "";
            return string.Join(" · ", new[] { traffic, sub.ExpiryText }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
    public string CompactSummary => HasUsage ? $"{Count} · {Usage}" : Subtitle;
    public string DetailsTooltip => HasUsage ? Subtitle + Environment.NewLine + Usage : Subtitle;
    public bool HasUsage => !string.IsNullOrEmpty(Usage);
    public bool HasQuota => Subscription?.HasTraffic == true;
    public double UsagePercent => Subscription is { Total: > 0 } sub
        ? Math.Clamp(((double)(sub.Upload ?? 0) + (sub.Download ?? 0)) / sub.Total.Value * 100, 0, 100) : 0;
    public string Error => Subscription?.LastErrorText ?? "";
    public bool HasError => Subscription?.HasError == true;
    public bool CanRefresh => Subscription is { IsBusy: false };
    public bool HasSubscription => Subscription != null;
    public bool IsAutoRefreshDisabled => Subscription is { IsAutoRefreshEnabled: false };
    public bool CanTest => Count > 0 && !_owner.IsTestingLatencies;
    public bool IsBusy => Subscription?.IsBusy == true || IsTesting;
    public string ProgressText => IsTesting ? $"{Completed} / {TestTotal}" : "";
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    [ObservableProperty] public partial int Count { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial int Completed { get; set; }
    public int TestTotal { get; set; }
    public IRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand TestCommand { get; }
    public IAsyncRelayCommand ConfigureCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    public ServerListGroup(ServerListViewModel owner, string id, SubscriptionEntry? subscription)
    {
        _owner = owner; Id = id; Subscription = subscription;
        ToggleCommand = new RelayCommand(() => owner.ToggleGroup(this));
        RefreshCommand = new AsyncRelayCommand(() => owner.RefreshGroupAsync(this));
        TestCommand = new AsyncRelayCommand(() => owner.TestGroupAsync(this));
        ConfigureCommand = new AsyncRelayCommand(() => owner.ConfigureGroupAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteGroupAsync(this));
        if (Subscription != null) Subscription.PropertyChanged += OnSubscriptionChanged;
    }
    public void SetSubscription(SubscriptionEntry? subscription)
    {
        if (ReferenceEquals(Subscription, subscription)) return;
        if (Subscription != null) Subscription.PropertyChanged -= OnSubscriptionChanged;
        Subscription = subscription;
        if (Subscription != null) Subscription.PropertyChanged += OnSubscriptionChanged;
        Refresh();
    }
    private void OnSubscriptionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));
    partial void OnCountChanged(int value) { OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(CompactSummary)); OnPropertyChanged(nameof(DetailsTooltip)); OnPropertyChanged(nameof(CanTest)); }
    partial void OnIsTestingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(CanTest)); }
    partial void OnCompletedChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(CompactSummary), nameof(DetailsTooltip), nameof(Usage), nameof(HasUsage), nameof(HasQuota), nameof(UsagePercent), nameof(Error), nameof(HasError), nameof(CanRefresh), nameof(HasSubscription), nameof(IsAutoRefreshDisabled), nameof(CanTest), nameof(IsBusy), nameof(ProgressText) }) OnPropertyChanged(name);
    }
    public void Dispose() { if (Subscription != null) Subscription.PropertyChanged -= OnSubscriptionChanged; }
}
