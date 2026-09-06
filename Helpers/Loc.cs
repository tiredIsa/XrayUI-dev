using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XrayUI.Services;

namespace XrayUI.Helpers;

public sealed record LanguageState(string? Preference, string EffectiveTag, long Revision);

/// <summary>UI-thread language transactions; immutable snapshots are safe for background readers.</summary>
public static class Loc
{
    private sealed record Snapshot(LanguageState State, ImmutableDictionary<string, string> Strings);
    private static Snapshot? _snapshot;
    private static readonly SemaphoreSlim ChangeGate = new(1, 1);
    public static LanguageState Current => _snapshot?.State ?? new(null, "en-US", 0);

    public static void Initialize(string? preference)
    {
        var tag = LanguageResolver.Resolve(preference, SystemLanguages());
        _snapshot = Prepare(preference, tag, 0);
    }

    private static IEnumerable<string> SystemLanguages()
    {
        uint count = 0, length = 0;
        if (!GetUserPreferredUILanguages(8, ref count, null, ref length) || length == 0)
            return [CultureInfo.InstalledUICulture.Name];
        var buffer = new char[length];
        return GetUserPreferredUILanguages(8, ref count, buffer, ref length)
            ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : [CultureInfo.InstalledUICulture.Name];
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, ref uint count,
        [System.Runtime.InteropServices.Out] char[]? buffer, ref uint length);

    private static Snapshot Prepare(string? preference, string tag, long revision)
    {
        var strings = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadCatalog("en-US", strings);
        if (tag != "en-US") ReadCatalog(tag, strings);
        return new(new(preference, tag, revision), strings.ToImmutable());
    }

    private static void ReadCatalog(string tag, ImmutableDictionary<string, string>.Builder strings)
    {
        // MRT can keep the startup PrimaryLanguageOverride even for an explicit
        // ResourceContext. Read the same source catalogs directly so the selected
        // language is deterministic and independent of native resource caches.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"XrayUI.Localization.{tag}")
            ?? throw new InvalidOperationException($"Localization catalog is missing: {tag}");
        foreach (var entry in XDocument.Load(stream).Root!.Elements("data"))
        {
            var key = entry.Attribute("name")!.Value;
            strings[key] = entry.Element("value")!.Value;
        }
    }

    public static string GetString(string key)
    {
        var snapshot = Volatile.Read(ref _snapshot) ?? throw new InvalidOperationException("Localization is not initialized.");
        return snapshot.Strings.TryGetValue(key.Replace('/', '.'), out var text) ? text : key;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, GetString(key), args);

    public static async Task ChangeAsync(string? preference, SettingsService settings)
    {
        if (preference != null && LanguageHelper.Normalize(preference) == null)
            throw new ArgumentException("Unsupported language.", nameof(preference));
        await ChangeGate.WaitAsync();
        try
        {
            var current = Current;
            var tag = LanguageResolver.Resolve(preference, SystemLanguages());
            if (current.Preference == preference && current.EffectiveTag == tag) { LocalizationBindings.Refresh(); return; }
            var next = Prepare(preference, tag, current.Revision + 1);
            await settings.UpdateSettingsAsync(s => s.Language = preference);
            Volatile.Write(ref _snapshot, next);
            LocalizationBindings.Refresh();
        }
        finally { ChangeGate.Release(); }
    }

    public static void RefreshSystemLanguage()
    {
        if (Current.Preference != null || ChangeGate.CurrentCount == 0) return;
        var tag = LanguageResolver.Resolve(null, SystemLanguages());
        if (tag == Current.EffectiveTag) return;
        Volatile.Write(ref _snapshot, Prepare(null, tag, Current.Revision + 1));
        LocalizationBindings.Refresh();
    }
}
