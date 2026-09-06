using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Tests;

public class LiveLocalizationTests
{
    [Theory]
    [InlineData("ru-RU", "en-US", "ru-RU")]
    [InlineData(null, "ru-BY", "ru-RU")]
    [InlineData(null, "en-GB", "en-US")]
    [InlineData(null, "zh-Hans-SG", "zh-CN")]
    [InlineData(null, "zh-Hant-TW", "en-US")]
    [InlineData(null, "de-DE", "en-US")]
    public void ResolvesPreferenceIndependentlyOfRegion(string? choice, string system, string expected) =>
        Assert.Equal(expected, LanguageResolver.Resolve(choice, [system]));

    [Fact]
    public void ChecksAllSystemPreferences() =>
        Assert.Equal("ru-RU", LanguageResolver.Resolve(null, ["de-DE", "ru-RU", "en-US"]));

    [Fact]
    public void StaleSettingsWriterDoesNotUndoLanguageChange()
    {
        var baseline = JsonNode.Parse("""{"Language":"en-US","Port":1000,"Subscriptions":[1]}""")!.AsObject();
        var current = JsonNode.Parse("""{"Language":"ru-RU","Port":1000,"Subscriptions":[1]}""")!.AsObject();
        var edited = JsonNode.Parse("""{"Language":"en-US","Port":2000,"Subscriptions":[1,2]}""")!.AsObject();
        var result = SettingsSnapshotMerge.Apply(current, baseline, edited);
        Assert.Equal("ru-RU", result["Language"]!.GetValue<string>());
        Assert.Equal(2000, result["Port"]!.GetValue<int>());
        Assert.Equal(2, result["Subscriptions"]!.AsArray().Count);
        Assert.Equal(1000, current["Port"]!.GetValue<int>());
    }

    [Fact]
    public void CanExplicitlyClearLanguageWithoutOverwritingNewerPort()
    {
        var before = JsonNode.Parse("""{"Language":"ru-RU","Port":1000}""")!.AsObject();
        var current = JsonNode.Parse("""{"Language":"ru-RU","Port":2000}""")!.AsObject();
        var edited = JsonNode.Parse("""{"Language":null,"Port":1000}""")!.AsObject();
        var result = SettingsSnapshotMerge.Apply(current, before, edited);
        Assert.Null(result["Language"]);
        Assert.Equal(2000, result["Port"]!.GetValue<int>());
    }

    [Fact]
    public void RebindingReplacesCallbackAndUnrelatedOwnersStillRefreshAfterFailure()
    {
        var owner = new object(); var other = new object(); var value = 0;
        try
        {
            LocalizationBindings.Bind(owner, "Text", () => value = 1, false);
            LocalizationBindings.Bind(owner, "Text", () => value = 2, false);
            LocalizationBindings.Refresh();
            Assert.Equal(2, value);
            LocalizationBindings.Bind(owner, "Text", () => throw new InvalidOperationException(), false);
            LocalizationBindings.Bind(other, "Text", () => value = 3, false);
            Assert.Throws<AggregateException>(LocalizationBindings.Refresh);
            Assert.Equal(3, value);
        }
        finally { LocalizationBindings.Remove(owner); LocalizationBindings.Remove(other); }
    }

    [Fact]
    public void RefreshUsesReplacementRegisteredByEarlierCallback()
    {
        var parent = new object(); var child = new object(); var text = "";
        try
        {
            LocalizationBindings.Bind(parent, "Layout", () =>
                LocalizationBindings.Bind(child, "Text", () => text = "current"), false);
            LocalizationBindings.Bind(child, "Text", () => text = "stale", false);
            LocalizationBindings.Refresh();
            Assert.Equal("current", text);
        }
        finally { LocalizationBindings.Remove(parent); LocalizationBindings.Remove(child); }
    }

    [Fact]
    public void RemovedOwnerIsSkippedDuringCurrentRefresh()
    {
        var parent = new object(); var child = new object(); var calls = 0;
        try
        {
            LocalizationBindings.Bind(parent, "Layout", () => LocalizationBindings.Remove(child), false);
            LocalizationBindings.Bind(child, "Text", () => calls++, false);
            LocalizationBindings.Refresh();
            LocalizationBindings.Refresh();
            Assert.Equal(0, calls);
        }
        finally { LocalizationBindings.Remove(parent); LocalizationBindings.Remove(child); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateOwner()
    {
        var owner = new object();
        LocalizationBindings.Bind(owner, "Text", () => GC.KeepAlive(owner), false);
        return new(owner);
    }

    [Fact]
    public void CallbackCapturingOwnerDoesNotKeepItAlive()
    {
        var weak = CreateOwner();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        LocalizationBindings.Refresh();
    }

    [Fact]
    public void AllLocalesHaveMatchingKeysAndFormatArguments()
    {
        static Dictionary<string, string> Read(string locale) => XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Locales", locale, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(n => (string)n.Attribute("name")!, n => n.Element("value")!.Value, StringComparer.OrdinalIgnoreCase);
        var english = Read("en-US");
        foreach (var locale in new[] { "ru-RU", "zh-CN" })
        {
            var translated = Read(locale);
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), key);
                System.Text.CompositeFormat.Parse(value);
                static string[] Arguments(string text) => Regex.Matches(text, @"\{\d+[^{}]*\}").Select(m => m.Value).Order().ToArray();
                Assert.Equal(Arguments(english[key]), Arguments(value));
            }
        }
    }
}
