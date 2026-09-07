using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.ViewModels;

public sealed partial class ServerListViewModel
{
    public ObservableCollection<ServerListGroup> Groups { get; } = new();
    private readonly Dictionary<string, ServerListGroup> _displayGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly ServerSelectionState _selection = new();
    private bool _applyingSelection;
    private HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private Task _groupSave = Task.CompletedTask;
    internal Task FlushGroupStateAsync() => _groupSave;
    public event Action<ServerListGroup>? GroupToggling;

    public ServerRowViewModel[] NavigableRows => Groups.Where(g => g.IsExpanded).SelectMany(g => g.Rows).ToArray();
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var old = target.IndexOf(desired[i]);
            if (old >= 0) target.Move(old, i); else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
    private void RebuildGroups()
    {
        var desired = new List<ServerListGroup>();
        var visible = VisibleServers.GroupBy(s => s.SubscriptionId ?? "").ToDictionary(g => g.Key, g => g.ToList());
        var ids = _knownSubscriptions.Select(s => s.Id).Concat(Servers.Select(s => s.SubscriptionId ?? "")).Distinct().ToArray();
        foreach (var removed in _displayGroups.Keys.Except(ids).ToArray())
        { _displayGroups[removed].Dispose(); _displayGroups.Remove(removed); }
        foreach (var server in Servers)
        {
            if (_rows.TryGetValue(server.Id, out var row)) row.Server = server;
            else _rows[server.Id] = new ServerRowViewModel(server);
        }
        foreach (var removed in _rows.Keys.Except(Servers.Select(s => s.Id)).ToArray())
        { _rows[removed].Dispose(); _rows.Remove(removed); }
        foreach (var id in ids)
        {
            var subscription = _knownSubscriptions.FirstOrDefault(s => s.Id == id);
            if (!_displayGroups.TryGetValue(id, out var group))
                _displayGroups[id] = group = new ServerListGroup(this, id, subscription);
            else group.SetSubscription(subscription);
            var matches = visible.GetValueOrDefault(id) ?? new List<ServerEntry>();
            Reconcile(group.Rows, matches.Select(s => _rows[s.Id]).ToArray());
            group.Count = matches.Count;
            var searching = !string.IsNullOrWhiteSpace(SearchQuery);
            group.IsExpanded = searching || !_collapsedGroups.Contains(id);
            if (matches.Count == 0 && (searching || SelectedChip?.Kind != ServerGroupChip.ChipKind.All)) continue;
            desired.Add(group);
        }
        Reconcile(Groups, desired);
        _selection.Hide(_selection.Selected.Except(NavigableRows.Select(r => r.Server.Id)).ToArray());
        ApplyBrowserSelection();
    }
    public void ToggleGroup(ServerListGroup group) => SetGroupExpanded(group, !group.IsExpanded);
    public void SetGroupExpanded(ServerListGroup group, bool expanded)
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery) || group.IsExpanded == expanded) return;
        GroupToggling?.Invoke(group);
        if (expanded) _collapsedGroups.Remove(group.Id); else _collapsedGroups.Add(group.Id);
        group.IsExpanded = expanded;
        if (!expanded) { _selection.Hide(group.Rows.Select(r => r.Server.Id)); ApplyBrowserSelection(); }
        _groupSave = SaveGroupStateAsync(_groupSave, _collapsedGroups.ToList());
    }
    public void SelectRow(ServerRowViewModel row, bool control = false, bool shift = false)
    {
        _selection.Choose(row.Server.Id, NavigableRows.Select(r => r.Server.Id).ToArray(), control, shift);
        _applyingSelection = true;
        try { SelectedServer = row.Server; } finally { _applyingSelection = false; }
        ApplyBrowserSelection();
    }
    public void SelectAllRows()
    {
        _selection.Replace(SelectedServer?.Id, NavigableRows.Select(r => r.Server.Id));
        ApplyBrowserSelection();
    }
    private void SyncBrowserSelection(IReadOnlyList<ServerEntry> selected)
    {
        if (_applyingSelection) return;
        _selection.Replace(SelectedServer?.Id, selected.Select(s => s.Id));
        UpdateSelectionFlags();
    }
    private void ApplyBrowserSelection()
    {
        _applyingSelection = true;
        try { SetSelectedServers(_selection.Selected.Where(_rows.ContainsKey).Select(id => _rows[id].Server).ToArray()); }
        finally { _applyingSelection = false; }
        UpdateSelectionFlags();
    }
    private void UpdateSelectionFlags()
    { foreach (var row in _rows.Values) row.IsSelected = _selection.Selected.Contains(row.Server.Id); }
    private async Task SaveGroupStateAsync(Task previous, List<string> collapsed)
    {
        try { await previous; await _settings.UpdateSettingsAsync(s => s.CollapsedServerGroups = collapsed); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Groups] Save failed: {ex}"); }
    }
    public async Task RefreshGroupAsync(ServerListGroup group)
    {
        if (group.Subscription is not { IsBusy: false } sub) return;
        await RunGroupActionAsync(() => RefreshSubscriptionsAsync(new[] { sub }, null));
    }
    public async Task ConfigureGroupAsync(ServerListGroup group)
    {
        if (group.Subscription is not { IsBusy: false } sub) return;
        await RunGroupActionAsync(async () =>
        {
            var vm = new ManageSubscriptionsViewModel(new[] { sub }, RefreshSubscriptionsAsync, DeleteSubscriptionAsync, EditSubscriptionAsync);
            vm.ShowManagePage();
            var added = await _dialogs.ShowSubscriptionsDialogAsync(vm);
            if (added != null) { await UpsertSubscriptionAsync(added); TrackKnownSubscription(added); await RefreshSubscriptionsAsync(new[] { added }, null); }
            RebuildAll();
        });
    }
    public async Task DeleteGroupAsync(ServerListGroup group)
    {
        if (group.Subscription is not { IsBusy: false } sub) return;
        await RunGroupActionAsync(async () =>
        {
            if (!await _dialogs.ShowConfirmationAsync(LocalizedText.Key("Groups_DeleteTitle"),
                LocalizedText.Format("Groups_DeleteMessage", group.Title), isDanger: true)) return;
            if (!await DeleteSubscriptionAsync(sub))
                await _dialogs.ShowErrorAsync(LocalizedText.Key("Groups_DeleteTitle"), sub.LastErrorText);
        });
    }
    public async Task TestGroupAsync(ServerListGroup group) => await RunGroupActionAsync(() =>
        TestServersAsync(Servers.Where(s => (s.SubscriptionId ?? "") == group.Id).ToList()));
    public async Task TestServerAsync(ServerEntry server) => await RunGroupActionAsync(() => TestServersAsync(new() { server }));
    private async Task RunGroupActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync(LocalizedText.Key("Groups_Error"), ex.Message); }
    }
}
