using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using XrayUI.Models.Traffic;

namespace XrayUI.Helpers;

public readonly record struct TrafficPlotPoint(double X, double Y);
public sealed record TrafficPlot(ImmutableArray<ImmutableArray<TrafficPlotPoint>> Download,
    ImmutableArray<ImmutableArray<TrafficPlotPoint>> Upload, double Maximum);

public static class TrafficPresentation
{
    public static string Bytes(double value)
    {
        if (!double.IsFinite(value) || value < 0) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
    public static string Rate(double? value) => value.HasValue ? Bytes(value.Value) + "/s" : "—";
    public static string Address(TrafficAddress? address) => address is null ? "—" :
        address.Port is int port ? $"{(address.Host.Contains(':') ? "[" + address.Host + "]" : address.Host)}:{port}" : address.Host;

    public static TrafficPlot Plot(ImmutableArray<TrafficSample> samples, int minutes, double width, double height)
    {
        if (samples.IsDefaultOrEmpty || width <= 0 || height <= 0) return new([], [], 1);
        var end = samples[^1].Elapsed.TotalSeconds;
        var start = end - Math.Max(1, minutes) * 60;
        var visible = samples.Where(s => s.Elapsed.TotalSeconds >= start).ToArray();
        var max = Math.Max(1, visible.Where(s => s.Speed.HasValue).Select(s =>
            Math.Max(s.Speed!.Value.DownloadBytesPerSecond, s.Speed.Value.UploadBytesPerSecond)).DefaultIfEmpty(1).Max()) * 1.15;
        ImmutableArray<ImmutableArray<TrafficPlotPoint>> Build(bool upload)
        {
            var segments = ImmutableArray.CreateBuilder<ImmutableArray<TrafficPlotPoint>>();
            var points = ImmutableArray.CreateBuilder<TrafficPlotPoint>();
            TimeSpan? previous = null;
            foreach (var sample in visible)
            {
                // Do not join lines across missing measurements or collector stalls.
                if (!sample.Speed.HasValue || (previous.HasValue && sample.Elapsed - previous.Value > TimeSpan.FromSeconds(3)))
                {
                    if (points.Count > 0) { segments.Add(points.ToImmutable()); points.Clear(); }
                }
                if (sample.Speed is { } speed)
                {
                    var rate = upload ? speed.UploadBytesPerSecond : speed.DownloadBytesPerSecond;
                    points.Add(new((sample.Elapsed.TotalSeconds - start) / (end - start) * width,
                        height - Math.Clamp(rate / max, 0, 1) * height));
                }
                previous = sample.Elapsed;
            }
            if (points.Count > 0) segments.Add(points.ToImmutable());
            return segments.ToImmutable();
        }
        return new(Build(false), Build(true), max);
    }
}
