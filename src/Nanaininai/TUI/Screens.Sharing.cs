using Nanaininai.Core.Network;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;
using Terminal.Gui;

namespace Nanaininai.TUI;

public static class ScreenSharing
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var smb = await TuiApp.GetCachedAsync("smb", TuiApp.CacheShort, () => ctx.Smb.GetStatusAsync()).ConfigureAwait(false);
        var shares = await TuiApp.GetCachedAsync("shares", TuiApp.CacheShort, () => ctx.Shares.ListSharesAsync()).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var y = 0;
            Label L(string t) { var l = new Label(t) { X = 1, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }

            L("SMB STATUS");
            L($"  SMB Client        {(smb.ClientRunning ? "ENABLED" : "DISABLED")}");
            L($"  SMB Server        {(smb.ServerRunning ? "ENABLED" : "DISABLED")}");
            L($"  Network Discovery {(smb.NetworkDiscoveryEnabled == true ? "ENABLED" : "DISABLED")}");
            if (smb.Smb1Supported) L("  [!] SMB1 is enabled (obsolete and insecure)");
            y++;

            L("SHARES");
            var list = new ListView
            {
                X = 1, Y = y, Width = Dim.Fill(1), Height = Math.Max(1, Math.Min(6, shares.Count))
            };
            list.SetSource(shares.Select(s => $"{s.Name,-20} {s.LocalPath}").ToList());
            host.Add(list);
            y += list.Frame.Height + 1;

            var output = new TextView
            {
                X = 1, Y = y, Width = Dim.Fill(), Height = 6, ReadOnly = true
            };
            host.Add(output);
            list.SelectedItemChanged += _ =>
            {
                var s = shares[Math.Min(list.SelectedItem, shares.Count - 1)];
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"SHARE: {s.Name}");
                sb.AppendLine($"LOCAL PATH: {s.LocalPath}");
                sb.AppendLine($"NETWORK PATH: {s.NetworkPath}");
                sb.AppendLine("SHARE PERMISSIONS:");
                foreach (var p in s.SharePermissions) sb.AppendLine($"   {p.Identity,-28} {p.Rights} ({p.Type})");
                sb.AppendLine("NTFS:");
                foreach (var p in s.NtfsPermissions) sb.AppendLine($"   {p.Identity,-28} {p.Rights} ({p.Type})");
                output.Text = sb.ToString();
            };

            int by = host.Frame.Height - 3;
            var enable = new Button("Enable file sharing") { X = 1, Y = by };
            var create = new Button("Create share") { X = 24, Y = by };
            var remove = new Button("Remove share") { X = 41, Y = by };
            var test = new Button("Test remote share") { X = 58, Y = by };
            host.Add(enable, create, remove, test);

            enable.Clicked += () => TuiApp.Background(async () =>
            {
                var plan = new OperationPlan
                {
                    Title = "Enable File and Printer Sharing",
                    Changes =
                    {
                        new PlannedChange { Index = 1, Category = "Services", Description = "Start the 'Server' (LanmanServer) service, startup type: Automatic" },
                        new PlannedChange { Index = 2, Category = "SMB", Description = "Enable SMB2 protocol (SMB1 stays disabled)" },
                    }
                };
                var r = await TuiApp.ApplyWithConfirmationAsync(host, "Enable file sharing", plan, op => ctx.Smb.EnableFileSharingAsync(op)).ConfigureAwait(false);
                if (r.Success) TuiApp.LoadScreenRefresh();
            });

            create.Clicked += () => CreateShareDialog();

            remove.Clicked += () =>
            {
                if (shares.Count == 0) return;
                var s = shares[Math.Min(list.SelectedItem, shares.Count - 1)];
                if (!TuiApp.Confirm("Remove share", $"Remove share '{s.Name}'?\n\nThe folder on disk is NOT deleted.\nConnected users will lose access."))
                    return;
                TuiApp.Background(async () =>
                {
                    var r = await ctx.Shares.RemoveShareAsync(s.Name, new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
                    TuiApp.Ui(() => { if (r.Success) TuiApp.LoadScreenRefresh(); else TuiApp.Error(r.Message + "\n\n" + r.Details); });
                });
            };

            test.Clicked += () => TestShareDialog();
        });

        void CreateShareDialog()
        {
            var dlg = new Dialog { Title = "CREATE NETWORK SHARE", Width = 60, Height = 16 };

            dlg.Add(new Label("Folder:") { X = 1, Y = 1 });
            var folder = new TextField("D:\\SchoolShare") { X = 12, Y = 1, Width = Dim.Fill(1) };
            dlg.Add(folder);
            dlg.Add(new Label("Share name:") { X = 1, Y = 3 });
            var name = new TextField("SchoolShare") { X = 12, Y = 3, Width = Dim.Fill(1) };
            dlg.Add(name);
            dlg.Add(new Label("Access:") { X = 1, Y = 5 });
            var access = new RadioGroup(new[] { "Read", "Change", "Full Control" }.Select(NStack.ustring.Make).ToArray()) { X = 12, Y = 5, Width = 30 };
            dlg.Add(access);
            dlg.Add(new Label("Note: NTFS permissions of the folder are not changed automatically.") { X = 1, Y = 9, Width = Dim.Fill() });

            var ok = new Button("Create") { IsDefault = true };
            var cancel = new Button("Cancel");
            ok.Clicked += () =>
            {
                var request = new CreateShareRequest
                {
                    LocalPath = folder.Text?.ToString() ?? "",
                    ShareName = name.Text?.ToString() ?? "",
                    AccessLevel = access.SelectedItem switch { 0 => "Read", 1 => "Change", _ => "Full" }
                };
                Application.RequestStop(dlg);
                TuiApp.Background(async () =>
                {
                    var plan = new OperationPlan
                    {
                        Title = $"Create share '{request.ShareName}' -> {request.LocalPath}",
                        Changes =
                        {
                            new PlannedChange { Index = 1, Category = "Share", Description = $"Create SMB share '{request.ShareName}' pointing to '{request.LocalPath}'" },
                            new PlannedChange { Index = 2, Category = "Permissions", Description = $"Grant {request.AccessLevel} share access to Authenticated Users; remove default Everyone grant" },
                        }
                    };
                    var r = await TuiApp.ApplyWithConfirmationAsync(host, "Create share", plan, op => ctx.Shares.CreateShareAsync(request, op)).ConfigureAwait(false);
                    if (r.Success) TuiApp.LoadScreenRefresh();
                });
            };
            cancel.Clicked += () => Application.RequestStop(dlg);
            dlg.AddButton(ok);
            dlg.AddButton(cancel);
            Application.Run(dlg);
        }

        void TestShareDialog()
        {
            var dlg = new Dialog { Title = "TEST REMOTE SHARE", Width = 60, Height = 10 };
            dlg.Add(new Label("Target:") { X = 1, Y = 1 });
            var target = new TextField("\\\\192.168.10.10\\SchoolShare") { X = 10, Y = 1, Width = Dim.Fill(1) };
            dlg.Add(target);
            var write = new CheckBox("Test write access") { X = 10, Y = 3 };
            dlg.Add(write);
            var ok = new Button("Test") { IsDefault = true };
            var cancel = new Button("Cancel");
            ok.Clicked += () =>
            {
                var t = target.Text?.ToString() ?? "";
                var tw = write.Checked;
                Application.RequestStop(dlg);
                TuiApp.Background(async () =>
                {
                    var r = await ctx.Shares.TestShareAsync(t, tw).ConfigureAwait(false);
                    TuiApp.Info("SMB TEST RESULT", r.Render());
                });
            };
            cancel.Clicked += () => Application.RequestStop(dlg);
            dlg.AddButton(ok);
            dlg.AddButton(cancel);
            Application.Run(dlg);
        }
    }
}

