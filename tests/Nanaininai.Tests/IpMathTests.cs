using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Diagnostics;
using Nanaininai.Core.Models;
using Nanaininai.Core.Network;
using Xunit;

namespace Nanaininai.Tests;

public class IpMathTests
{
    [Theory]
    [InlineData("192.168.10.10", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1 ", true)]  // surrounding whitespace is trimmed
    public void Valid_Ips_Parse(string text, bool expected)
    {
        Assert.Equal(expected, IpMath.TryParseIp(text, out _));
    }

    [Theory]
    [InlineData("256.1.1.1")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.1.1")]
    [InlineData("01.2.3.4")]      // leading zero
    [InlineData("a.b.c.d")]
    [InlineData("")]
    public void Invalid_Ips_Rejected(string text)
    {
        Assert.False(IpMath.TryParseIp(text, out _));
    }

    [Theory]
    [InlineData("/24", 24)]
    [InlineData("/0", 0)]
    [InlineData("/32", 32)]
    [InlineData("/8", 8)]
    public void Cidr_Parses(string text, int expected)
    {
        Assert.True(IpMath.TryParseCidr(text, out var p));
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData("/33")]
    [InlineData("/-1")]
    [InlineData("/abc")]
    [InlineData("24")]   // missing slash
    public void Bad_Cidr_Rejected(string text)
    {
        Assert.False(IpMath.TryParseCidr(text, out _));
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.0.0.0", 8)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.255.255.252", 30)]
    [InlineData("0.0.0.0", 0)]
    public void Masks_Convert_To_Prefix(string mask, int expected)
    {
        Assert.True(IpMath.TryParseMask(mask, out var p));
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData("255.0.255.0")]   // non-contiguous
    [InlineData("255.255.0.255")]
    public void NonContiguous_Masks_Rejected(string mask)
    {
        Assert.False(IpMath.TryParseMask(mask, out _));
    }

    [Fact]
    public void Network_And_Broadcast_Are_Computed()
    {
        IpMath.TryParseIp("192.168.10.130", out var ip);
        Assert.Equal(IpMath.ToString(IpMath.NetworkOf(ip, 25)), "192.168.10.128");
        Assert.Equal(IpMath.ToString(IpMath.BroadcastOf(ip, 25)), "192.168.10.255");
        Assert.Equal(IpMath.ToString(IpMath.NetworkOf(ip, 24)), "192.168.10.0");
    }

    [Fact]
    public void Host_Range_Is_Computed()
    {
        IpMath.TryParseIp("192.168.10.0", out var net);
        Assert.Equal("192.168.10.1", IpMath.ToString(IpMath.FirstHost(net, 24)));
        Assert.Equal("192.168.10.254", IpMath.ToString(IpMath.LastHost(net, 24)));
        Assert.Equal(254, IpMath.UsableHosts(24));
    }

    [Fact]
    public void Slash31_And_32_Have_No_Excluded_Addresses()
    {
        IpMath.TryParseIp("10.0.0.4", out var ip);
        Assert.Equal(2, IpMath.UsableHosts(31));
        Assert.Equal(1, IpMath.UsableHosts(32));
    }

    [Theory]
    [InlineData("192.168.10.10", "192.168.10.200", 24, true)]
    [InlineData("192.168.10.10", "192.168.11.10", 24, false)]
    [InlineData("192.168.10.10", "192.168.10.200", 25, false)]  // different /25 halves
    [InlineData("192.168.10.10", "192.168.10.200", 26, false)]
    public void Same_Subnet_Detection(string a, string b, int prefix, bool expected)
    {
        IpMath.TryParseIp(a, out var x);
        IpMath.TryParseIp(b, out var y);
        Assert.Equal(expected, IpMath.InSameSubnet(x, y, prefix));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]     // loopback
    [InlineData("169.254.5.5", true)]   // APIPA
    [InlineData("224.0.0.1", true)]     // multicast
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.10.10", false)]
    [InlineData("10.1.2.3", false)]
    public void Reserved_Addresses(string ip, bool expected)
    {
        IpMath.TryParseIp(ip, out var v);
        Assert.Equal(expected, IpMath.IsReserved(v));
    }

    [Theory]
    [InlineData("192.168.10.10", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Private_Range_Detection(string ip, bool expected)
    {
        IpMath.TryParseIp(ip, out var v);
        Assert.Equal(expected, IpMath.IsPrivateRange(v));
    }
}

public class IpValidatorTests
{
    private static IpConfig Static(string ip, int prefix, string? gw, string? dns1, string? dns2 = null) =>
        new() { Mode = IpConfigMode.Static, Address = ip, PrefixLength = prefix, Gateway = gw, PreferredDns = dns1, AlternateDns = dns2 };

    [Fact]
    public void Valid_Static_Config_Passes()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.10.1", "192.168.10.1", "8.8.8.8"));
        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Gateway_Outside_Subnet_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.20.1", "192.168.10.1"));
        Assert.Contains(r.Errors, e => e.Contains("outside the subnet"));
    }

    [Fact]
    public void Gateway_Equal_To_Self_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.10.10", null));
        Assert.Contains(r.Errors, e => e.Contains("must not be the address of this PC"));
    }

    [Theory]
    [InlineData("192.168.10.0", 24)]
    [InlineData("192.168.10.255", 24)]
    public void Network_Or_Broadcast_As_Host_Is_Error(string ip, int prefix)
    {
        var r = IpValidator.ValidateStatic(Static(ip, prefix, "192.168.10.1", null));
        Assert.Contains(r.Errors, e => e.Contains("network or broadcast"));
    }

    [Fact]
    public void Reserved_Address_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("169.254.10.5", 24, null, null));
        Assert.Contains(r.Errors, e => e.Contains("reserved address"));
    }

