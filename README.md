# Deploy Manager

A zero-touch Windows imaging and deployment system. PXE-boot machines, apply a Windows image, join the domain (or prepare for Autopilot), and install software — all managed from a web UI.

Replaces MDT/WDS with a lightweight, modern alternative that supports UEFI Secure Boot out of the box.

## Features

- **PXE boot imaging** — UEFI and BIOS via iPXE + TFTP, fully automatic
- **Secure Boot** — Microsoft-signed iPXE shim chain works with Secure Boot enabled, no firmware changes or key enrollment required
- **Pre-stage reboot** — re-image existing Windows machines over HTTP without PXE or TFTP; Secure Boot compatible
- **Domain join** — offline domain join (ODJ) with automatic OU placement by subnet
- **Autopilot / Intune** — workgroup mode boots to OOBE for Autopilot enrollment, with hardware hash collection and optional automatic Intune device registration via the Microsoft Graph API
- **Driver injection** — per-model driver packages applied during deployment
- **Software packages** — silent post-install with real-time per-app progress in the web UI
- **Multi-site** — subnet-to-OU mapping with per-site timezone and locale support
- **Live monitoring** — job status streams to the dashboard via SignalR in real time
- **Entra ID SSO** — sign in with Microsoft Entra ID (Azure AD); breakglass local login always available
- **Email notifications** — SMTP alerts on job completion or failure
- **BranchCache** — optional BITS/BranchCache integration for site-local software caching over WAN
- **Audit log** — append-only JSONL log of all deployment actions and authentication events
- **Encrypted secrets** — service account passwords, client secrets and WinPE credentials encrypted at rest (AES-256-GCM)
- **Self-signed or CA certificate** — HTTPS with CSR generation and live certificate replacement built in
- **Self-contained** — a single MSI installs everything: web app, TFTP server, tray monitor. No external database, no IIS, no .NET prerequisite.

## How it works

1. A machine PXE-boots (or pre-stage reboots) and loads WinPE via the iPXE Secure Boot chain
2. WinPE contacts the Deploy Manager API, registers the machine, and picks up its job
3. Windows is applied, drivers are injected, and the machine joins the domain or workgroup
4. Post-install runs: software is installed silently, results reported back in real time
5. For **domain join**: machine arrives at the Windows login screen, already on the domain
6. For **Autopilot**: hardware hash is collected and (optionally) registered with Intune automatically; machine boots to OOBE for Entra enrollment

## Requirements

**Server:**
- Windows Server 2019/2022 or Windows 10/11 Pro/Enterprise (64-bit)
- Static IP address
- Windows ADK + WinPE add-on (for building and patching the boot image)
- .NET 8 Runtime (bundled in the MSI — no separate install needed)

**Network:**
- DHCP server with PXE options configured:
  - Option 066 (Next Server) → Deploy Manager server IP
  - Option 067 (Boot File) → `ipxe-shim.efi` (UEFI with Secure Boot) or `undionly.kpxe` (legacy BIOS)

**Active Directory** (for domain join mode):
- Service account with Full Control delegated on target computer OUs
- OUs created for each site/location

**Intune** (for automatic Autopilot registration, optional):
- Entra ID app registration with `DeviceManagementServiceConfig.ReadWrite.All` application permission (admin consent granted)
- Tenant ID, Client ID and Client Secret configured in Deploy Manager Settings

## Installation

Download the latest MSI from [Releases](https://github.com/JesseKozeluh/DeployManager/releases) and run it. The installer handles service registration, TFTP setup, and firewall rules.

After installing:
1. Open `https://<server-ip>:8090` and complete the setup wizard
2. Install the Windows ADK and WinPE add-on on the server
3. Build the WinPE boot image (see the Setup Guide at `https://<server-ip>:8090/docs`)
4. Go to **Settings → Boot Image** and patch boot.wim to bake in your server URL
5. Configure DHCP options 066 and 067 on your target scope
6. Add a Windows WIM image under **Images**
7. Configure sites and subnets under **Settings → Sites**
8. Deploy your first machine from the **Deploy** page

For detailed step-by-step instructions, see the **Setup & Administration Guide** bundled with the application at `https://<server-ip>:8090/docs`.

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 8090 | TCP/HTTPS | Web UI and deployment API |
| 8080 | TCP/HTTP  | WIM image, boot file, and job file downloads |
| 69   | UDP       | TFTP (initial PXE chainload only — everything else moves to HTTP) |

## Security notes

- HTTPS is required. A self-signed certificate is generated on first start; for production, generate a CSR under **Settings → Certificate** and have it signed by your internal CA.
- Sensitive settings (service account password, WinPE password, Entra client secret, SMTP password) are encrypted at rest using AES-256-GCM. Back up the encryption key at `%ProgramData%\DeployManager\data\enc.key`.
- WinPE job API calls are protected by per-job tokens — a device must present the correct token (embedded in its job file) to update job status.
- An append-only audit log is maintained at `%ProgramData%\DeployManager\data\audit.jsonl`.
- Login attempts are rate-limited (10 per 15 minutes per IP address).

## License

This software is licensed under the [Business Source License 1.1](LICENSE).

- **Free** for internal, non-commercial use (evaluation, testing, personal labs, home use).
- **Commercial use** (MSPs, IT service providers reselling deployments, hosting as a service) requires a commercial license — contact jessekozeluh@gmail.com.
- On **July 6, 2030**, the code converts to the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) and becomes fully open source.

See [LICENSE](LICENSE) for the full terms.

## Support

This software is provided **as-is with no support, warranty, or service-level agreement**. The author is under no obligation to respond to bug reports, feature requests, or questions. Community discussion is welcome via [GitHub Issues](https://github.com/JesseKozeluh/DeployManager/issues), but no response or fix is guaranteed.