public static class ScreenFirewall
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var fw = await TuiApp.GetCachedAsync("fw", TuiApp.CacheShort, () => ctx.Firewall.GetStatusAsync()).ConfigureAwait(false);
        var required = await TuiApp.GetCachedAsync("fwRules", TuiApp.CacheShort, () => ctx.Firewall.GetRequiredRulesAsync()).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var y = 0;
            Label L(string t) { var l = new Label(t) { X = 1, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }

            L("WINDOWS FIREWALL");
            L($"  Domain Profile   {(fw.DomainEnabled ? "ENABLED" : "DISABLED")}");
            L($"  Private Profile  {(fw.PrivateEnabled ? "ENABLED" : "DISABLED")}");
            L($"  Public Profile   {(fw.PublicEnabled ? "ENABLED" : "DISABLED")}");
            y++;
            L("Required Rules:");
            y++;

            var items = required.ToList();
            var list = new ListView { X = 1, Y = y, Width = Dim.Fill(1), Height = items.Count + 1 };
            list.SetSource(items.Select(x => $"{(x.Rule?.Enabled == true ? "[ON ]" : "[OFF]")} {x.DisplayName,-34} {x.Rule?.Name ?? "not found"}").ToList());
            host.Add(list);
            y += items.Count + 2;

            var output = new Label("Master switch: turning all profiles OFF disables the Windows Firewall completely - use with care.")
            { X = 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill() };
            host.Add(output);

            var enable = new Button("Enable selected rule") { X = 1, Y = Pos.AnchorEnd(3), Width = 24 };
            var dry = new Button("Dry run") { X = 28, Y = Pos.AnchorEnd(3), Width = 12 };
            host.Add(enable, dry);

            var allOn = new Button("Turn ON all profiles") { X = 1, Y = Pos.AnchorEnd(1), Width = 22 };
            var allOff = new Button("Turn OFF all profiles") { X = 26, Y = Pos.AnchorEnd(1), Width = 24 };
            host.Add(allOn, allOff);

            void ToggleAllProfiles(bool turnOn)
            {
                if (!turnOn && !TuiApp.Confirm("Disable firewall",
                        "This turns OFF the Windows Firewall for ALL profiles (Domain, Private, Public).\n\nThe PC will be unprotected from network threats until it is turned back on.\n\nContinue?"))
                    return;

                var plan = new OperationPlan
                {
                    Title = turnOn ? "Enable all firewall profiles" : "Disable all firewall profiles",
                    Changes =
                    {
                        new PlannedChange
                        {
                            Index = 1,
                            Category = "Firewall",
                            Description = turnOn
                                ? "Set firewall state ON for all profiles (Domain, Private, Public)"
                                : "Set firewall state OFF for all profiles (Domain, Private, Public) - the Windows Firewall will be COMPLETELY DISABLED"
                        }
                    }
                };
                TuiApp.Background(async () =>
                {
                    var r = await TuiApp.ApplyWithConfirmationAsync(host,
                        turnOn ? "Turn firewall ON" : "Turn firewall OFF",
                        plan, op => ctx.Firewall.SetAllProfilesAsync(turnOn, op)).ConfigureAwait(false);
                    if (r.Success) TuiApp.LoadScreenRefresh();
                });
            }

            void EnableSelected(bool dryRun)
            {
                if (items.Count == 0) return;
                var (_, rule) = items[Math.Min(list.SelectedItem, items.Count - 1)];
                if (rule is null) { TuiApp.Error("The rule was not found on this system."); return; }
                TuiApp.Background(async () =>
                {
                    var confirmed = dryRun || TuiApp.Confirm("Firewall", $"Enable rule:\n\n{rule.Name}\n\nEnable?");
                    var r = await ctx.Firewall.EnableRuleAsync(rule.Name, new OperationContext { DryRun = dryRun, UserConfirmed = confirmed }).ConfigureAwait(false);
                    TuiApp.Ui(() =>
                    {
                        if (r.Success) TuiApp.LoadScreenRefresh();
                        else TuiApp.Error(r.Message + "\n\n" + r.Details);
                    });
                });
            }

            enable.Clicked += () => EnableSelected(false);
            dry.Clicked += () => EnableSelected(true);
            allOn.Clicked += () => ToggleAllProfiles(true);
            allOff.Clicked += () => ToggleAllProfiles(false);
        });
    }
}

