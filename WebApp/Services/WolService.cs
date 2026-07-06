using System.Net;
using System.Net.Sockets;

namespace DeployManager.Services;

public class WolService
{
    private readonly ILogger<WolService> _logger;

    public WolService(ILogger<WolService> logger) => _logger = logger;

    public bool Send(string macAddress, string? targetIp = null)
    {
        try
        {
            var mac = macAddress.Replace(":", "").Replace("-", "");
            if (mac.Length != 12)
                throw new ArgumentException($"Invalid MAC address: {macAddress}");

            var macBytes = Enumerable.Range(0, 6)
                .Select(i => Convert.ToByte(mac.Substring(i * 2, 2), 16))
                .ToArray();

            // Magic packet: 6x 0xFF then MAC repeated 16 times = 102 bytes
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++) Array.Copy(macBytes, 0, packet, i * 6, 6);

            // Build list of broadcast addresses to try.
            // 255.255.255.255 only works on the local subnet.
            // For cross-subnet WoL we need the directed broadcast of the target's /24,
            // e.g. 192.168.192.7 → 192.168.192.255. Routers must have directed broadcast
            // forwarding enabled (ip directed-broadcast on Cisco, or equivalent).
            // Send to machine's unicast IP — switch CAM table delivers it directly to the port.
            // Falls back to global broadcast if no IP is recorded.
            IPAddress target = IPAddress.Broadcast;
            if (!string.IsNullOrWhiteSpace(targetIp) &&
                IPAddress.TryParse(targetIp.Trim(), out var parsed) &&
                parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                target = parsed;
            }

            using var client = new UdpClient();
            client.EnableBroadcast = true;
            client.Send(packet, packet.Length, new IPEndPoint(target, 9));
            client.Send(packet, packet.Length, new IPEndPoint(target, 7));

            _logger.LogInformation("WoL magic packet sent to {Mac} via {Target}", macAddress, target);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WoL packet to {Mac}", macAddress);
            return false;
        }
    }
}
