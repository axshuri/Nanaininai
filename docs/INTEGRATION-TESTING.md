# Integration Testing Guide (on Windows)

Unit tests (105) cover all pure logic and run anywhere: `dotnet test`.
This document describes **manual integration testing on real Windows machines**.

Use the published executable:
`src/Nanaininai/bin/Release/net10.0-windows/win-x64/publish/lanagent.exe`

Copy it to a USB stick or UNC-accessible staging folder.

## Environment matrix

| Scenario | Machines | Notes |
|----------|----------|-------|
| A. Single PC | 1 (Windows 11 Pro) | basics, no server peer |
| B. Workgroup trio | 3 (PC-01 Win10 Pro, PC-02 Win11 Pro, PC-03 Win11 Home) | mixed OS versions |
| C. Full school LAN | 18 PCs + switch + router | SCHOOL-18-PC profile |

All machines: Ethernet to one switch, optional router at `192.168.10.1`,
workgroup `SCHOOL`, IPv4 range `192.168.10.10 – 192.168.10.27`.

## A. Single PC checklist

1. `lanagent` — TUI opens in **LIMITED** mode; choose *Continue in Read-Only Mode*.
   - [ ] Dashboard shows computer name, edition, adapter, IPv4, health score.
   - [ ] Every health deduction has a visible reason.
2. `lanagent diagnose` — all read-only tests produce `PASS`/`WARN` with explanations.
3. `lanagent network show` — matches `ipconfig /all`.
4. `lanagent network validate 192.168.10.10/24 192.168.10.1 192.168.10.1` — PASS.
5. `lanagent network validate 192.168.10.0/24 192.168.10.1` — FAIL (network address).
6. `lanagent network validate 192.168.10.10/24 192.168.20.1` — FAIL (gateway outside subnet).
7. `lanagent checklan` — report renders, no crash when alone on the LAN.
8. `lanagent firewall status` — shows three profiles and required-rule status.
9. `lanagent --dry-run` paths: `lanagent prepare --dry-run` prints planned changes and
   states **No changes were made.**

## B. Trio (PC-01/02/03) checklist

1. **Elevation flow**: run on PC-01, pick *Restart as Administrator*, accept UAC,
   verify title/status shows **ADMIN**.
2. **Static apply**: on PC-02 set DHCP-leased `192.168.10.12`:
   - Network screen → Static, `192.168.10.12/24`, GW `192.168.10.1`, DNS `192.168.10.1`.
   - [ ] Preview lists exactly: address, gateway, DNS changes.
   - [ ] After apply, `lanagent network show` matches; ping PC-01 works.
   - [ ] Rollback info logged (`lanagent logs`).
3. **Firewall**: disable *File and Printer Sharing (SMB-In)* on PC-03 via Windows settings.
   - [ ] `lanagent diagnose` on PC-01 flags it; from PC-03 the fix dialog previews and
     enables **only that rule**.
   - [ ] Profiles were never disabled.
4. **ICMP case**: block ICMP Echo inbound on PC-03.
   - [ ] `checklan` reports "reachable over TCP but did not answer ICMP".
   - [ ] Recommended fix enables the ICMPv4 Echo rule, not the firewall.
5. **Network profile**: set PC-02 profile to Public.
   - [ ] Diagnostic warns; fix offers Private **with confirmation**.
6. **Sharing**: create share `SchoolShare` on `D:\SchoolShare` (Read, Authenticated Users).
   - [ ] `lanagent shares list` shows share + NTFS permissions separately.
   - [ ] `lanagent share test \\PC-02\SchoolShare --write` from PC-01: read ✓, write ✗ with
     a permission reason.
   - [ ] Everyone was not granted Full Control.
7. **Drives**: map `Z:` → `\\PC-02\SchoolShare` on PC-01 (persistent).
   - [ ] `lanagent drive list` shows it; disconnect works.
   - [ ] Bad share name yields a human-readable error (path not found guidance).
8. **Naming**: rename PC-03 to `SCHOOL-PC-03`; verify warning about restart; name applies
   after reboot.
9. **Workgroup**: PC-03 in `WORKGROUP`; diagnostics/prepare flag the mismatch and
   *only* show instructions/confirmation — never auto-change.

## C. Full 18-PC school scenario

1. On the admin PC: `lanagent plan` → SCHOOL-18-PC plan lists `SCHOOL-PC-01 … SCHOOL-PC-18`
   on `.10 … .27`, gateway reserved, no duplicates.
2. For each PC i: apply static config from the plan (CLI or TUI), rename to
   `SCHOOL-PC-XX`, join workgroup `SCHOOL`, reboot.
3. From PC-01: `lanagent checklan`
   - [ ] 18 devices found, all ONLINE, gateway listed.
   - [ ] No duplicate-IP conflicts.
   - [ ] Score reflects intentional deviations with explicit deductions.
4. Create the `SchoolShare` on PC-01, map `Z:` on all others, test read+write.
5. Kill the switch uplink for one PC and re-run `checklan` — verify OFFLINE status and
   an issue line.
6. Verify log files (`C:\ProgramData\Nanaininai\logs\lanagent-YYYYMMDD.log`) contain
   structured entries — and **no password/token material anywhere**.

## Regression guards (must stay true)

- `netsh advfirewall set allprofiles state off` runs ONLY from the explicit
  "Turn OFF all profiles" master switch, always gated behind plan preview →
  explicit confirmation → admin elevation. No automatic code path ever turns
  the firewall off (fixes, prepare, dry-run, etc. only enable rules/profiles).
- No share can be created with Everyone + Full Control.
- Every change requires: validation → plan preview → explicit confirmation → apply.
- `--dry-run` never touches the system.
- Scans are restricted to the user-selected local subnet (hard-capped at /22).