public static class ScreenDrives
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var drives = await TuiApp.GetCachedAsync("drives", TuiApp.CacheShort, () => ctx.Drives.ListMappedDrivesAsync()).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var y = 0;
            Label L(string t) { var l = new Label(t) { X = 1, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }

            L("MAPPED NETWORK DRIVES");
            y++;
            var list = new ListView { X = 1, Y = y, Width = Dim.Fill(1), Height = Math.Max(1, Math.Min(8, drives.Count)) };
            list.SetSource(drives.Select(d => $"{d.Letter,-4} -> {d.RemotePath}").ToList());
            host.Add(list);
            y += list.Frame.Height + 1;

            var output = new TextView { X = 1, Y = y, Width = Dim.Fill(), Height = 4, ReadOnly = true };
            host.Add(output);

            int by = host.Frame.Height - 3;
            var map = new Button("Map drive") { X = 1, Y = by };
            var unmap = new Button("Disconnect selected") { X = 16, Y = by };
            host.Add(map, unmap);

            map.Clicked += () =>
            {
                var dlg = new Dialog { Title = "MAP NETWORK DRIVE", Width = 62, Height = 12 };
                dlg.Add(new Label("Drive letter:") { X = 1, Y = 1 });
                var letter = new TextField("Z:") { X = 15, Y = 1, Width = 6 };
                dlg.Add(letter);
                dlg.Add(new Label("Network path:") { X = 1, Y = 3 });
                var path = new TextField("\\\\192.168.10.10\\SchoolShare") { X = 15, Y = 3, Width = Dim.Fill(1) };
                dlg.Add(path);
                var persistent = new CheckBox("Reconnect at sign-in") { X = 15, Y = 5, Checked = true };
                dlg.Add(persistent);

                var ok = new Button("Map Drive") { IsDefault = true };
                var cancel = new Button("Cancel");
                ok.Clicked += () =>
                {
                    var l = letter.Text?.ToString() ?? "";
                    var p = path.Text?.ToString() ?? "";
                    var pers = persistent.Checked;
                    Application.RequestStop(dlg);
                    TuiApp.Background(async () =>
                    {
                        var plan = new OperationPlan
                        {
                            Title = "Map network drive",
                            Changes = { new PlannedChange { Index = 1, Category = "Drive", Description = $"Map {l.ToUpperInvariant()} to {p}{(pers ? " (persistent)" : "")}" } }
                        };
                        var r = await TuiApp.ApplyWithConfirmationAsync(host, "Map drive", plan, op => ctx.Drives.MapDriveAsync(l, p, pers, null, op)).ConfigureAwait(false);
                        if (r.Success) TuiApp.LoadScreenRefresh();
                    });
                };
                cancel.Clicked += () => Application.RequestStop(dlg);
                dlg.AddButton(ok);
                dlg.AddButton(cancel);
                Application.Run(dlg);
            };

            unmap.Clicked += () =>
            {
                if (drives.Count == 0) return;
                var d = drives[Math.Min(list.SelectedItem, drives.Count - 1)];
                if (!TuiApp.Confirm("Disconnect drive", $"Disconnect {d.Letter} (→ {d.RemotePath})?")) return;
                TuiApp.Background(async () =>
                {
                    var r = await ctx.Drives.UnmapDriveAsync(d.Letter, new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
                    TuiApp.Ui(() => { if (r.Success) TuiApp.LoadScreenRefresh(); else TuiApp.Error(r.Message); });
                });
            };
        });
    }
}

