using Nanaininai.Core.Models;
using Nanaininai.Core.Network;
using Terminal.Gui;

namespace Nanaininai.TUI;

/// <summary>Main dashboard: system + network summary + health score.</summary>
public static class ScreenDashboard
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;

        // Cached (5 s TTL): switching back to the dashboard is instant.
        var sys = await TuiApp.GetCachedAsync("sys", TuiApp.CacheShort, () => ctx.SystemInfo.GetSystemInfoAsync()).ConfigureAwait(false);
        var adapter = sys.PrimaryAdapter;
        var ipCfg = adapter is null
            ? null
            : await TuiApp.GetCachedAsync($"ip:{adapter.Name}", TuiApp.CacheShort, () => ctx.NetworkConfig.GetIpConfigAsync(adapter.Name)).ConfigureAwait(false);
        var (results, health) = await TuiApp.GetCachedAsync("diag", TuiApp.CacheMedium, () => ctx.Diagnostics.RunAllWithHealthAsync()).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var y = 0;
            Label L(string t) { var l = new Label(t) { X = 1, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }

            L("SYSTEM");
            L($"  Computer Name   : {sys.ComputerName}");
            L($"  Windows         : {sys.Edition} ({sys.WindowsVersion})");
            L($"  Build           : {sys.Build}");
            L($"  User            : {sys.CurrentUser}");
            L($"  Workgroup       : {sys.Workgroup}{(sys.IsDomainJoined ? " (domain joined)" : "")}");
            y++;
            L("NETWORK");
            L($"  Adapter         : {adapter?.Name ?? "-"} ({adapter?.Status ?? "none"})");
            L($"  MAC             : {adapter?.Mac ?? "-"}");
            L($"  IPv4            : {ipCfg?.Address ?? "-"}{(ipCfg?.PrefixLength is int p ? $"/{p}" : "")}");
            L($"  Mode            : {ipCfg?.Mode.ToString() ?? "-"}");
            L($"  Gateway         : {ipCfg?.Gateway ?? "-"}");
            L($"  DNS             : {string.Join(", ", adapter?.DnsServers ?? new List<string>())}");
            L($"  Profile         : {adapter?.NetworkCategory ?? "-"}");
            y++;
            var scoreLabel = new Label($"LAN HEALTH: {health.Score} / 100  ({health.Deductions.Count()} deductions)")
            {
                X = 1, Y = y++, Width = Dim.Fill()
            };
            host.Add(scoreLabel);
            foreach (var d in health.Deductions.Take(4))
                L($"  -{d.Deduction}  {d.Reason}");
            y++;
            L($"  Log: {ctx.Log.LogFilePath}");
        });
    }
}

/// <summary>Network configuration screen: show config, validate, apply static/DHCP with preview.</summary>
public static class ScreenNetwork
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var sys = await TuiApp.GetCachedAsync("sys", TuiApp.CacheShort, () => ctx.SystemInfo.GetSystemInfoAsync()).ConfigureAwait(false);
        var primary = sys.PrimaryAdapter;
        var current = primary is null
            ? null
            : await TuiApp.GetCachedAsync($"ip:{primary.Name}", TuiApp.CacheShort, () => ctx.NetworkConfig.GetIpConfigAsync(primary.Name)).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var adapterLabel = new Label($"Adapter: {primary?.Name ?? "(none found)"}") { X = 1, Y = 0, Width = Dim.Fill() };
            host.Add(adapterLabel);

            var radioLabels = new[] { "DHCP (automatic)", "Static IPv4" };
            var mode = new RadioGroup(radioLabels.Select(NStack.ustring.Make).ToArray()) { X = 1, Y = 2, Width = 30 };
            if (current?.Mode == IpConfigMode.Static) mode.SelectedItem = 1;
            host.Add(mode);

            int y = 2;
            var ip = new TextField(current?.Address ?? "") { X = 51, Y = y - 1, Width = 20 };
            host.Add(ip);
            var prefix = new TextField($"/{current?.PrefixLength ?? 24}") { X = 72, Y = y - 1, Width = 6 };
            host.Add(prefix);
            var gw = new TextField(current?.Gateway ?? "") { X = 51, Y = y++, Width = 20 };
            host.Add(gw);
            var dns1 = new TextField(current?.PreferredDns ?? "") { X = 51, Y = y - 1, Width = 20 };
            host.Add(dns1);
            y++;
            var dns2 = new TextField(current?.AlternateDns ?? "") { X = 51, Y = y - 1, Width = 20 };
            host.Add(dns2);

            var info = new Label("Fields (top to bottom at right): IP, Subnet, Gateway, Preferred DNS, Alternate DNS")
            { X = 1, Y = Pos.Bottom(mode) + 1, Width = Dim.Fill() };
            host.Add(info);

            int by = host.Frame.Height - 5;
            var validate = new Button("Validate") { X = 1, Y = by };
            var preview = new Button("Preview") { X = 14, Y = by };
            var apply = new Button("Apply") { X = 26, Y = by };
            var dhcp = new Button("Switch to DHCP") { X = 36, Y = by };
            host.Add(validate, preview, apply, dhcp);

            var output = new TextView
            {
                X = 1, Y = by + 2, Width = Dim.Fill(), Height = 3, ReadOnly = true
            };
            host.Add(output);

            IpConfig BuildConfig()
            {
                var cfg = new IpConfig
                {
                    Mode = mode.SelectedItem == 0 ? IpConfigMode.Dhcp : IpConfigMode.Static,
                    Address = ip.Text?.ToString(),
                    Gateway = gw.Text?.ToString(),
                    PreferredDns = dns1.Text?.ToString(),
                    AlternateDns = dns2.Text?.ToString()
                };
                var pText = prefix.Text?.ToString()?.TrimStart('/');
                if (int.TryParse(pText, out var p)) cfg.PrefixLength = p;
                return cfg;
            }

            validate.Clicked += () =>
            {
                var cfg = BuildConfig();
                var result = cfg.Mode == IpConfigMode.Dhcp
                    ? "DHCP selected: nothing to validate."
                    : IpValidator.ValidateStatic(cfg).Render();
                output.Text = result;
            };

            preview.Clicked += () => TuiApp.Background(async () =>
            {
                var cfg = BuildConfig();
                var plan = await ctx.NetworkConfig.BuildApplyPlanAsync(primary?.Name ?? "Ethernet", cfg).ConfigureAwait(false);
                TuiApp.Ui(() => output.Text = plan.Render(dryRun: true));
            });

            apply.Clicked += () => TuiApp.Background(async () =>
            {
                var cfg = BuildConfig();
                var validation = cfg.Mode == IpConfigMode.Static ? IpValidator.ValidateStatic(cfg) : new ValidationResult();
                if (!validation.IsValid)
                {
                    TuiApp.Error("Configuration is invalid:\n\n" + validation.Render());
                    return;
                }
                var adapterName = primary?.Name ?? "Ethernet";
                var plan = await ctx.NetworkConfig.BuildApplyPlanAsync(adapterName, cfg).ConfigureAwait(false);
                var result = await TuiApp.ApplyWithConfirmationAsync(host, "Apply IPv4 configuration", plan,
                    op => ctx.NetworkConfig.ApplyIpConfigAsync(adapterName, cfg, op)).ConfigureAwait(false);
                if (result.Success) TuiApp.LoadScreenRefresh();
            });

            dhcp.Clicked += () => TuiApp.Background(async () =>
            {
                var adapterName = primary?.Name ?? "Ethernet";
                var cfg = new IpConfig { Mode = IpConfigMode.Dhcp };
                var plan = await ctx.NetworkConfig.BuildApplyPlanAsync(adapterName, cfg).ConfigureAwait(false);
                var result = await TuiApp.ApplyWithConfirmationAsync(host, "Switch to DHCP", plan,
                    op => ctx.NetworkConfig.ApplyIpConfigAsync(adapterName, cfg, op)).ConfigureAwait(false);
                if (result.Success) TuiApp.LoadScreenRefresh();
            });
        });
    }
}

