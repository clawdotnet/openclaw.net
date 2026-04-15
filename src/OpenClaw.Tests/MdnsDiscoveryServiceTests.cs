using System.Buffers.Binary;
using System.Net;
using System.Text;
using OpenClaw.Gateway.Integrations;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MdnsDiscoveryServiceTests
{
    [Fact]
    public void TryParseQuery_ParsesPtrBrowseQuestion()
    {
        var query = BuildQueryPacket("_openclaw._tcp.local", MdnsDiscoveryService.DnsRecordType.Ptr);

        var parsed = MdnsDiscoveryService.TryParseQuery(query, out var transactionId, out var questions);

        Assert.True(parsed);
        Assert.Equal(0x1234, transactionId);
        var question = Assert.Single(questions);
        Assert.Equal("_openclaw._tcp.local", question.Name);
        Assert.Equal(MdnsDiscoveryService.DnsRecordType.Ptr, question.Type);
    }

    [Fact]
    public void BuildResponsePacket_UsesEffectiveAuthStateAndStandardDnsSdRecords()
    {
        var response = MdnsDiscoveryService.BuildResponsePacket(
            transactionId: 0x4242,
            instanceName: "OpenClaw",
            serviceType: "_openclaw._tcp",
            hostName: "openclaw-host",
            port: 18789,
            authRequired: true,
            addresses: [IPAddress.Parse("192.168.1.10")]);

        Assert.Equal((ushort)0x4242, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2)));
        Assert.Equal((ushort)0x8400, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10, 2)));
        Assert.Contains("auth=required", Encoding.UTF8.GetString(response), StringComparison.Ordinal);

        var recordTypes = ReadRecordTypes(response);
        Assert.Contains(MdnsDiscoveryService.DnsRecordType.Ptr, recordTypes);
        Assert.Contains(MdnsDiscoveryService.DnsRecordType.Srv, recordTypes);
        Assert.Contains(MdnsDiscoveryService.DnsRecordType.Txt, recordTypes);
        Assert.Contains(MdnsDiscoveryService.DnsRecordType.A, recordTypes);
    }

    [Fact]
    public void BuildResponsePacket_IncludesIpv6AddressRecords()
    {
        var response = MdnsDiscoveryService.BuildResponsePacket(
            transactionId: 0x4242,
            instanceName: "OpenClaw",
            serviceType: "_openclaw._tcp",
            hostName: "openclaw-host",
            port: 18789,
            authRequired: false,
            addresses: [IPAddress.Parse("2001:db8::42")]);

        var recordTypes = ReadRecordTypes(response);
        Assert.Contains(MdnsDiscoveryService.DnsRecordType.Aaaa, recordTypes);
    }

    private static byte[] BuildQueryPacket(string fqdn, ushort type)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0x1234);
        WriteUInt16(stream, 0x0000);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteName(stream, fqdn);
        WriteUInt16(stream, type);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static List<ushort> ReadRecordTypes(byte[] packet)
    {
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));
        var totalRecords = answerCount + additionalCount;
        var offset = 12;
        var types = new List<ushort>(totalRecords);

        for (var i = 0; i < totalRecords; i++)
        {
            ReadName(packet, ref offset);
            types.Add(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2)));
            offset += 2; // type
            offset += 2; // class
            offset += 4; // ttl
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2;
            offset += rdLength;
        }

        return types;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        while (packet[offset] != 0)
        {
            var len = packet[offset++];
            labels.Add(System.Text.Encoding.UTF8.GetString(packet, offset, len));
            offset += len;
        }

        offset++;
        return string.Join('.', labels);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
