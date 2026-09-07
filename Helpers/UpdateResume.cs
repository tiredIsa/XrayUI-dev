using System;
using System.Collections.Generic;
using System.Text;

namespace XrayUI.Helpers;

/// <summary>A one-launch connection handoff, independent of Windows autostart settings.</summary>
public sealed record UpdateResume(string ServerId, bool TunMode)
{
    public const string TunPrefix = "--resume-update-tun=";
    public const string ProxyPrefix = "--resume-update-proxy=";

    // Base64 keeps IDs out of shell quoting and preserves arbitrary server IDs.
    public string ToArgument() => (TunMode ? TunPrefix : ProxyPrefix)
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerId));

    public static UpdateResume? Parse(IEnumerable<string> arguments)
    {
        UpdateResume? result = null;
        foreach (var argument in arguments)
        {
            var tun = argument.StartsWith(TunPrefix, StringComparison.Ordinal);
            if (!tun && !argument.StartsWith(ProxyPrefix, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            try
            {
                var prefix = tun ? TunPrefix : ProxyPrefix;
                var id = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(argument[prefix.Length..]));
                if (string.IsNullOrWhiteSpace(id)) return null;
                result = new UpdateResume(id, tun);
            }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }
        return result;
    }
}
