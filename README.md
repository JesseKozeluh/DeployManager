# Deploy Manager

A zero-touch Windows imaging and deployment system. PXE-boot machines, apply a Windows image, join the domain (or prepare for Autopilot), and install software — all managed from a web UI.

Replaces MDT/WDS with a lightweight, modern alternative that supports UEFI Secure Boot out of the box.

## Features

- **PXE boot imaging** — UEFI and BIOS via iPXE + TFTP, fully automatic
- **Pre-stage reboot** — re-image existing Windows machines over HTTP without PXE or TFTP; Secure Boot compatible
- **Domain join** — offline domain join (ODJ) with automatic OU placement by subnet
- **Workgroup / Autopilot** — skip domain join and boot to OOBE for Intune enrollment
- **Driver injection** — per-model driver packages applied during deployment
- **Software packages** — silent post-install with real-time progress in the web UI
- **Multi-site** — subnet-to-OU mapping with per-site timezone support
- **Audit log** — append-only JSONL log of all deployment actions
- **Encrypted secrets** — service account and WinPE passwords encrypted at rest (AES-256-GCM)
- **Self-signed or CA certificate** — HTTPS with CSR generation built in

## How it works

1. A machine PXE-boots (or pre-stage reboots) and loads WinPE
2. WinPE contacts the Deploy Manager API, registers the machine, and picks up its job
3. Windows is applied, the machine joins the domain or workgroup, and reboots
4. Post-install runs: software is installed silently, results reported back in real time
5. Machine arrives at the login screen (or OOBE for Autopilot), ready to go

## Requirements

**Server:**
- Windows Server 2019/2022 or Windows 10/11 Pro/Enterprise (64-bit)
- Static IP address
- .NET 8 Runtime (bundled in the MSI)

**Network:**
- DHCP server with PXE options configured:
  - Option 066 (Next Server) → Deploy Manager server IP
  - Option 067 (Boot File) → `Boot\BCD`

**Active Directory** (for domain join mode):
- Service account with Full Control delegated on target computer OUs
- OUs created for each site/location

## Installation

Download the latest MSI from [Releases](https://github.com/JesseKozeluh/DeployManager/releases) and run it. The installer handles service registration, TFTP setup, and firewall rules.

After installing:
1. Open `https://<server-ip>:8090` and complete the setup wizard
2. Go to **Settings → Boot Image** and rebuild boot.wim to bake in your server IP
3. Add a Windows WIM image under **Images**
4. Configure sites and subnets under **Settings → Sites**
5. Deploy your first machine from the **Deploy** page

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 8090 | TCP/HTTPS | Web UI and deployment API |
| 8080 | TCP/HTTP  | WIM image and boot file downloads |
| 69   | UDP       | TFTP (PXE boot files) |

## Security notes

- HTTPS is required. A self-signed certificate is generated on first start; for production, generate a CSR under **Settings → Certificate** and have it signed by your internal CA.
- Sensitive settings are encrypted at rest using AES-256-GCM. Back up the encryption key at `%ProgramData%\DeployManager\data\enc.key`.
- An append-only audit log is maintained at `%ProgramData%\DeployManager\data\audit.jsonl`.

## License

This software is licensed under the [Business Source License 1.1](LICENSE).

- **Free** for internal, non-commercial use (evaluation, testing, personal labs, home use).
- **Commercial use** (MSPs, IT service providers reselling deployments, hosting as a service) requires a commercial license — contact jessekozeluh@gmail.com.
- On **July 6, 2030**, the code converts to the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) and becomes fully open source.

See [LICENSE](LICENSE) for the full terms.

## Support

This project is provided as-is. Bug reports and feature requests are welcome via [GitHub Issues](https://github.com/JesseKozeluh/DeployManager/issues). There is no guaranteed response time or paid support at this time.