public static class ScreenNaming
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        var sys = await TuiApp.GetCachedAsync("sys", TuiApp.CacheShort, () => ctx.SystemInfo.GetSystemInfoAsync()).ConfigureAwait(false);
        var wg = await TuiApp.GetCachedAsync("wg", TuiApp.CacheMedium, () => ctx.SystemInfo.GetWorkgroupInfoAsync()).ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            var y = 0;
            Label L(string t) { var l = new Label(t) { X = 1, Y = y++, Width = Dim.Fill() }; host.Add(l); return l; }

            L("COMPUTER NAME & WORKGROUP");
            L($"  Current name     : {sys.ComputerName}");
            L($"  Workgroup/Domain : {wg.Workgroup}{(wg.IsDomainJoined ? " (domain)" : "")}");
            y++;

            L("Rename computer (a restart is required afterwards):");
            var newName = new TextField("") { X = 1, Y = y++, Width = 30 };
            host.Add(newName);
            var rename = new Button("Rename") { X = 1, Y = y++ };
            host.Add(rename);
            y++;

            L("Workgroup: changing the workgroup requires a system configuration change");
            L("and a restart. It is never changed automatically by Nanaininai.");

            rename.Clicked += () =>
            {
                var name = newName.Text?.ToString() ?? "";
                if (!StrictInput.IsValidComputerName(name))
                {
                    TuiApp.Error($"'{name}' is not a valid computer name.\n\nUp to 15 characters; letters, digits and hyphens; no spaces or dots.");
                    return;
                }
                if (!TuiApp.Confirm("Rename computer", $"Rename '{sys.ComputerName}' to '{name}'?\n\nA restart is required for the new name to take effect."))
                    return;
                TuiApp.Background(async () =>
                {
                    var r = await ctx.NetworkConfig.RenameComputerAsync(name, new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
                    TuiApp.Info("Rename", r.Message);
                });
            };
        });
    }
}

public static class ScreenLogs
{
    public static async Task Show(FrameView host)
    {
        var ctx = TuiApp._ctx;
        await Task.CompletedTask.ConfigureAwait(false);

        TuiApp.InvokeRefresh(host, () =>
        {
            host.Add(new Label("LOG VIEWER") { X = 1, Y = 0, Width = Dim.Fill() });

            var levels = new[] { "All", "INFO+", "WARNING+", "ERROR" };
            var filter = new RadioGroup(levels.Select(NStack.ustring.Make).ToArray()) { X = 1, Y = 1, Width = 40 };
            host.Add(filter);

            var output = new TextView
            {
                X = 1, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true, Text = ""
            };
            host.Add(output);

            void Refresh()
            {
                LogEventLevel? min = filter.SelectedItem switch
                {
                    1 => LogEventLevel.Info,
                    2 => LogEventLevel.Warn,
                    3 => LogEventLevel.Error,
                    _ => null
                };
                var entries = ctx.Log.GetEntries(min == LogEventLevel.Info ? null : min);
                if (filter.SelectedItem == 1)
                    entries = entries.Where(e => e.Level is LogEventLevel.Info or LogEventLevel.Success or LogEventLevel.Warn or LogEventLevel.Error).ToList();
                output.Text = string.Join(Environment.NewLine, entries.TakeLast(500).Select(e => e.Format()));
            }

            filter.SelectedItemChanged += _ => Refresh();
            Refresh();
            host.Add(new Label($"Log file: {ctx.Log.LogFilePath}") { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill() });
        });
    }
}
