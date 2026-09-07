using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace XrayUI.Services;

/// <summary>Read-only country index over the GeoIPList protobuf already shipped with Xray.</summary>
public sealed class GeoIpDatabase
{
    // Schema: https://github.com/XTLS/Xray-core/blob/main/common/geodata/geodat.proto
    // IPv4 networks use the IPv4-mapped IPv6 range, so both families share one index.
    private sealed record PrefixTable(UInt128[] Networks, ushort[] Countries);
    private readonly PrefixTable?[] _prefixes = new PrefixTable?[129];
    private readonly List<string?> _countries = new() { null };
    private readonly record struct Network(UInt128 Address, ushort Country);

    public static GeoIpDatabase Load(string path) => Parse(File.ReadAllBytes(path));

    public static GeoIpDatabase Parse(ReadOnlySpan<byte> data)
    {
        var result = new GeoIpDatabase();
        var networks = new List<Network>?[129];
        var list = new ProtoReader(data);
        while (list.Read(out var field, out _, out var entry))
        {
            if (field != 1 || entry.IsEmpty) continue;
            var reader = new ProtoReader(entry);
            string? code = null;
            var inverse = false;
            while (reader.Read(out field, out var scalar, out var bytes))
            {
                if (field == 1) code = Encoding.UTF8.GetString(bytes).ToUpperInvariant();
                if (field == 3) inverse = scalar != 0;
            }
            // Provider/category lists overlap countries and are not geographical labels.
            if (inverse || code == null || (code != "PRIVATE" &&
                (code.Length != 2 || code[0] is < 'A' or > 'Z' || code[1] is < 'A' or > 'Z' || code is "EU" or "UN" or "ZZ"))) continue;
            ushort country = 0;
            if (code != "PRIVATE")
            {
                country = checked((ushort)result._countries.Count);
                result._countries.Add(code);
            }
            reader = new ProtoReader(entry);
            while (reader.Read(out field, out _, out var bytes))
            {
                if (field != 2 || bytes.IsEmpty) continue;
                var cidr = new ProtoReader(bytes);
                ReadOnlySpan<byte> ip = default;
                ulong prefix = 0;
                while (cidr.Read(out var cidrField, out var scalar, out var value))
                {
                    if (cidrField == 1) ip = value;
                    if (cidrField == 2) prefix = scalar;
                }
                if (ip.Length is not (4 or 16) || prefix > (ulong)(ip.Length * 8))
                    throw new InvalidDataException("Invalid GeoIP network");
                var bits = (int)prefix + (ip.Length == 4 ? 96 : 0);
                var address = AddressValue(ip) & Mask(bits);
                (networks[bits] ??= new()).Add(new Network(address, country));
            }
        }
        for (var bits = 0; bits < networks.Length; bits++)
        {
            if (networks[bits] is not { } entries) continue;
            entries.Sort((a, b) => a.Address.CompareTo(b.Address));
            var addresses = new UInt128[entries.Count];
            var countries = new ushort[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            { addresses[i] = entries[i].Address; countries[i] = entries[i].Country; }
            result._prefixes[bits] = new PrefixTable(addresses, countries);
            networks[bits] = null;
        }
        return result;
    }

    public string? FindCountry(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return null;
        var ip = AddressValue(address.GetAddressBytes());
        for (var bits = 128; bits >= 0; bits--)
        {
            if (_prefixes[bits] is not { } table) continue;
            var index = Array.BinarySearch(table.Networks, ip & Mask(bits));
            if (index >= 0) return _countries[table.Countries[index]];
        }
        return null;
    }

    private static UInt128 AddressValue(ReadOnlySpan<byte> ip) => ip.Length == 4
        ? ((UInt128)0xffff << 32) | BinaryPrimitives.ReadUInt32BigEndian(ip)
        : BinaryPrimitives.ReadUInt128BigEndian(ip);
    private static UInt128 Mask(int bits) => bits == 0 ? 0 : UInt128.MaxValue << (128 - bits);

    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public bool Read(out int field, out ulong scalar, out ReadOnlySpan<byte> bytes)
        {
            field = 0; scalar = 0; bytes = default;
            if (_remaining.IsEmpty) return false;
            var tag = Varint();
            field = checked((int)(tag >> 3));
            if (field == 0) throw new InvalidDataException("Invalid GeoIP field");
            switch (tag & 7)
            {
                case 0: scalar = Varint(); break;
                case 1: Take(8); break;
                case 2:
                    var length = Varint();
                    if (length > (ulong)_remaining.Length) throw new InvalidDataException("Truncated GeoIP data");
                    bytes = Take((int)length); break;
                case 5: Take(4); break;
                default: throw new InvalidDataException("Unsupported GeoIP wire type");
            }
            return true;
        }
        private ReadOnlySpan<byte> Take(int length)
        {
            if (length > _remaining.Length) throw new InvalidDataException("Truncated GeoIP data");
            var value = _remaining[..length]; _remaining = _remaining[length..]; return value;
        }
        private ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var next = Take(1)[0];
                if (shift == 63 && next > 1) throw new InvalidDataException("Invalid GeoIP varint");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid GeoIP varint");
        }
    }
}
