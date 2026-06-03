namespace LanAdmin.Server;

public sealed class BootstrapOptions
{
    public string ServerBaseUrl { get; set; } = "http://localhost:5000";
    public int DiscoveryUdpPort { get; set; } = 5010;
}
