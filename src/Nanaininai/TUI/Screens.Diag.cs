using Nanaininai.Core.Models;
using Nanaininai.Services;
using Terminal.Gui;

namespace Nanaininai.TUI;

public static class ScreenDiagnostics
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var (results, health) = await TuiApp.GetCachedAsync("diag", TuiApp.CacheMedium, () => ctx.Diagnostics.RunAllWithHealthAsync()).ConfigureAwait(false);
        var fixer = new FixService(ctx);

        TuiApp.InvokeRefresh(host, () =>
        {
            var list = new ListView
            {
                X = 1, Y = 0, Width = Dim.Fill(1), Height = results.Count + 1
            };
            list.SetSource(results.Select(r => $"{r.Symbol,-4} {r.Title,-26} {r.Message}").ToList());
            host.Add(list);

            var output = new TextView
            {
                X = 1, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(), Height = Dim.Fill(4), ReadOnly = true
            };
            host.Add(output);

            int by = host.Frame.Height - 3;
            var details = new Button("Details") { X = 1, Y = by };
            var fix = new Button("Fix") { X = 13, Y = by };
            var healthBtn = new Button("Health") { X = 21, Y = by };
            host.Add(details, fix, healthBtn);

            list.SelectedItemChanged += _ =>
            {
                var r = results[Math.Min(list.SelectedItem, results.Count - 1)];
                output.Text = $"{r.Title}\n{new string('=', 40)}\nStatus: {r.Status}\n\n{r.Message}\n\n{r.Detail}\n\nRecommended: {r.RecommendedAction ?? "-"}";
            };

            details.Clicked += () =>
            {
                var r = results[Math.Min(list.SelectedItem, results.Count - 1)];
                TuiApp.Info(r.Title, $"{r.Message}\n\n{r.Detail}\n\nRecommended action:\n{r.RecommendedAction ?? "-"}");
            };

            fix.Clicked += () =>
            {
                var r = results[Math.Min(list.SelectedItem, results.Count - 1)];
                if (r.FixId is null)
                {
                    TuiApp.Info("Fix", "No automated fix for this finding.\n" + (r.RecommendedAction ?? "Apply the recommended action manually."));
                    return;
                }
                TuiApp.Background(async () =>
                {
                    var plan = await fixer.BuildPlanAsync(r.FixId).ConfigureAwait(false);
                    var result = await TuiApp.ApplyWithConfirmationAsync(host, "Apply fix", plan,
                        op => fixer.ApplyAsync(r.FixId!, op)).ConfigureAwait(false);
                    if (result.Success) TuiApp.LoadScreenRefresh();
                });
            };

            healthBtn.Clicked += () => TuiApp.Info("LAN HEALTH", health.Render());
        });
    }
}

public static class ScreenCheckLan
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var report = await TuiApp.GetCachedAsync("lanreport", TuiApp.CacheMedium, () => ctx.LanReport.CheckAsync(null, new Progress<string>())).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var text = new TextView
            {
                X = 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true,
                Text = report.Render()
            };
            host.Add(text);

            var save = new Button("Save report") { X = 1, Y = Pos.AnchorEnd(1), Width = 16 };
            host.Add(save);
            save.Clicked += () =>
            {
                try
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"lan-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                    File.WriteAllText(path, report.Render());
                    TuiApp.Info("Report saved", path);
                }
                catch (Exception ex) { TuiApp.Error("Could not save report: " + ex.Message); }
            };
        });
    }
}

public static class ScreenDiscovery
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;

        TuiApp.InvokeRefresh(host, () =>
        {
            host.Add(new Label("Network (CIDR):") { X = 1, Y = 0, Width = 16 });
            var net = new TextField("192.168.10.0/24") { X = 17, Y = 0, Width = 20 };
            host.Add(net);

            int by = 2;
            var scan = new Button("Scan") { X = 1, Y = by };
            var ping = new Button("Ping device") { X = 11, Y = by };
            host.Add(scan, ping);

            var output = new TextView
            {
                X = 1, Y = by + 2, Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true,
                Text = "Only the explicitly selected local subnet is scanned."
            };
            host.Add(output);

            scan.Clicked += () => TuiApp.Background(async () =>
            {
                var cidr = net.Text?.ToString() ?? "";
                TuiApp.Ui(() => output.Text = $"Scanning {cidr} ...");
                try
                {
                    var devices = await ctx.Discovery.ScanAsync(cidr, 1000, null).ConfigureAwait(false);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("LAN DEVICES");
                    sb.AppendLine("IP               HOSTNAME            STATUS   LATENCY  SMB  MAC");
                    foreach (var d in devices)
                        sb.AppendLine($"{d.Ip,-16} {(d.Hostname ?? "-"),-19} {(d.Online ? "ONLINE" : "OFFLINE"),-8} {(d.LatencyMs is null ? "-" : Math.Round(d.LatencyMs.Value) + "ms"),-8} {(d.SmbAvailable ? "yes" : "no")}  {d.Mac ?? "-"}");
                    sb.AppendLine();
                    sb.AppendLine($"{devices.Count} device(s).");
                    TuiApp.Ui(() => output.Text = sb.ToString());
                }
                catch (Exception ex)
                {
                    TuiApp.Ui(() => output.Text = "Scan failed: " + ex.Message);
                }
            });

            ping.Clicked += () => TuiApp.Background(async () =>
            {
                var target = net.Text?.ToString() ?? "";
                TuiApp.Ui(() => output.Text = $"Pinging {target} (4 packets) ...");
                var r = await ctx.Discovery.PingAsync(target, 4, 1000).ConfigureAwait(false);
                TuiApp.Ui(() =>
                    output.Text = $"PING {target}\nSent: {r.Total}  Replies: {r.Success}  Lost: {r.Lost}\nMin: {r.MinMs:0.0} ms   Avg: {r.AvgMs:0.0} ms   Max: {r.MaxMs:0.0} ms");
            });
        });
    }
}
