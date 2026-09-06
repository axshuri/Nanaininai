# Nanaininai

A portable lab-network setup tool for Windows and Linux.

You put a USB drive with one editor-configured file on each machine. When you run the tool, it:

- picks the right network adapter,
- checks that the next IP is free,
- assigns that machine a static IP and hostname,
- writes a safety marker on the local machine so it cannot be reconfigured by accident,
- advances the USB counter so the next computer gets the next IP and name.

It is meant for a simple school/lab LAN where each PC gets a predictable name and address from one USB stick.

---

## What you get

- One shared `config.json` that controls naming, IP range, gateway, DNS, and how many machines the stick can configure.
- One shared `state.json` that remembers the next free computer number.
- Windows and Linux executables built from the same logic.
- A clear terminal UI showing the planned config and a numbered step-by-step run.
- Logs on the USB drive and a per-machine marker on each configured PC.

## Project structure

```text
.
├── shared/                  Shared types and pure validation helpers
├── src/
│   ├── Nanaininai/          Windows console app
│   └── Linux/               Linux console app
├── tests/                   xUnit tests for shared logic
├── usb-sample/              Sample USB layout and config
├── release/                 Regenerated platform releases
└── scripts/
    └── build.sh             Build + publish + release layout
```

## Quick start

1. Clone or download the repository.
2. Run `scripts/build.sh`.
3. Use the files under `release/` as your deployment package.

On Linux:

```bash
bash scripts/build.sh
```

On Windows you can run the same script in Git Bash, WSL, or a compatible shell, or run the equivalent `dotnet publish` commands manually. The important outcome is the folder layout under `release/`.

## Configuring the USB stick

Edit `config.json` before you deploy. The sample file looks like this:

```json
{
  "computerNamePrefix": "LAB-PC",
  "computerNamePadding": 2,

  "network": {
    "controllerIp": null,
    "controllerPort": null,
    "subnetMask": "255.255.255.0",
    "clientStartIp": "192.168.50.101",
    "gateway": null,
    "dns": []
  },

  "deployment": {
    "maxComputers": 17,
    "confirmBeforeApply": true,
    "allowAutoRestart": false
  },

  "ui": {
    "showDetailedLogs": true
  }
}
```

The important fields:

- `computerNamePrefix` — base name for each machine, for example `LAB-PC`.
- `computerNamePadding` — how many digits the number uses, for example `2` gives `LAB-PC-01`.
- `clientStartIp` — IP for the first computer. Each later computer gets the next IP.
- `subnetMask` — must be a valid contiguous mask.
- `gateway` — optional.
- `dns` — optional list of DNS servers.
- `controllerIp` / `controllerPort` — optional post-setup connectivity check.
- `maxComputers` — the tool stops assigning after this number.
- `confirmBeforeApply` — when true, it shows the planned config and waits for confirmation.
- `allowAutoRestart` — when true on Windows, it restarts automatically after renaming.

`state.json` starts at `1`:

```json
{
  "nextComputerNumber": 1
}
```

Leave it as-is for a fresh USB stick. The tool updates it after each successful configuration.

## USB deployment layout

Copy the published binary and the two JSON files into a folder named `LabNetwork` at the root of the USB drive.

For Windows:

```text
USB:\
└── LabNetwork\
    ├── Nanaininai.exe
    ├── config.json
    └── state.json
```

For Linux:

```text
/media/<user>/USB/
└── LabNetwork/
    ├── nanaininai
    ├── config.json
    └── state.json
```

The sample release package already has this layout:

```text
release/
├── Linux/
│   └── LabNetwork/
│       ├── nanaininai
│       ├── config.json
│       └── state.json
└── Windows/
    └── LabNetwork/
        ├── Nanaininai.exe
        ├── config.json
        └── state.json
```

## Running on Windows

1. Copy `release/Windows/LabNetwork` onto the USB root as `LabNetwork`.
2. Edit `LabNetwork/config.json` if needed.
3. Make sure the USB contains `LabNetwork/Nanaininai.exe`, `config.json`, and `state.json`.
4. Right-click `Nanaininai.exe` and choose **Run as administrator**.
5. Follow the terminal prompt.

The tool will:

