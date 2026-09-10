# Nanaininai — Windows LAN Networking Automation Agent

Nanaininai (`lanagent.exe`) is a professional, terminal-based (TUI) Windows administration
tool that prepares and connects Windows PCs to a local LAN. It is **not** a collection of
shell scripts: it is a modular networking engine with a TUI on top.

Core workflow: **SCAN → DIAGNOSE → PLAN → CONFIRM → APPLY → VERIFY → REPORT**

```
Unconfigured Windows PC  →  Validated LAN PC with working connectivity,
                            SMB/file sharing, and optional mapped network drives.
```

## Features

| Area | What it does |
|------|--------------|
| Dashboard | Live system, network and LAN-health summary |
| Network | Full IPv4 config (DHCP / static), CIDR *and* mask notation, validation, preview-before-apply, rollback snapshot |
| Planner | Duplicate-free IP allocation plans (bundled editable `SCHOOL-18-PC` profile: 18 PCs, `192.168.10.10 – 192.168.10.27`) |
| Discovery | Ping-sweep of the *selected local subnet only*, hostname/MAC/SMB enrichment |
| Diagnostics | 11-test battery (adapter, link, IPv4, subnet, gateway, DNS, pings, firewall, SMB, sharing, profile) with explanations and one-click fixes |
| Check LAN | One-action full report: discovery + SMB + conflict detection + score |
| Sharing | SMB status, share list with share-vs-NTFS permission separation, create/remove shares (never Everyone-Full), remote-share testing (read/write probes) |
| Firewall | Profile status + required rules (ICMP Echo, SMB-In, Network Discovery). Enables *specific rules only* — never disables profiles |
| Drives | Map/disconnect network drives via native `WNetAddConnection2` (credentials never on a command line) |
| Naming | Computer rename with Windows naming rules; workgroup info (never changed automatically) |
| Logs | Structured JSONL logging with level filtering; credential material scrubbed |
| Safety | `--dry-run` on every changing operation, explicit confirmations, admin-only changes, human-readable errors |

## Build

Requirements: .NET 10 SDK.

```bash
dotnet build                          # solution (Core + app + tests)
dotnet test                           # 105 unit tests (run anywhere)
dotnet publish src/Nanaininai -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The published, self-contained executable lands in
`src/Nanaininai/bin/Release/net10.0-windows/win-x64/publish/lanagent.exe`
(no .NET runtime needs to be installed on target PCs).

## Run

```text
lanagent                 # interactive TUI (Windows 10/11)
```

On first launch without elevation you get:

```text
Administrator privileges are required for configuration changes.
Current mode: LIMITED (read-only diagnostics available)
[Restart as Administrator] [Continue in Read-Only Mode] [Cancel]
```

Elevation is never silent — diagnostics work in limited mode.

## CLI (scriptable)

```text
lanagent scan [--dry-run]           lanagent firewall status
lanagent diagnose                   lanagent smb status
lanagent health                     lanagent shares list
lanagent prepare [--dry-run]        lanagent share test \\host\share [--write]
lanagent checklan [cidr]            lanagent drive list
lanagent network show               lanagent logs
lanagent network validate 192.168.10.10/24 192.168.10.1 192.168.10.1
lanagent network apply ...          [--dry-run]
lanagent plan                       # SCHOOL-18-PC allocation
```

## Architecture

```text
Nanaininai.Core/          # pure .NET, fully unit-tested
  Network/                #   IpMath, IpValidator, IpPlanner, StrictInput
  Diagnostics/            #   HealthScorer
  Models/                 #   domain models
  Abstractions/           #   service interfaces (TUI has zero networking logic)
  Logging/, Configuration/#   JSONL logger, editable LAN profiles
Nanaininai/
  Platform/               # Windows implementations: netsh, WMI, PowerShell,
                          # ServiceController, native WNetAddConnection2
  Services/               # diagnostic engine, fixes, Check-LAN, Prepare-PC
  TUI/                    # Terminal.Gui screens, dialogs, navigation
  CliHost.cs              # scriptable CLI
tests/Nanaininai.Tests/   # 105 unit tests
docs/INTEGRATION-TESTING.md
```

Security model: least privilege (read-only without admin), strict input
whitelists before any external process call, no arbitrary shell commands,
no remote-control features, SMB1 never enabled, no credential storage or logging.

See `docs/INTEGRATION-TESTING.md` for the manual Windows test plan (single PC,
PC-01/02/03 trio, full 18-PC school scenario).
