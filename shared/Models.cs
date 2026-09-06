using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace Nanaininai;

/// <summary>Cross-platform network adapter info used by Spectre.Console selector.</summary>
public sealed record AdapterInfo(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsUp);

/// <summary>Everything that might change between deployments. Never hardcode these values.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("computerNamePrefix")]
    public string ComputerNamePrefix { get; set; } = "LAB-PC";

    [JsonPropertyName("computerNamePadding")]
    public int ComputerNamePadding { get; set; } = 2;

    [JsonPropertyName("network")]
    public NetworkConfig Network { get; set; } = new();

    [JsonPropertyName("deployment")]
    public DeploymentConfig Deployment { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiConfig Ui { get; set; } = new();
}

public sealed class NetworkConfig
{
    [JsonPropertyName("controllerIp")]
    public string? ControllerIp { get; set; }

    [JsonPropertyName("subnetMask")]
    public string SubnetMask { get; set; } = "255.255.255.0";

    [JsonPropertyName("clientStartIp")]
    public string ClientStartIp { get; set; } = "192.168.50.101";

    [JsonPropertyName("gateway")]
    public string? Gateway { get; set; }

    [JsonPropertyName("dns")]
    public List<string> Dns { get; set; } = new();

    [JsonPropertyName("controllerPort")]
    public int? ControllerPort { get; set; }
}

public sealed class DeploymentConfig
{
    [JsonPropertyName("maxComputers")]
    public int MaxComputers { get; set; } = 17;

    [JsonPropertyName("confirmBeforeApply")]
    public bool ConfirmBeforeApply { get; set; } = true;

    [JsonPropertyName("allowAutoRestart")]
    public bool AllowAutoRestart { get; set; } = false;
}

public sealed class UiConfig
{
    [JsonPropertyName("showDetailedLogs")]
    public bool ShowDetailedLogs { get; set; } = true;
}

/// <summary>USB state: the next unused computer number.</summary>
public sealed class UsbState
{
    [JsonPropertyName("nextComputerNumber")]
    public int NextComputerNumber { get; set; } = 1;
}

/// <summary>Local per-PC marker proving this machine was already configured.</summary>
public sealed record OpResult(bool Success, string Message);

public sealed class LocalSetupMarker
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("computerNumber")]
    public int? ComputerNumber { get; set; }

    [JsonPropertyName("computerName")]
    public string? ComputerName { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }
}
