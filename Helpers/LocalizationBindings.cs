using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace XrayUI.Helpers;

/// <summary>Owners retain callbacks; the global registry retains only weak references.</summary>
public static partial class LocalizationBindings
{
    static partial void RetainNativeScope(object owner, object scope);
    static partial void ReleaseNativeScope(object owner);
    private sealed class Scope { public readonly Dictionary<string, Action> Callbacks = new(); }
    private static readonly ConditionalWeakTable<object, Scope> Owners = new();
    private static readonly List<WeakReference<Scope>> Scopes = new();

    public static void Bind(object owner, string key, Action refresh, bool apply = true)
    {
        var scope = Owners.GetValue(owner, _ =>
        {
            var created = new Scope();
            Scopes.Add(new(created));
            return created;
        });
        RetainNativeScope(owner, scope);
        scope.Callbacks[key] = refresh;
        if (apply) refresh();
    }
    public static void Remove(object owner)
    {
        if (Owners.TryGetValue(owner, out var scope)) scope.Callbacks.Clear();
        Owners.Remove(owner);
        ReleaseNativeScope(owner);
    }
    public static T BindValue<T>(T owner, string key, Action<T> refresh) where T : class
    {
        Bind(owner, key, () => refresh(owner));
        return owner;
    }
    public static void Refresh()
    {
        var callbacks = new List<(Scope Scope, string Key)>();
        var errors = new List<Exception>();
        Scopes.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in Scopes.ToArray())
            if (reference.TryGetTarget(out var scope))
                foreach (var key in scope.Callbacks.Keys) callbacks.Add((scope, key));
        foreach (var (scope, key) in callbacks)
        {
            // Earlier callbacks may replace a conditional caption or detach its owner.
            if (!scope.Callbacks.TryGetValue(key, out var callback)) continue;
            try { callback(); }
            catch (Exception ex) { Debug.WriteLine($"[Localization] Refresh failed: {ex}"); errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Some interface captions could not be refreshed. Select the language again to retry.", errors);
    }
}
