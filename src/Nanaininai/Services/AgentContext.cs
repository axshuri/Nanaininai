using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Configuration;
using Nanaininai.Core.Logging;
using Nanaininai.Platform;

namespace Nanaininai.Services;

/// <summary>Composition root: builds and owns every service instance.</summary>
public sealed class AgentContext
{
    public ILogService Log { get; }
    public ProfileStore Profiles { get; }
    public ISystemInfoProvider SystemInfo { get; }
    public INetworkConfigService NetworkConfig { get; }
    public IDiscoveryService Discovery { get; }
    public IFirewallService Firewall { get; }
    public ISmbService Smb { get; }
    public IShareService Shares { get; }
    public IDriveMappingService Drives { get; }
    public DiagnosticService Diagnostics { get; }
    public LanReportService LanReport { get; }
    public PrepareWorkflow Prepare { get; }

    public bool IsElevated { get; }

    public AgentContext()
    {
        Log = new LogService();
        Profiles = new ProfileStore();
        SystemInfo = new WindowsSystemInfoProvider();
        NetworkConfig = new WindowsNetworkConfigService(Log);
        Discovery = new WindowsDiscoveryService(Log);
        Firewall = new WindowsFirewallService(Log);
        Smb = new WindowsSmbService(Log);
        Shares = new WindowsShareService(Log);
        Drives = new WindowsDriveMappingService(Log);
        IsElevated = Elevation.IsAdmin();
        Diagnostics = new DiagnosticService(this);
        LanReport = new LanReportService(this);
        Prepare = new PrepareWorkflow(this);
        Log.Info("agent.start", Environment.ProcessPath, $"elevated={IsElevated} os={(OperatingSystem.IsWindows() ? "Windows" : "non-Windows")}");
    }
}
