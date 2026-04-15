using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Integrations;

/// <summary>
/// Advertises the OpenClaw gateway on the local network via mDNS/DNS-SD.
/// </summary>
internal sealed class MdnsDiscoveryService : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddressV4 = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::fb");
    private static readonly TimeSpan AdvertisedAddressCacheTtl = TimeSpan.FromSeconds(30);
    private const int MdnsPort = 5353;
    private const string ServiceEnumerationName = "_services._dns-sd._udp.local";

    private readonly MdnsConfig _config;
    private readonly int _gatewayPort;
    private readonly bool _authRequired;
    private readonly ILogger<MdnsDiscoveryService> _logger;
    private readonly Lock _addressCacheLock = new();
    private IReadOnlyList<IPAddress>? _cachedAdvertisedAddresses;
    private DateTimeOffset _cachedAdvertisedAddressesExpiresUtc;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _listenTasks = [];
    private readonly List<UdpClient> _udpClients = [];

    public MdnsDiscoveryService(MdnsConfig config, int gatewayPort, bool authRequired, ILogger<MdnsDiscoveryService> logger)
    {
        _config = config;
        _gatewayPort = gatewayPort;
        _authRequired = authRequired;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        if (!_config.Enabled)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var startedFamilies = new List<string>(capacity: 2);
        TryStartListener(AddressFamily.InterNetwork, MulticastAddressV4, IPAddress.Any, startedFamilies, _cts.Token);
        TryStartListener(AddressFamily.InterNetworkV6, MulticastAddressV6, IPAddress.IPv6Any, startedFamilies, _cts.Token);

        if (startedFamilies.Count == 0)
        {
            _logger.LogWarning("Failed to start mDNS discovery. Service advertisement disabled.");
            return;
        }

        var instanceName = _config.InstanceName ?? Environment.MachineName;
        var port = _config.Port > 0 ? _config.Port : _gatewayPort;
        _logger.LogInformation(
            "mDNS discovery started: {Instance}.{ServiceType}.local on port {Port} ({Families}).",
            instanceName,
            _config.ServiceType,
            port,
            string.Join(", ", startedFamilies));
    }

    private void TryStartListener(AddressFamily addressFamily, IPAddress multicastAddress, IPAddress bindAddress, ICollection<string> startedFamilies, CancellationToken ct)
    {
        try
        {
            var udp = CreateListenerSocket(addressFamily, multicastAddress, bindAddress);
            _udpClients.Add(udp);
            _listenTasks.Add(ListenLoopAsync(udp, new IPEndPoint(multicastAddress, MdnsPort), ct));
            startedFamilies.Add(addressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start {Family} mDNS listener.", addressFamily);
        }
    }

    private static UdpClient CreateListenerSocket(AddressFamily addressFamily, IPAddress multicastAddress, IPAddress bindAddress)
    {
        var udp = new UdpClient(addressFamily);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (addressFamily == AddressFamily.InterNetworkV6)
            udp.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);

        udp.Client.Bind(new IPEndPoint(bindAddress, MdnsPort));
        if (addressFamily == AddressFamily.InterNetworkV6)
            JoinIpv6MulticastGroups(udp, multicastAddress);
        else
            udp.JoinMulticastGroup(multicastAddress);
        return udp;
    }

    private static void JoinIpv6MulticastGroups(UdpClient udp, IPAddress multicastAddress)
    {
        var joined = false;
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var index = networkInterface.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
            if (index <= 0)
                continue;

            try
            {
                udp.JoinMulticastGroup(index, multicastAddress);
                joined = true;
            }
            catch (SocketException)
            {
            }
        }

        if (!joined)
            udp.JoinMulticastGroup(0, multicastAddress);
    }

    private async Task ListenLoopAsync(UdpClient udp, IPEndPoint responseEndpoint, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                if (!TryParseQuery(result.Buffer, out var transactionId, out var questions))
                    continue;

                var instanceName = SanitizeLabel(_config.InstanceName ?? Environment.MachineName);
                var hostName = SanitizeLabel(Environment.MachineName);
                if (!ShouldRespondToQuery(_config.ServiceType, instanceName, hostName, questions))
                    continue;

                var port = _config.Port > 0 ? _config.Port : _gatewayPort;
                var response = BuildResponsePacket(
                    transactionId,
                    instanceName,
                    _config.ServiceType,
                    hostName,
                    port,
                    _authRequired,
                    GetCachedAdvertisedAddresses());

                await udp.SendAsync(response, responseEndpoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS receive error (non-fatal).");
            }
        }
    }

    internal static bool TryParseQuery(byte[] packet, out ushort transactionId, out IReadOnlyList<DnsQuestion> questions)
    {
        transactionId = 0;
        questions = [];

        if (packet.Length < 12)
            return false;

        transactionId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2));
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        var offset = 12;
        var parsed = new List<DnsQuestion>(questionCount);

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadDomainName(packet, ref offset, out var name))
                return false;
            if (offset + 4 > packet.Length)
                return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            var @class = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2, 2));
            offset += 4;
            parsed.Add(new DnsQuestion(name, type, (ushort)(@class & 0x7FFF)));
        }

        questions = parsed;
        return parsed.Count > 0;
    }

    internal static bool ShouldRespondToQuery(string serviceType, string instanceName, string hostName, IReadOnlyList<DnsQuestion> questions)
    {
        var serviceFqdn = BuildServiceFqdn(serviceType);
        var instanceFqdn = BuildInstanceFqdn(instanceName, serviceType);
        var hostFqdn = BuildHostFqdn(hostName);

        foreach (var question in questions)
        {
            if (question.Name.Equals(serviceFqdn, StringComparison.OrdinalIgnoreCase) ||
                question.Name.Equals(instanceFqdn, StringComparison.OrdinalIgnoreCase) ||
                question.Name.Equals(hostFqdn, StringComparison.OrdinalIgnoreCase) ||
                question.Name.Equals(ServiceEnumerationName, StringComparison.OrdinalIgnoreCase))
            {
                return question.Type is DnsRecordType.Ptr or DnsRecordType.Srv or DnsRecordType.Txt or DnsRecordType.A or DnsRecordType.Aaaa or DnsRecordType.Any;
            }
        }

        return false;
    }

    internal static byte[] BuildResponsePacket(
        ushort transactionId,
        string instanceName,
        string serviceType,
        string hostName,
        int port,
        bool authRequired,
        IReadOnlyList<IPAddress> addresses)
    {
        var serviceFqdn = BuildServiceFqdn(serviceType);
        var instanceFqdn = BuildInstanceFqdn(instanceName, serviceType);
        var hostFqdn = BuildHostFqdn(hostName);
        var writer = new DnsMessageWriter();

        writer.WriteHeader(
            transactionId,
            flags: 0x8400,
            questionCount: 0,
            answerCount: 4,
            authorityCount: 0,
            additionalCount: (ushort)addresses.Count);

        writer.WritePtrRecord(serviceFqdn, instanceFqdn, ttlSeconds: 120);
        writer.WriteSrvRecord(instanceFqdn, hostFqdn, port, ttlSeconds: 120);
        writer.WriteTxtRecord(instanceFqdn, BuildTxtValues(port, authRequired), ttlSeconds: 120);
        writer.WritePtrRecord(ServiceEnumerationName, serviceFqdn, ttlSeconds: 4500);

        foreach (var address in addresses)
            writer.WriteAddressRecord(hostFqdn, address, ttlSeconds: 120);

        return writer.ToArray();
    }

    internal static IReadOnlyList<IPAddress> GetAdvertisedAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (IPAddress.IsLoopback(address))
                    continue;
                if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;
                if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                    continue;

                addresses.Add(address);
            }
        }

        if (addresses.Count == 0)
            addresses.Add(IPAddress.Loopback);

        return addresses
            .Distinct()
            .ToArray();
    }

    private IReadOnlyList<IPAddress> GetCachedAdvertisedAddresses()
    {
        lock (_addressCacheLock)
        {
            if (_cachedAdvertisedAddresses is not null && _cachedAdvertisedAddressesExpiresUtc > DateTimeOffset.UtcNow)
                return _cachedAdvertisedAddresses;
        }

        var refreshed = GetAdvertisedAddresses();
        lock (_addressCacheLock)
        {
            _cachedAdvertisedAddresses = refreshed;
            _cachedAdvertisedAddressesExpiresUtc = DateTimeOffset.UtcNow.Add(AdvertisedAddressCacheTtl);
            return _cachedAdvertisedAddresses;
        }
    }

    private static string[] BuildTxtValues(int port, bool authRequired)
        =>
        [
            "version=1.0",
            $"port={port}",
            $"auth={(authRequired ? "required" : "none")}"
        ];

    private static string BuildServiceFqdn(string serviceType)
        => $"{serviceType}.local";

    private static string BuildInstanceFqdn(string instanceName, string serviceType)
        => $"{SanitizeLabel(instanceName)}.{serviceType}.local";

    private static string BuildHostFqdn(string hostName)
        => $"{SanitizeLabel(hostName)}.local";

    private static string SanitizeLabel(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '-')
                builder.Append(c);
            else if (c is ' ' or '_' or '.')
                builder.Append('-');
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "openclaw" : sanitized;
    }

    private static bool TryReadDomainName(byte[] packet, ref int offset, out string name)
    {
        var labels = new List<string>();
        var jumped = false;
        var currentOffset = offset;
        var seenOffsets = new HashSet<int>();

        while (currentOffset < packet.Length)
        {
            var length = packet[currentOffset];
            if (length == 0)
            {
                currentOffset++;
                if (!jumped)
                    offset = currentOffset;
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (currentOffset + 1 >= packet.Length)
                    break;

                var pointer = ((length & 0x3F) << 8) | packet[currentOffset + 1];
                if (!seenOffsets.Add(pointer))
                    break;

                if (!jumped)
                    offset = currentOffset + 2;

                currentOffset = pointer;
                jumped = true;
                continue;
            }

            currentOffset++;
            if (currentOffset + length > packet.Length)
                break;

            labels.Add(Encoding.UTF8.GetString(packet, currentOffset, length));
            currentOffset += length;
            if (!jumped)
                offset = currentOffset;
        }

        name = "";
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenTasks.Count > 0)
        {
            try { await Task.WhenAll(_listenTasks); } catch (OperationCanceledException) { }
        }

        foreach (var udp in _udpClients)
            udp.Dispose();

        _cts?.Dispose();
    }

    internal sealed record DnsQuestion(string Name, ushort Type, ushort Class);

    internal static class DnsRecordType
    {
        public const ushort A = 1;
        public const ushort Ptr = 12;
        public const ushort Txt = 16;
        public const ushort Aaaa = 28;
        public const ushort Srv = 33;
        public const ushort Any = 255;
    }

    private sealed class DnsMessageWriter
    {
        private const ushort InternetClass = 0x0001;
        private const ushort CacheFlushClass = 0x8001;
        private readonly MemoryStream _buffer = new();

        public void WriteHeader(ushort transactionId, ushort flags, ushort questionCount, ushort answerCount, ushort authorityCount, ushort additionalCount)
        {
            WriteUInt16(transactionId);
            WriteUInt16(flags);
            WriteUInt16(questionCount);
            WriteUInt16(answerCount);
            WriteUInt16(authorityCount);
            WriteUInt16(additionalCount);
        }

        public void WritePtrRecord(string name, string value, uint ttlSeconds)
        {
            WriteName(name);
            WriteUInt16(DnsRecordType.Ptr);
            WriteUInt16(InternetClass);
            WriteUInt32(ttlSeconds);

            using var rdata = new MemoryStream();
            WriteName(rdata, value);
            WriteUInt16((ushort)rdata.Length);
            rdata.Position = 0;
            rdata.CopyTo(_buffer);
        }

        public void WriteSrvRecord(string name, string target, int port, uint ttlSeconds)
        {
            WriteName(name);
            WriteUInt16(DnsRecordType.Srv);
            WriteUInt16(CacheFlushClass);
            WriteUInt32(ttlSeconds);

            using var rdata = new MemoryStream();
            WriteUInt16(rdata, 0);
            WriteUInt16(rdata, 0);
            WriteUInt16(rdata, (ushort)port);
            WriteName(rdata, target);
            WriteUInt16((ushort)rdata.Length);
            rdata.Position = 0;
            rdata.CopyTo(_buffer);
        }

        public void WriteTxtRecord(string name, IReadOnlyList<string> values, uint ttlSeconds)
        {
            WriteName(name);
            WriteUInt16(DnsRecordType.Txt);
            WriteUInt16(CacheFlushClass);
            WriteUInt32(ttlSeconds);

            using var rdata = new MemoryStream();
            foreach (var value in values)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                rdata.WriteByte((byte)bytes.Length);
                rdata.Write(bytes, 0, bytes.Length);
            }

            WriteUInt16((ushort)rdata.Length);
            rdata.Position = 0;
            rdata.CopyTo(_buffer);
        }

        public void WriteAddressRecord(string name, IPAddress address, uint ttlSeconds)
        {
            WriteName(name);
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                WriteUInt16(DnsRecordType.A);
                WriteUInt16(CacheFlushClass);
                WriteUInt32(ttlSeconds);
                var bytes = address.GetAddressBytes();
                WriteUInt16((ushort)bytes.Length);
                _buffer.Write(bytes, 0, bytes.Length);
                return;
            }

            WriteUInt16(DnsRecordType.Aaaa);
            WriteUInt16(CacheFlushClass);
            WriteUInt32(ttlSeconds);
            var addressBytes = address.GetAddressBytes();
            WriteUInt16((ushort)addressBytes.Length);
            _buffer.Write(addressBytes, 0, addressBytes.Length);
        }

        public byte[] ToArray() => _buffer.ToArray();

        private void WriteName(string name) => WriteName(_buffer, name);

        private static void WriteName(Stream stream, string name)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }

            stream.WriteByte(0);
        }

        private void WriteUInt16(ushort value) => WriteUInt16(_buffer, value);

        private static void WriteUInt16(Stream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _buffer.Write(bytes);
        }
    }
}
