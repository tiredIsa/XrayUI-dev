using System;
using System.Net;
using System.Text.RegularExpressions;
using XrayUI.Models.Traffic;

namespace XrayUI.Services.Traffic;

/// <summary>Parses the compact access lines emitted by Xray, for example
/// "from 127.0.0.1:1234 accepted tcp:example.com:443 [proxy]".</summary>
public sealed class XrayAccessLogParser : IXrayAccessEventParser
{
    private static readonly Regex Pattern = new(
        @"from\s+(?<source>[^\s]+)\s+accepted\s+(?<transport>tcp|udp):(?<destination>[^\s]+)(?:\s+\[(?<route>[^\]]+)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public TrafficAccessEvent? Parse(string line, DateTimeOffset receivedAt)
    {
        var match = Pattern.Match(line);
        if (!match.Success) return null;
        var transport = match.Groups["transport"].Value.Equals("udp", StringComparison.OrdinalIgnoreCase)
            ? TrafficTransport.Udp : TrafficTransport.Tcp;
        var routeTag = match.Groups["route"].Value;
        var route = routeTag.Equals("direct", StringComparison.OrdinalIgnoreCase) ? TrafficRouteKind.Direct
            : routeTag.Equals("block", StringComparison.OrdinalIgnoreCase) ? TrafficRouteKind.Blocked
            : routeTag.Equals("proxy", StringComparison.OrdinalIgnoreCase) ? TrafficRouteKind.Proxy
            : TrafficRouteKind.Unknown;
        return new(receivedAt, ParseAddress(match.Groups["source"].Value),
            ParseAddress(match.Groups["destination"].Value), transport, TrafficAccessStatus.Accepted,
            "mixed-in", string.IsNullOrWhiteSpace(routeTag) ? null : routeTag, route);
    }

    private static TrafficAddress ParseAddress(string value)
    {
        if (Uri.TryCreate("tcp://" + value, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return new(uri.Host, uri.Port > 0 ? uri.Port : null);
        return new(value, null);
    }
}
