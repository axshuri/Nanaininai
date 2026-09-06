# USB Layout

Copy the published executable plus these two files into a folder named `LabNetwork`
and place it in the root of the USB drive (or mounted Linux drive).

### Windows USB

```
USB:\
└── LabNetwork\
    ├── Nanaininai.exe       ← published self-contained single-file exe (win-x64)
    ├── config.json          ← edit this before deployment
    └── state.json           ← leave as-is; nextComputerNumber starts at 1
```

### Linux USB / mounted drive

```
/media/<user>/USB/LabNetwork/
└── LabNetwork/
    ├── nanaininai           ← published self-contained single-file binary (linux-x64)
    ├── config.json          ← edit this before deployment
    └── state.json           ← leave as-is; nextComputerNumber starts at 1
```

After each successful PC configuration the tool creates:

- `state.json` (updated) and `state.backup.json` (safety copy)
- `Logs\yyyy-MM-dd.log` on the USB
- `C:\ProgramData\LabNetwork\setup.json` on each configured PC

## Editing config.json

- `computerNamePrefix` / `computerNamePadding` — computer name format
  (max hostname length is 15 chars, e.g. `LAB-PC-01`)
- `network.clientStartIp` — first client IP; each subsequent PC gets the next address
- `network.subnetMask` — must be a valid contiguous mask
- `network.gateway` / `network.dns` — optional; leave `null`/`[]` for an isolated LAN
- `network.controllerIp` / `controllerPort` — optional post-setup connectivity check
  (TCP port preferred over ICMP)
- `deployment.maxComputers` — tool refuses to assign beyond this number
- `deployment.confirmBeforeApply` — show planned config and require Enter (recommended)
- `deployment.allowAutoRestart` — auto-restart after rename (default false)
