# Installation, updates and removal

## Development package

Build with scripts/build-native.ps1. The GUI and backend must stay together. Core setup installs sing-box and the process-capture payload; the chosen ByeDPI or Zapret engine is optional and separate. A compatible official Windows Packet Filter driver, .NET Framework and Visual C++ runtime are prerequisites.

The registered installer is scripts/install-openprism.ps1. Unsigned local packages require its explicit DevelopmentPackage switch. The default destination is the current user's LocalAppData/OpenPrism. It registers a Windows uninstall entry.

## Signed releases

scripts/build-release.ps1 requires a real trusted code-signing certificate with a private key, its thumbprint and the expected publisher subject. It signs first-party executables/assemblies and installer scripts, verifies timestamped signatures, hashes the package and signs a detached CMS manifest. The expected publisher is embedded in the release build.

The signed-update action verifies publisher trust, manifest signature, safe paths, the complete paired payload and every file hash before running the installer. The installer stages the pair and retains the previous installation for rollback. Old directories may remain while the old GUI is still running; inspect and remove them once no longer needed.

No certificate is bundled, and no signed release was produced during this work. Development builds have no configured publisher and refuse signed-update execution. Private GitHub releases also need appropriate access; the manual signed-package path does not embed a GitHub token.

## Removal

Optional engines have separate controls. WGTun, sing-box and capture are core components and have no separate removal buttons. Bulk removal stops the owned process tree, unregisters the broker, removes matching owned firewall/WinDivert registrations and recognized files. It does not remove the shared Windows Packet Filter driver or reset system networking.

Windows Settings uninstall invokes the registered uninstaller. The service-host uninstall entry also removes recognized settings, DPAPI files and hash-verified caches. Unknown or modified files are preserved and may need inspection. No restore point is created.

## Validation

scripts/test-installation.py uses temporary fixtures and invokes the real install/uninstall scripts without touching services, drivers or uninstall registration. Live optional removal/reinstallation and bulk removal/reinstallation were tested through the approved maintenance runner.