    [Fact]
    public void Duplicate_Ip_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.10.1", null),
            knownIps: new[] { "192.168.10.10" });
        Assert.Contains(r.Errors, e => e.Contains("conflict"));
    }

    [Fact]
    public void Duplicate_Ip_Not_Triggered_By_Other_Host()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.10.1", null),
            knownIps: new[] { "192.168.10.11" });
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Invalid_Gateway_Format_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "not-an-ip", null));
        Assert.Contains(r.Errors, e => e.Contains("Gateway"));
    }

    [Fact]
    public void Missing_Gateway_And_Dns_Are_Warnings_Not_Errors()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, null, null));
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("gateway"));
        Assert.Contains(r.Warnings, w => w.Contains("DNS"));
    }

    [Fact]
    public void Invalid_Dns_Is_Error()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 24, "192.168.10.1", "300.1.1.1"));
        Assert.Contains(r.Errors, e => e.Contains("Preferred DNS"));
    }

    [Fact]
    public void Prefix_Out_Of_Range_Is_Rejected()
    {
        var r = IpValidator.ValidateStatic(Static("192.168.10.10", 33, null, null));
        Assert.False(r.IsValid);
    }
}

public class IpPlannerTests
{
    [Fact]
    public void Plans_18_Pcs_Without_Duplicates()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 18, "192.168.10.1", startIp: "192.168.10.10", namePrefix: "SCHOOL-PC");
        Assert.True(plan.IsValid, string.Join(";", plan.Validation.Errors));
        var ips = plan.Entries.Where(e => !e.IsGateway).Select(e => e.Ip).ToList();
        Assert.Equal(18, ips.Count);
        Assert.Equal(ips.Count, ips.Distinct().Count());
        Assert.Equal("192.168.10.10", ips[0]);
        Assert.Equal("192.168.10.27", ips[17]);
        Assert.Contains(plan.Entries, e => e.Name == "SCHOOL-PC-01" && e.Ip == "192.168.10.10");
        Assert.Contains(plan.Entries, e => e.Name == "SCHOOL-PC-18" && e.Ip == "192.168.10.27");
    }

    [Fact]
    public void Gateway_Is_Never_Allocated()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 254, "192.168.10.1", startIp: "192.168.10.1");
        var allocated = plan.Entries.Where(e => !e.IsGateway).Select(e => e.Ip);
        Assert.DoesNotContain("192.168.10.1", allocated);
    }

    [Fact]
    public void Extra_Reservations_Are_Skipped()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 3, "192.168.10.1",
            reservedExtra: new[] { "192.168.10.11" }, startIp: "192.168.10.10");
        var ips = plan.Entries.Where(e => !e.IsGateway).Select(e => e.Ip).ToList();
        Assert.DoesNotContain("192.168.10.11", ips);
        Assert.Equal(new[] { "192.168.10.10", "192.168.10.12", "192.168.10.13" }, ips);
    }

    [Fact]
    public void Network_And_Broadcast_Never_Allocated()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 254);
        var allocated = plan.Entries.Where(e => !e.IsGateway).Select(e => e.Ip);
        Assert.DoesNotContain("192.168.10.0", allocated);
        Assert.DoesNotContain("192.168.10.255", allocated);
    }

    [Fact]
    public void Too_Small_Subnet_Is_Reported()
    {
        var plan = IpPlanner.Plan("192.168.10.0/30", 5);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Validation.Errors, e => e.Contains("free addresses"));
    }

    [Fact]
    public void Start_Ip_Outside_Range_Is_Reported()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 10, startIp: "192.168.11.10");
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void Non_Cidr_Input_Is_Reported()
    {
        var plan = IpPlanner.Plan("banana", 5);
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void Gateway_Outside_Subnet_Is_Reported()
    {
        var plan = IpPlanner.Plan("192.168.10.0/24", 5, "10.0.0.1");
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void Smaller_Subnet_Planning_Works()
    {
        var plan = IpPlanner.Plan("192.168.10.16/28", 13, "192.168.10.17", startIp: "192.168.10.18");
        Assert.True(plan.IsValid, string.Join(";", plan.Validation.Errors));
        var ips = plan.Entries.Where(e => !e.IsGateway).Select(e => e.Ip).ToList();
        Assert.Equal(13, ips.Count);
        Assert.Equal("192.168.10.18", ips[0]);
        Assert.Equal("192.168.10.30", ips[^1]); // last usable in /28
        Assert.DoesNotContain("192.168.10.16", ips);
        Assert.DoesNotContain("192.168.10.31", ips);
    }
}

public class HealthScoreTests
{
    [Fact]
    public void Perfect_Checks_Score_100()
    {
        var report = HealthScorer.Compute(new[]
        {
            ("Adapter", DiagnosticStatus.Pass, 0, "ok"),
            ("IPv4", DiagnosticStatus.Pass, 0, "ok"),
            ("Firewall", DiagnosticStatus.Pass, 0, "ok"),
        });
        Assert.Equal(100, report.Score);
        Assert.Empty(report.Deductions);
    }

    [Fact]
    public void Warnings_Deduct_The_Specified_Points()
    {
        var report = HealthScorer.Compute(new[]
        {
            ("Gateway", DiagnosticStatus.Warn, 5, "gateway silent"),
            ("DNS", DiagnosticStatus.Warn, 1, "no dns"),
        });
        Assert.Equal(94, report.Score);
        Assert.Equal(2, report.Deductions.Count());
    }

    [Fact]
    public void Failures_Cost_Double()
    {
        var report = HealthScorer.Compute(new[]
        {
            ("File Sharing", DiagnosticStatus.Fail, 15, "smb down"),
        });
        Assert.Equal(70, report.Score);
    }

    [Fact]
    public void Score_Never_Goes_Below_Zero()
    {
        var report = HealthScorer.Compute(Enumerable.Range(0, 20).Select(i => ($"C{i}", DiagnosticStatus.Fail, 15, "bad")));
        Assert.Equal(0, report.Score);
    }

    [Fact]
    public void Every_Deduction_Has_A_Reason()
    {
        var report = HealthScorer.Compute(new[]
        {
            ("Firewall", DiagnosticStatus.Warn, 5, "ICMP Echo blocked"),
            ("DNS", DiagnosticStatus.Warn, 1, "DNS configuration unavailable"),
        });
        Assert.All(report.Deductions, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
        Assert.Contains(report.Deductions, d => d.Reason == "ICMP Echo blocked");
        Assert.Contains(report.Deductions, d => d.Reason == "DNS configuration unavailable");
    }
}

public class StrictInputTests
{
    [Theory]
    [InlineData("SchoolShare", true)]
    [InlineData("School_Share-01.v2", true)]
    [InlineData("with space", false)]
    [InlineData("-lead", false)]
    [InlineData("", false)]
    [InlineData("bad;name", false)]
    [InlineData("bad$name", false)]
    public void Share_Name_Validation(string name, bool expected)
    {
        Assert.Equal(expected, StrictInput.IsValidShareName(name));
    }

    [Theory]
    [InlineData(@"D:\SchoolShare", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\server\share", false)]      // UNC is not a local path
    [InlineData(@"D:\bad<name", false)]
    [InlineData(@"D:\bad|name", false)]
    [InlineData("D:relative", false)]
    [InlineData("", false)]
    public void Local_Path_Validation(string path, bool expected)
    {
        Assert.Equal(expected, StrictInput.IsValidLocalPath(path));
    }

    [Theory]
    [InlineData(@"\\192.168.10.10\SchoolShare", true)]
    [InlineData(@"\\SERVER-01\share", true)]
    [InlineData(@"\\server\share\sub", true)]
    [InlineData(@"\\server", false)]
    [InlineData(@"\\se rver\share", false)]
    [InlineData("http://server/share", false)]
    [InlineData("", false)]
    public void Unc_Path_Validation(string path, bool expected)
    {
        Assert.Equal(expected, StrictInput.IsValidUncPath(path));
    }

    [Theory]
    [InlineData("SCHOOL-PC-01", true)]
    [InlineData("PC1", true)]
    [InlineData("has space", false)]
    [InlineData("way-too-long-computer-name", false)]
    [InlineData("-bad", false)]
    [InlineData("", false)]
    public void Computer_Name_Validation(string name, bool expected)
    {
        Assert.Equal(expected, StrictInput.IsValidComputerName(name));
    }

    [Theory]
    [InlineData("Authenticated Users", true)]
    [InlineData("bad&pipe", false)]
    [InlineData("bad;semi", false)]
    [InlineData("bad$var", false)]
    public void Identity_Validation(string id, bool expected)
    {
        Assert.Equal(expected, StrictInput.IsValidIdentity(id));
    }
}

public class PlanAndLogTests
{
    [Fact]
    public void Dry_Run_Plan_Renders_Changes_Without_Applying()
    {
        var plan = new OperationPlan
        {
            Title = "Configure IPv4 on 'Ethernet'",
            Changes =
            {
                new PlannedChange { Index = 1, Category = "Address", Description = "Set IPv4 to 192.168.10.10/24" },
                new PlannedChange { Index = 2, Category = "Gateway", Description = "Set Gateway to 192.168.10.1" },
            }
        };
        var text = plan.Render(dryRun: true);
        Assert.Contains("DRY RUN", text);
        Assert.Contains("No changes were made.", text);
        Assert.Contains("192.168.10.10/24", text);
    }

    [Fact]
    public void Log_Service_Scrubs_Credential_Material()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nanaininai-tests", Guid.NewGuid().ToString("N"));
        ILogService log = new Nanaininai.Core.Logging.LogService(dir);
        log.Error("share.create", "S", "password=hunter2 failed");
        var entry = log.GetEntries().Single();
        Assert.Contains("REDACTED", entry.ErrorMessage ?? "");
        Assert.DoesNotContain("hunter2", entry.ErrorMessage ?? "");
    }

    [Fact]
    public void Log_Service_Writes_Structured_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nanaininai-tests", Guid.NewGuid().ToString("N"));
        ILogService log = new Nanaininai.Core.Logging.LogService(dir);
        log.Info("network.apply", "Ethernet", "ok");
        Assert.True(File.Exists(log.LogFilePath));
        var text = File.ReadAllText(log.LogFilePath);
        Assert.Contains("\"op\":\"network.apply\"", text);
    }

    [Fact]
    public void Profile_Store_Bundles_The_School_18_Pc_Example()
    {
        var p = Nanaininai.Core.Configuration.ProfileStore.DefaultSchoolProfile();
        Assert.Equal("SCHOOL-18-PC", p.Name);
        Assert.Equal("192.168.10.0/24", p.NetworkCidr);
        Assert.Equal("192.168.10.1", p.Gateway);
        Assert.Equal("192.168.10.10", p.StartIp);
        Assert.Equal(18, p.PcCount);
        Assert.Equal("SCHOOL-PC", p.NamePrefix);
    }
}
