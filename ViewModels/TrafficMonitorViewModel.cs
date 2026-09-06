using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using XrayUI.Helpers;
using XrayUI.Models.Traffic;
using XrayUI.Services.Traffic;

namespace XrayUI.ViewModels;

public sealed partial record TrafficDisplayRow(TrafficAccessRow Row)
{
    public string Time => Row.Access.ObservedAt.ToLocalTime().ToString("HH:mm:ss");
    public string Destination => TrafficPresentation.Address(Row.Access.Destination);
    public string Source => TrafficPresentation.Address(Row.Access.Source);
    public string Protocol => Row.Access.Transport.ToString().ToUpperInvariant();
    public string Context => $"{Protocol} · {Source}";
    public string Route => Loc.GetString("Traffic_Route" + Row.Access.Route);
    public string Application => Row.Access.ProcessName ?? Loc.GetString("Traffic_UnknownApplication");
    public string RouteColor => Row.Access.Route switch
    {
        TrafficRouteKind.Proxy => "#168BBA",
        TrafficRouteKind.Direct => "#23865F",
        TrafficRouteKind.Blocked => "#C64755",
        _ => "#747B89"
    };
    public string Details => $"{Destination}\n{Context}\n{Row.Access.InboundTag ?? "—"} → {Row.Access.OutboundTag ?? "—"}\n{Application}\n{Row.Access.Status}";
}

/// <summary>UI-thread presentation only. Source injection does not create or control a collector.
/// Pause freezes the snapshot; filtering still works on that frozen snapshot.</summary>
public partial class TrafficMonitorViewModel : ObservableObject
{
    private readonly ITrafficMonitorReader? _reader;
    private TrafficMonitorSnapshot _snapshot = new(null, TrafficMonitorStatus.Stopped, null, default, null, 0, 0, [], []);
    public TrafficRowCollection Rows { get; } = new();
    public bool HasSource => _reader != null;
    public TrafficMonitorViewModel(ITrafficMonitorReader? reader = null) { _reader = reader; Refresh(); }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial int RouteFilter { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial int WindowIndex { get; set; }
    [ObservableProperty] public partial TrafficDisplayRow? SelectedRow { get; set; }
    public int WindowMinutes => WindowIndex == 0 ? 5 : 15;
    public ImmutableArray<TrafficSample> Samples => _snapshot.Samples;
    public bool HasRows => Rows.Count > 0;
    public bool HasSelection => SelectedRow != null;
    public bool CanClear => HasSource && (_snapshot.AccessCount > 0 || !_snapshot.Samples.IsEmpty);
    public string Download => TrafficPresentation.Rate(_snapshot.Speed?.DownloadBytesPerSecond);
    public string Upload => TrafficPresentation.Rate(_snapshot.Speed?.UploadBytesPerSecond);
    public string Volume => _snapshot.Session == null ? "—" : TrafficPresentation.Bytes(_snapshot.Volume.DownloadBytes + (double)_snapshot.Volume.UploadBytes);
    public string AccessCount => _snapshot.Session == null ? "—" : _snapshot.AccessCount.ToString("N0");
    public string RowCount => Loc.Format("Traffic_RowCount", Rows.Count, _snapshot.AccessCount, _snapshot.EvictedAccessCount);
    public string Status => Loc.GetString(IsPaused ? "Traffic_Paused" : !HasSource ? "Traffic_SourcePending" : "Traffic_Status" + _snapshot.Status);
    public string EmptyText => Loc.GetString(!HasSource ? "Traffic_EmptyPending" : _snapshot.AccessCount > 0 ? "Traffic_EmptyFilter" : "Traffic_Empty");
    public string Details => SelectedRow?.Details ?? string.Empty;

    public void Refresh()
    {
        if (IsPaused) return;
        _snapshot = _reader?.GetSnapshot() ?? _snapshot;
        UpdateView();
    }
    private void UpdateView()
    {
        RebuildRows();
        foreach (var name in new[] { nameof(Samples), nameof(Download), nameof(Upload), nameof(Volume), nameof(AccessCount), nameof(Status), nameof(CanClear) })
            OnPropertyChanged(name);
    }
    public void Clear()
    {
        _reader?.Clear();
        _snapshot = _reader?.GetSnapshot() ?? _snapshot;
        UpdateView();
    }
    partial void OnIsPausedChanged(bool value) { OnPropertyChanged(nameof(Status)); if (!value) Refresh(); }
    partial void OnSearchTextChanged(string value) => RebuildRows();
    partial void OnRouteFilterChanged(int value) => RebuildRows();
    partial void OnSortIndexChanged(int value) => RebuildRows();
    partial void OnWindowIndexChanged(int value) => OnPropertyChanged(nameof(WindowMinutes));
    partial void OnSelectedRowChanged(TrafficDisplayRow? value) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(Details)); }

    private void RebuildRows()
    {
        var search = SearchText.Trim();
        var query = _snapshot.Accesses.Select(row => new TrafficDisplayRow(row)).Where(row =>
            (RouteFilter == 0 || (int)row.Row.Access.Route == RouteFilter) &&
            (search.Length == 0 || row.Destination.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             row.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             row.Application.Contains(search, StringComparison.OrdinalIgnoreCase)));
        var sorted = SortIndex == 1 ? query.OrderBy(row => row.Destination, StringComparer.OrdinalIgnoreCase).ThenByDescending(row => row.Row.Sequence)
            : query.OrderByDescending(row => row.Row.Sequence);
        var desired = sorted.ToArray();
        var selected = SelectedRow?.Row;
        // Keep items and selection stable between polls when no access events changed.
        if (!Rows.Select(r => r.Row).SequenceEqual(desired.Select(r => r.Row)))
        {
            Rows.ReplaceAll(desired);
            SelectedRow = Rows.FirstOrDefault(row => row.Row.Sequence == selected?.Sequence && row.Row.SessionId == selected?.SessionId);
        }
        foreach (var name in new[] { nameof(HasRows), nameof(RowCount), nameof(EmptyText) }) OnPropertyChanged(name);
    }
}

/// <summary>One reset per batch instead of thousands of UI notifications during a traffic burst.</summary>
// CsWinRT generates the collection's COM interfaces in a partial declaration.
// Without it, WinUI cannot accept this derived collection as ItemsSource.
#if WINDOWS
[WinRT.GeneratedWinRTExposedType]
#endif
public sealed partial class TrafficRowCollection : ObservableCollection<TrafficDisplayRow>, System.Collections.IList
{
    internal void ReplaceAll(IEnumerable<TrafficDisplayRow> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
