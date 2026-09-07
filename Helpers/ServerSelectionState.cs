using System;
using System.Collections.Generic;

namespace XrayUI.Helpers;

/// <summary>Selection identity is independent of realized/recycled XAML elements.</summary>
public sealed class ServerSelectionState
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    public IReadOnlySet<string> Selected => _selected;
    public string? CurrentId { get; private set; }
    private string? _anchor;
    public void Replace(string? current, IEnumerable<string> selected)
    {
        CurrentId = current; _anchor = current; _selected.Clear();
        foreach (var id in selected) _selected.Add(id);
    }
    public void Choose(string id, IReadOnlyList<string> order, bool control, bool shift)
    {
        var end = IndexOf(order, id);
        if (end < 0) return;
        var start = IndexOf(order, _anchor);
        if (shift && start >= 0)
        {
            if (!control) _selected.Clear();
            for (var i = Math.Min(start, end); i <= Math.Max(start, end); i++) _selected.Add(order[i]);
        }
        else
        {
            if (!control) _selected.Clear();
            if (!control || !_selected.Remove(id)) _selected.Add(id);
            _anchor = id;
        }
        CurrentId = id;
    }
    public void Hide(IEnumerable<string> hidden)
    { foreach (var id in hidden) _selected.Remove(id); }
    private static int IndexOf(IReadOnlyList<string> values, string? id)
    { for (var i = 0; i < values.Count; i++) if (values[i] == id) return i; return -1; }
}