- find the USB config,
- check for administrator rights,
- load `config.json` and `state.json`,
- show the planned computer name and IP,
- ask for confirmation if configured to do so,
- configure the adapter,
- rename the computer,
- verify the IP,
- optionally test the controller,
- save a marker on the local machine,
- update the USB counter.

On Windows, a rename usually requires a restart to take full effect.

## Running on Linux

1. Copy `release/Linux/LabNetwork` onto the mounted USB as `LabNetwork`.
2. Edit `LabNetwork/config.json` if needed.
3. Make sure the USB contains `LabNetwork/nanaininai`, `config.json`, and `state.json`.
4. Run the tool as root:

```bash
sudo ./nanaininai
```

If your USB is mounted at `/media/arshi/USB`, the expected path would look like:

```bash
sudo /media/arshi/USB/LabNetwork/nanaininai
```

The Linux version uses `nmcli` if available and falls back to writing `/etc/hostname` when needed. It also prefers physical Ethernet interfaces before wireless ones.

On Linux, a new hostname usually applies to new login sessions, so a re-login or restart may be required.

## First-run walkthrough

1. Decide your naming and IP plan in `config.json`.
2. Start with `state.json` set to `1`.
3. Plug the USB into the first machine.
4. Run the tool with the required privileges.
5. Confirm the planned configuration.
6. Let it finish.
7. Move the USB to the next machine.
8. Repeat until all machines are configured.

Each successful run increments the USB counter, so the next machine gets the next name and IP automatically.

## What the tool does during setup

- Finds the USB config by looking next to the executable and then scanning removable drives.
- Validates the subnet mask and the computed computer name.
- Detects active physical network adapters and lets you choose one if multiple are found.
- Probes whether the next IP is already in use using ARP, ICMP, and TCP checks.
- Configures a static IPv4 address.
- Renames the computer or host.
- Verifies that the adapter now has the expected IP and mask.
- Optionally checks connectivity to a controller IP.
- Saves a local marker so the same machine is not reconfigured by accident.
- Updates the USB state so the next number is reserved for the next machine.

If a step fails, the tool does not increment the USB counter, so the same computer number is still available.

## Files created during setup

On the USB:

- `state.json` — updated after each successful configuration.
- `state.backup.json` — safety copy of the state file.
- `Logs/yyyy-MM-dd.log` — run log for that day.

On each configured machine:

- Windows: `C:\ProgramData\LabNetwork\setup.json`
- Linux: a marker under `/var/lib/labnetwork`, `/etc/labnetwork`, or another writable location chosen by the tool.

## Recovering a machine

If a machine was already configured and you truly need to redo it, the tool can clear its local marker and start over when you confirm that choice in the terminal.

If the USB state file is corrupted, the tool tries to recover from `state.backup.json`.

## Building from source

Requirements:

- .NET SDK that supports the project targets.
- On Windows, the Windows project targets `net10.0-windows`.
- On Linux, the Linux project targets `net10.0`.

To build both platform packages and regenerate the release layout:

```bash
bash scripts/build.sh
```

To build only one platform, use `dotnet publish` directly. For example:

```bash
dotnet publish src/Nanaininai/Nanaininai.csproj -c Release -r win-x64 -o out/windows
dotnet publish src/Linux/Nanaininai.linux.csproj -c Release -r linux-x64 -o out/linux
```

## Tests

The repository includes tests for the shared calculation and state-storage logic. Run them with:

```bash
dotnet test tests/Nanaininai.Tests/Nanaininai.Tests.csproj
```

## Limitations

- The tool assumes a simple LAN setup. It is not a replacement for full network management.
- The Windows version requires administrator rights.
- The Linux version requires root.
- Rename behavior differs by platform. Windows usually needs a restart. Linux usually needs a new session or reboot depending on the environment.
- Hostname length and format are constrained, because both platforms have naming rules.

## Troubleshooting

- If the tool cannot find the USB config, make sure `config.json` is inside `LabNetwork/` at the drive root.
- If it reports not elevated on Windows, run it as administrator.
- If it reports not root on Linux, use `sudo`.
- If no adapter is found, check that a physical network interface is up.
- If the IP conflict check fails, choose another starting IP or verify that the address is not already in use.
- If the naming looks wrong, check `computerNamePrefix` and `computerNamePadding` in `config.json`.

## License

This repository is provided as-is for the scenarios described above. If you plan to redistribute or embed it, check the current license file in the repository and follow its terms.
