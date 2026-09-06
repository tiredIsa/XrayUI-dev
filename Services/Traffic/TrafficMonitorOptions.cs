using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace XrayUI.Services.Traffic;

/// <summary>Tags come from the generated configuration. No wildcard aggregation of all inbounds
/// (API/test traffic) or all outbounds (chain hops). Options are copied and validated once.</summary>
public sealed class TrafficMonitorOptions
{
    public int AccessCapacity { get; }
    public int SampleCapacity { get; }
    public ImmutableArray<string> InboundTags { get; }
    public ImmutableArray<string> OutboundTags { get; }

    public TrafficMonitorOptions(IEnumerable<string> inboundTags, IEnumerable<string> outboundTags,
        int accessCapacity = 5000, int sampleCapacity = 900)
    {
        ArgumentNullException.ThrowIfNull(inboundTags);
        ArgumentNullException.ThrowIfNull(outboundTags);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accessCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCapacity);
        InboundTags = CopyTags(inboundTags);
        OutboundTags = CopyTags(outboundTags);
        if (InboundTags.IsEmpty) throw new ArgumentException("At least one measured inbound is required.", nameof(inboundTags));
        AccessCapacity = accessCapacity;
        SampleCapacity = sampleCapacity;
    }

    private static ImmutableArray<string> CopyTags(IEnumerable<string> tags)
    {
        var copied = tags.ToImmutableArray();
        if (copied.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Counter tags cannot be empty.", nameof(tags));
        return copied.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }
}
