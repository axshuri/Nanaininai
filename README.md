# Nanaininai

**lanagent.exe** — a terminal-based Windows tool that takes an unconfigured PC
and turns it into a validated LAN node with working networking, SMB sharing,
and optional mapped drives.

It is not a pile of scripts glued together. It is a modular networking engine
with a TUI and a scriptable CLI on top.

```text
Unconfigured Windows PC  →  Working LAN PC
                            connectivity · SMB sharing · mapped drives
```

## Pipeline

```text
SCAN → DIAGNOSE → PLAN → CONFIRM → APPLY → VERIFY → REPORT
```

## What it does

| Stage | What you get |
|-------|--------------|
| Dashboard | Live system, network, and LAN-health summary |
| Network | IPv4 config for DHCP or static, CIDR and mask notation, validation, preview-before-apply, rollback snapshot |
| Planner | Duplicate-free IP plans from editable LAN profiles, including the bundled `SCHOOL-18-PC` profile: 18 PCs from `192.168.10.10` to `192.168.10.27` |
| Discovery | Ping sweep of the selected local subnet only, with hostname, MAC, and SMB enrichment |
| Diagnostics | 11-test battery — adapter, link, IPv4, subnet, gateway, DNS, pings, firewall, SMB, sharing, profile — with explanations and one-click fixes |
| Check LAN | One action that runs discovery, SMB checks, conflict detection, and a health score |
| Sharing | SMB status, share list with share-vs-NTFS permissions shown separately, create/remove shares (never Everyone-Full), remote read/write probes |
| Firewall | Profile status plus the required rules: ICMP Echo, SMB-In, Network Discovery. It enables specific rules only, never disables profiles |
| Drives | Map and disconnect network drives through native `WNetAddConnection2`, with credentials never passed on a command line |
| Naming | Computer rename with Windows naming rules, plus workgroup info. Workgroup is never changed automatically |
| Logs | Structured JSONL logging with level filtering, credential material scrubbed |

## First launch

```bash
dotnet publish src/Nanaininai -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The published self-contained executable lands in:

```text
src/Nanaininai/bin/Release/net10.0-windows/win-x64/publish/lanagent.exe
```

No .NET runtime needs to be installed on the target PCs.

Run it:

```text
lanagent            # interactive TUI on Windows 10/11
```

If you start without elevation you see this:

```text
Administrator privileges are required for configuration changes.
Current mode: LIMITED (read-only diagnostics available)
[Restart as Administrator] [Continue in Read-Only Mode] [Cancel]
```

Elevation is never silent. Diagnostics still work in limited mode.

## Example: prepare one PC

A typical prepare run goes through the pipeline in order:

1. `lanagent diagnose` — see what is healthy and what is not.
2. `lanagent network validate 192.168.10.10/24 192.168.10.1 192.168.10.1` — check the proposed static config before touching anything.
3. `lanagent prepare --dry-run` — preview the planned fixes.
4. `lanagent checklan` — verify the LAN view from this PC.
5. Apply the confirmed changes, then re-run diagnostics.

## CLI

```text
lanagent scan                   lanagent firewall status
lanagent diagnose               lanagent smb status
lanagent health                 lanagent shares list
lanagent prepare [--dry-run]   lanagent share test \\host\share [--write]
lanagent checklan [cidr]       lanagent drive list
lanagent network show          lanagent logs
lanagent network validate 192.168.10.10/24 192.168.10.1 192.168.10.1
lanagent network apply ...     [--dry-run]
lanagent plan                  # SCHOOL-18-PC allocation
```

## Architecture

```text
Nanaininai.Core/          # pure .NET, fully unit-tested
  Network/                # IpMath, IpValidator, IpPlanner, StrictInput
  Diagnostics/            # HealthScorer
  Models/                 # domain models
  Abstractions/           # service interfaces
  Logging/, Configuration/# JSONL logger, editable LAN profiles

Nanaininai/
  Platform/               # Windows implementations: netsh, WMI, PowerShell,
                          # ServiceController, native WNetAddConnection2
  Services/               # diagnostic engine, fixes, Check-LAN, Prepare-PC
  TUI/                    # Terminal.Gui screens and dialogs
  CliHost.cs              # scriptable CLI

tests/Nanaininai.Tests/   # 105 unit tests
docs/INTEGRATION-TESTING.md
```

The TUI has zero networking logic. All real work is delegated to the Core and
Services layer.

## Safety model

- Least privilege: read-only work is available without admin.
- Strict input whitelists before any external process call.
- No arbitrary shell commands.
- No remote-control features.
- SMB1 is never enabled.
- No credential storage or logging.
- Every change follows the same path: validate → plan preview → explicit confirmation → apply.
- `--dry-run` is available on every changing operation and never touches the system.
- Scans are limited to the user-selected local subnet, hard-capped at /22.

## Testing

Unit tests cover the pure logic and run anywhere:

```bash
dotnet test
```

For real Windows validation, see `docs/INTEGRATION-TESTING.md`. It covers the
single-PC path, the PC-01/02/03 trio, and the full 18-PC school scenario.

## Requirements

- .NET 10 SDK for builds.
- Windows 10/11 for the interactive TUI.