/// <summary>IP planner screen driven by the editable profiles.</summary>
public static class ScreenPlanner
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        await Task.CompletedTask.ConfigureAwait(false);
        var profiles = ctx.Profiles.Profiles;

        TuiApp.InvokeRefresh(host, () =>
        {
            host.Add(new Label("LAN PROFILES (editable in profiles.json)") { X = 1, Y = 0, Width = Dim.Fill() });

            var list = new ListView
            {
                X = 1, Y = 1, Width = 40, Height = Math.Max(1, profiles.Count)
            };
            list.SetSource(profiles.Select(p => p.Name).ToList());
            host.Add(list);

            int y = 1;
            Label C(string t) { var l = new Label(t) { X = 44, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }
            C("NETWORK PLANNER");
            var net = new TextField("192.168.10.0/24") { X = 44, Y = y++, Width = 20 };
            var gw = new TextField("192.168.10.1") { X = 44, Y = y++, Width = 20 };
            var count = new TextField("18") { X = 44, Y = y++, Width = 6 };
            var start = new TextField("192.168.10.10") { X = 44, Y = y++, Width = 20 };
            var namePfx = new TextField("SCHOOL-PC") { X = 44, Y = y++, Width = 20 };
            host.Add(net, gw, count, start, namePfx);
            y++;

            var output = new TextView
            {
                X = 1, Y = y + 1, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true, Text = "Select a profile and press 'Load', or edit values and press 'Plan'."
            };
            host.Add(output);

            var load = new Button("Load profile") { X = 1, Y = y, Width = 16 };
            var planBtn = new Button("Plan") { X = 20, Y = y, Width = 10 };
            host.Add(load, planBtn);

            list.OpenSelectedItem += _ => Load();

            void Load()
            {
                var p = profiles[Math.Min(list.SelectedItem, profiles.Count - 1)];
                net.Text = p.NetworkCidr;
                gw.Text = p.Gateway;
                count.Text = p.PcCount.ToString();
                start.Text = p.StartIp;
                namePfx.Text = p.NamePrefix;
            }

            planBtn.Clicked += () =>
            {
                var p = new LanProfile
                {
                    NetworkCidr = net.Text?.ToString() ?? "",
                    Gateway = gw.Text?.ToString() ?? "",
                    StartIp = start.Text?.ToString() ?? "",
                    NamePrefix = namePfx.Text?.ToString() ?? "PC",
                    PcCount = int.TryParse(count.Text?.ToString(), out var c) ? c : 18
                };
                var plan = IpPlanner.Plan(p.NetworkCidr, p.PcCount, p.Gateway, p.ReservedIps, p.StartIp, p.NamePrefix, p.NamePadding);
                output.Text = (plan.Validation.Errors.Count > 0 ? plan.Validation.Render() + "\n\n" : "") + plan.Render();
            };
        });
    }
}
