namespace DeployManager.Models;

public class Machine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Hostname { get; set; } = "";
    // Stored as AA:BB:CC:DD:EE:FF
    public string MacAddress { get; set; } = "";
    // Last known IP — used for PSRemoting PXE reboot
    public string IpAddress { get; set; } = "";
    public string Site { get; set; } = "";
    public string OU { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? LastImaged { get; set; }
    public string LastJobId { get; set; } = "";
    // Set each time the machine registers from WinPE. Used to show "waiting in WinPE" badge.
    public DateTime? DiscoveredAt { get; set; }

    public static string NormalizeMac(string mac) =>
        string.Join(":", mac.Replace("-", "").Replace(":", "")
            .ToUpper().Chunk(2).Select(c => new string(c)));
}
