using System;
using System.Collections.Generic;
namespace XrayUI.Helpers;

public static class LanguageResolver
{
    public static string Resolve(string? preference, IEnumerable<string> systemLanguages)
    {
        if (preference is not null) return Match(preference) ?? "en-US";
        foreach (var language in systemLanguages)
            if (Match(language) is { } match) return match;
        return "en-US";
    }
    private static string? Match(string tag)
    {
        tag = tag.ToLowerInvariant();
        if (tag == "ru" || tag.StartsWith("ru-", StringComparison.Ordinal)) return "ru-RU";
        if (tag == "en" || tag.StartsWith("en-", StringComparison.Ordinal)) return "en-US";
        if (tag is "zh" or "zh-cn" or "zh-sg" or "zh-hans" || tag.StartsWith("zh-hans-", StringComparison.Ordinal)) return "zh-CN";
        return null;
    }
}
