using System.Net;
using System.Text.Json;
using Spectre.Console;
using Nanaininai;

// ============================================================
// Nanaininai — portable Windows lab automatic configuration
// ============================================================

int exitCode = 0;
UsbLogger? logger = null;

try
{
    exitCode = await RunAsync();
}
catch (Exception ex)
{
    Ui.Error(ex.Message);
    logger?.Log($"FATAL: {ex.Message}");
    exitCode = 1;
}
finally
{
    logger?.Dispose();
}
return exitCode;

async Task<int> RunAsync()
{
    Ui.Header("SCHOOL LAN NETWORK SETUP (WINDOWS)");

    // ---------- Locate USB ----------
    var usbDir = LocateUsbAppDir();
    if (usbDir is null)
    {
        Ui.Error("Could not find LabNetwork\\config.json on the USB drive.\n" +
                 "Expected layout: USB:\\LabNetwork\\Nanaininai.exe + config.json + state.json");
        return 1;
    }
    Ui.Ok($"USB configuration found: {usbDir}");

    logger = new UsbLogger(usbDir);
    logger.Log("START");

    // ---------- Administrator ----------
    if (!WindowsOps.IsAdministrator())
    {
        Ui.Error("Administrator privileges are required. Right-click Nanaininai.exe and choose 'Run as administrator'.");
        logger.Log("FAIL: not elevated");
        return 1;
    }
    Ui.Ok("Running as Administrator");

    // ---------- Load config ----------
    AppConfig config;
    try
    {
        var configPath = Path.Combine(usbDir, FileNames.Config);
        config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath))
                 ?? throw new InvalidOperationException("config.json is empty");
    }
    catch (Exception ex)
    {
        Ui.Error($"Cannot read config.json: {ex.Message}");
        logger.Log($"FAIL: config load: {ex.Message}");
        return 1;
    }
    if (!Core.IsValidSubnetMask(config.Network.SubnetMask))
    {
        Ui.Error($"Invalid subnet mask in config.json: '{config.Network.SubnetMask}'");
        return 1;
    }
    Ui.Ok("Configuration loaded");

    // ---------- State (with backup recovery) ----------
    var store = new UsbStateStore(usbDir);
    var state = store.Load();
    if (state is null)
    {
        state = new UsbState { NextComputerNumber = 1 };
        store.Save(state);
        Ui.Info("No state file found — starting at computer 1.");
    }
    logger.Log($"USB nextComputerNumber: {state.NextComputerNumber}");

    if (state.NextComputerNumber > config.Deployment.MaxComputers)
    {
        Ui.Panel("SETUP COMPLETE", new[]
        {
            $"All {config.Deployment.MaxComputers} computers have already been assigned.",
            "No additional computer can be configured using this USB.",
        });
        logger.Log("COMPLETE: max reached");
        return 0;
    }

    var number = state.NextComputerNumber;
    var computerName = Core.ComputeComputerName(config, number);
    var clientIp = Core.ComputeClientIp(config.Network.ClientStartIp, number);
    if (clientIp is null)
    {
        Ui.Error($"Cannot compute client IP from start '{config.Network.ClientStartIp}' for computer {number}.");
        return 1;
    }
    var ipString = clientIp.ToString();

    // ---------- Local marker (already configured?) ----------
    var marker = WindowsOps.ReadMarker();
    if (marker is { Configured: true })
    {
        Ui.Panel("ALREADY CONFIGURED", new[]
        {
            "This computer has already been configured.",
            "",
            $"Computer Number: {marker.ComputerNumber}",
            $"Computer Name:   {marker.ComputerName}",
            $"IP Address:      {marker.IpAddress}",
            "",
            "No changes were made.",
        });

        var actual = WindowsOps.CurrentComputerName;
        if (!string.Equals(actual, marker.ComputerName, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Warn($"Note: current hostname '{actual}' differs from marker '{marker.ComputerName}'. " +
                    "A previous run may have crashed before the rename took effect.");
        }

        if (Ui.Confirm("Reset and reconfigure this computer anyway?", false))
        {
            WindowsOps.DeleteMarker();
            logger.Log("Admin requested reconfigure — marker cleared");
        }
        else
        {
            logger.Log("EXIT: already configured");
            return 0;
        }
    }

    // ---------- Adapters ----------
    var adapters = WindowsOps.DetectAdapters();
    if (adapters.Count == 0)
    {
        Ui.Error("No active physical network adapter detected (Ethernet or Wi-Fi).");
        logger.Log("FAIL: no adapter");
        return 1;
    }
    var adapter = adapters.Count == 1 ? adapters[0] : Ui.SelectAdapter(adapters);
    if (adapter is null)
    {
        Ui.Error("No adapter selected.");
        return 1;
    }
    logger.Log($"Adapter: {adapter.Name} ({adapter.Description})");

    // ---------- IP conflict probe (ARP + ICMP + TCP) ----------
    Ui.Info($"Checking whether {ipString} is already in use…");
    if (await WindowsOps.IsIpOccupiedAsync(clientIp, TimeSpan.FromSeconds(2)))
    {
        Ui.Panel("IP CONFLICT", new[]
        {
            $"{ipString} appears to be in use.",
            "",
            $"Computer number: {number}",
            "",
            "Network configuration was NOT changed.",
        });
        logger.Log($"FAIL: IP conflict at {ipString}");
        return 1;
    }
    Ui.Ok($"{ipString} appears free");

    // ---------- Preflight summary ----------
    Ui.KeyValueTable("Planned Configuration", new[]
    {
        ("Computer Number", number.ToString("D" + Math.Max(2, config.ComputerNamePadding))),
        ("Computer Name", computerName),
        ("IP Address", ipString),
        ("Subnet Mask", config.Network.SubnetMask),
        ("Gateway", config.Network.Gateway ?? "None"),
        ("DNS", config.Network.Dns.Count > 0 ? string.Join(", ", config.Network.Dns) : "None"),
        ("Adapter", $"{adapter.Type} — {adapter.Description}"),
    });

    if (!Core.IsValidComputerName(computerName))
    {
        Ui.Error($"Computed computer name '{computerName}' violates Windows naming rules. " +
                 "Check computerNamePrefix in config.json (max 15 chars, letters/digits/hyphens).");
        return 1;
    }

    if (config.Deployment.ConfirmBeforeApply)
    {
        if (!Ui.Confirm("Apply this configuration?", true))
        {
            Ui.Warn("Cancelled. No changes were made. The USB counter was NOT consumed.");
            logger.Log("Cancelled by user");
            return 0;
        }
    }

    // ---------- Apply ----------
    const int totalSteps = 7;
    var step = 0;
    try
    {
        Ui.Step(++step, totalSteps, "Configuring IPv4…", () =>
        {
            var r = WindowsOps.ConfigureStaticIp(adapter.Name, ipString, config.Network.SubnetMask,
                config.Network.Gateway, config.Network.Dns);
            if (!r.Success) throw new InvalidOperationException(r.Message);
            logger?.Log("Static IP configured");
            return ipString;
        });

        var renameNeeded = !string.Equals(WindowsOps.CurrentComputerName, computerName, StringComparison.OrdinalIgnoreCase);
        Ui.Step(++step, totalSteps, "Renaming computer…", () =>
        {
            if (!renameNeeded) return "Already named correctly";
            var r = WindowsOps.RenameComputer(computerName);
            if (!r.Success) throw new InvalidOperationException(r.Message);
            logger?.Log($"Computer renamed: {computerName}");
            return computerName;
        });

        Ui.Step(++step, totalSteps, "Verifying network…", () =>
        {
            if (!WindowsOps.VerifyAdapterHasIp(adapter.Name, clientIp, config.Network.SubnetMask))
                throw new InvalidOperationException($"Adapter '{adapter.Name}' does not show expected IP {ipString} yet.");
            logger?.Log("Verification: IP/mask OK");
            return "IP + mask verified";
        });

        if (config.Network.ControllerIp is not null)
        {
            Ui.Step(++step, totalSteps, "Testing controller…", () =>
            {
                var r = WindowsOps.TestControllerAsync(
                    config.Network.ControllerIp, config.Network.ControllerPort, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                if (!r.Success) throw new InvalidOperationException(r.Message);
                logger?.Log($"Controller check: {r.Message}");
                return r.Message;
            });
        }
        else
        {
            Ui.Info("(no controllerIp configured — skipping connectivity test)");
        }

        Ui.Step(++step, totalSteps, "Saving local state…", () =>
        {
            WindowsOps.WriteMarker(new LocalSetupMarker
            {
                Configured = true,
                ComputerNumber = number,
                ComputerName = computerName,
                IpAddress = ipString,
            });
            logger?.Log($"Local marker written: {WindowsOps.MarkerPath}");
            return WindowsOps.MarkerPath;
        });

        Ui.Step(++step, totalSteps, "Updating USB counter…", () =>
        {
            store.Save(new UsbState { NextComputerNumber = number + 1 });
            logger?.Log($"USB state updated: {number + 1}");
            return $"next = {number + 1}";
        });
    }
    catch (Exception ex)
    {
        Ui.Error($"{ex.Message}\nUSB counter was NOT incremented — computer number {number} remains available.");
        logger?.Log($"FAIL at step {step}: {ex.Message}");
        return 1;
    }

    logger.Log("SUCCESS");

    var nextName = Core.ComputeComputerName(config, number + 1);
    var nextIp = Core.ComputeClientIp(config.Network.ClientStartIp, number + 1);
    var restartRequired = !string.Equals(WindowsOps.CurrentComputerName, computerName, StringComparison.OrdinalIgnoreCase);

    Ui.SuccessPanel(computerName, ipString, nextName, nextIp?.ToString() ?? "(n/a)");

    if (restartRequired)
    {
        Ui.Panel("RESTART REQUIRED", new[]
        {
            "Configuration completed successfully.",
            "",
            $"Computer: {computerName}",
            $"IP:       {ipString}",
            "",
            "Windows restart is required for the new name.",
            config.Deployment.AllowAutoRestart
                ? "Restarting now…"
                : "Please restart this computer.",
        }, borderColor: Color.Yellow);

        if (config.Deployment.AllowAutoRestart)
        {
            logger.Log("Auto-restart enabled — restarting");
            var psi = new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5 /c \"Nanaininai: restarting to apply computer name\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    Ui.Info("Press any key to exit…");
    Console.ReadKey(true);
    return 0;
}

static string? LocateUsbAppDir()
{
    var exeDir = AppContext.BaseDirectory;
    var candidate = Path.Combine(exeDir, FileNames.Config);
    if (File.Exists(candidate)) return exeDir.TrimEnd(Path.DirectorySeparatorChar);

    foreach (var d in DriveInfo.GetDrives())
    {
        if (!d.IsReady || d.DriveType is DriveType.NoRootDirectory or DriveType.CDRom) continue;
        candidate = Path.Combine(d.RootDirectory.FullName, FileNames.AppDir, FileNames.Config);
        if (File.Exists(candidate))
            return Path.Combine(d.RootDirectory.FullName, FileNames.AppDir);
    }
    return null;
}
