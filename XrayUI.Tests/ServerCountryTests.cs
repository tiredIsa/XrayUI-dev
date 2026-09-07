using System.Net;
using System.Text;
using XrayUI.Models;
using XrayUI.Services;
using XrayUI.ViewModels;

namespace XrayUI.Tests;

public class ServerCountryTests
{
    private static byte[] Var(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
        bytes.Add((byte)value); return bytes.ToArray();
    }
    private static byte[] Blob(int field, byte[] bytes) => Var((ulong)(field << 3 | 2)).Concat(Var((ulong)bytes.Length)).Concat(bytes).ToArray();
    private static byte[] Scalar(int field, ulong value) => Var((ulong)(field << 3)).Concat(Var(value)).ToArray();
    private static byte[] Country(string code, string ip, int prefix, bool inverse = false)
    {
        var cidr = Blob(1, IPAddress.Parse(ip).GetAddressBytes()).Concat(Scalar(2, (ulong)prefix)).ToArray();
        return Blob(1, Blob(1, Encoding.UTF8.GetBytes(code)).Concat(Blob(2, cidr)).Concat(Scalar(3, inverse ? 1UL : 0)).ToArray());
    }
    private static GeoIpDatabase Fixture() => GeoIpDatabase.Parse(
        Country("US", "8.0.0.0", 8).Concat(Country("DE", "8.8.8.0", 24))
        .Concat(Country("FR", "2001:db8:8000::", 33))
        .Concat(Country("GOOGLE", "8.8.8.8", 32))
        .Concat(Country("GB", "8.8.8.8", 32, inverse: true))
        .Concat(Country("PRIVATE", "10.0.0.0", 8)).ToArray());

    [Theory]
    [InlineData("8.8.8.0", "DE")]
    [InlineData("8.8.8.255", "DE")]
    [InlineData("8.8.9.0", "US")]
    [InlineData("::ffff:8.8.8.8", "DE")]
    [InlineData("2001:db8:8000::1", "FR")]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", "FR")]
    [InlineData("2001:db8:7fff::1", null)]
    [InlineData("10.1.2.3", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("::1", null)]
    [InlineData("9.0.0.0", null)]
    public void UsesLongestPrefixAcrossBothFamiliesAndIgnoresRoutingCategories(string ip, string? expected)
        => Assert.Equal(expected, Fixture().FindCountry(IPAddress.Parse(ip)));

    [Fact]
    public void HandlesUnknownProtobufFieldsAndRejectsTruncatedOrInvalidNetworks()
    {
        var data = Country("DE", "8.8.8.0", 24).Concat(Blob(99, [1, 2])).ToArray();
        Assert.Equal("DE", GeoIpDatabase.Parse(data).FindCountry(IPAddress.Parse("8.8.8.8")));
        Assert.Throws<InvalidDataException>(() => GeoIpDatabase.Parse(data[..^1]));
        Assert.Throws<InvalidDataException>(() => GeoIpDatabase.Parse(Country("DE", "8.8.8.0", 33)));
    }

    [Fact]
    public void ShippedDatabaseContainsCountriesAndExcludesPrivateNetworks()
    {
        var database = GeoIpDatabase.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "geoip.dat"));
        Assert.Equal("US", database.FindCountry(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("DE", database.FindCountry(IPAddress.Parse("136.243.0.1")));
        Assert.Null(database.FindCountry(IPAddress.Parse("192.168.1.1")));
        Assert.Null(database.FindCountry(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public async Task CoalescesDnsCachesResultsAndSkipsDnsForLiteralIps()
    {
        var calls = 0;
        var completion = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServerCountryService(Fixture, (_, _) => { Interlocked.Increment(ref calls); return completion.Task; });
        var first = service.GetCountryAsync(" NODE.DOMAIN. ");
        var second = service.GetCountryAsync("node.domain");
        completion.SetResult([IPAddress.Parse("8.8.8.8")]);
        Assert.Equal("DE", await first); Assert.Equal("DE", await second);
        Assert.Equal("DE", await service.GetCountryAsync("node.domain"));
        Assert.Equal("DE", await service.GetCountryAsync("[::ffff:8.8.8.8]"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SuppressesAmbiguousCountriesAndSurvivesMissingDatabase()
    {
        var service = new ServerCountryService(Fixture, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.9.0.1") }));
        Assert.Null(await service.GetCountryAsync("mixed.domain"));
        Assert.Null(await service.GetCountryAsync("fixture.test"));
        var missing = new ServerCountryService(() => throw new FileNotFoundException(), (_, _) => throw new Exception("DNS should not run"));
        Assert.Null(await missing.GetCountryAsync("node.domain"));
    }

    [Fact]
    public async Task LimitsParallelDnsLookups()
    {
        var running = 0; var maximum = 0;
        var service = new ServerCountryService(Fixture, async (_, token) =>
        {
            var active = Interlocked.Increment(ref running);
            int previous;
            do { previous = maximum; } while (active > previous && Interlocked.CompareExchange(ref maximum, active, previous) != previous);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref running);
            return [IPAddress.Parse("8.8.8.8")];
        });
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.GetCountryAsync($"node{i}.domain")));
        Assert.InRange(maximum, 1, 4);
    }

    [Fact]
    public async Task EditingOrReplacingServerDoesNotApplyOldDnsResult()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServerCountryService(Fixture, (_, _) => { requested.TrySetResult(); return completion.Task; });
        var original = new ServerEntry { Host = "old.domain", Protocol = "vless" };
        using var row = new ServerRowViewModel(original, service);
        var pending = row.EnsureCountryAsync();
        await requested.Task;
        original.Host = "new.test";
        completion.SetResult([IPAddress.Parse("8.8.8.8")]);
        await pending;
        Assert.Null(row.CountryCode);
        row.Server = new ServerEntry { Host = "8.8.8.8", Protocol = "vless" };
        await row.EnsureCountryAsync();
        Assert.Equal("DE", row.CountryCode);
        original.Host = "8.9.0.1";
        Assert.Equal("DE", row.CountryCode);
        row.Server.Protocol = "chain";
        Assert.False(row.HasCountry);
    }
}
