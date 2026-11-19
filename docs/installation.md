# Installation, updates and removal

## Development package

Build with scripts/build-native.ps1. The GUI and backend must stay together. Core setup installs sing-box and the process-capture payload; the chosen ByeDPI or Zapret engine is optional and separate. The new selective packet ingress uses pinned WinDivert; ProxiFyre and the NDIS Windows Packet Filter prerequisite are no longer installed. UI, broker and WGTun.Tun.exe must be a matched build.

Open the standalone `OpenPrism-Installer.exe`. The wizard verifies the embedded package, prepares the existing WARP account or creates a free account after you accept the Cloudflare terms, and creates rules only for apps you select. Setup uses one administrator approval for the Windows service and all selected components. The default destination is the current user's `%LOCALAPPDATA%\OpenPrism`, and setup registers a Windows uninstall entry. To use an extracted package instead, open `OpenPrism-Setup.exe` directly.

## Unsigned preview releases

Download the standalone `OpenPrism-Installer.exe` from GitHub Releases, verify its SHA-256 against the release checksum, and open it directly. The installer contains the application and its matching backend; setup still downloads the pinned networking dependencies and requires one administrator approval. Windows may show an unknown-publisher warning. The embedded package is checked for integrity, but this is not Authenticode publisher verification. Automatic signed updates are not available in this build.

For an extracted package, open `OpenPrism-Setup.exe` directly. Manual setup checks package hashes without requiring a publisher signature.

## Signed releases

scripts/build-release.ps1 requires a real trusted code-signing certificate with a private key, its thumbprint and the expected publisher subject. It signs first-party executables/assemblies and installer scripts, verifies timestamped signatures, hashes the package and signs a detached CMS manifest. The expected publisher is embedded in the release build.

The signed-update action verifies publisher trust, manifest signature, safe paths, the complete paired payload and every file hash before running the installer. The installer stages the pair and retains the previous installation for rollback. Old directories may remain while the old GUI is still running; inspect and remove them once no longer needed.

No certificate is bundled, and no signed release was produced during this work. Development builds have no configured publisher and refuse signed-update execution. Private GitHub releases also need appropriate access; the manual signed-package path does not embed a GitHub token.

## Removal

Optional engines have separate controls. WGTun, sing-box and capture are core components and have no separate removal buttons. Bulk removal stops the owned process tree, unregisters the broker, removes matching owned firewall/WinDivert registrations and recognized files. It does not remove the shared Windows Packet Filter driver or reset system networking.

Open `OpenPrism-Uninstall.exe`, select **Kaldır…** in setup, or uninstall OpenPrism from **Settings → Apps → Installed apps** after installing this package. The native removal wizard displays the registered installation directory and asks for confirmation before removing the service, settings, local WARP credentials and verified application files. Unknown or modified files are preserved. No restore point is created.

## Validation

scripts/test-installation.py uses temporary fixtures and invokes the real install/uninstall scripts without touching services, drivers or uninstall registration. Live optional removal/reinstallation and bulk removal/reinstallation were tested through the approved maintenance runner.
