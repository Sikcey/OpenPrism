# Verification — OpenPrism 0.2.0

Verified on Windows x64 on 2026-11-15. Privileged maintenance used the user-launched named-pipe runner; no additional elevation prompt was requested.

## Live capture

- Selected HTTPS requests succeeded with ByeDPI, Zapret and an existing WARP/WireGuard profile.
- Include mode, exclude mode, all-traffic mode with exclusions, and an overlapping direct exclusion passed TCP-path checks using an owned probe.
- The selected probe created an outbound connection through the transport; the same probe with a direct exclusion did not.
- Native routes and DNS server settings were unchanged during capture and after disconnect.
- The shared Windows Packet Filter driver remained installed and running. Its existing driver signature was valid.
- Streaming playback has not been tested. Full IPv6, UDP/QUIC and custom-provider interoperability need broader packet-level validation.

Raw reports remain local under artifacts/admin-runner and are not committed.

## GoodCheck

Full HTTPS scans completed for both engines against example.com: 15 ByeDPI candidates and 7 Zapret candidates. Per-host results were present. The best four candidates were retested. Cancellation retained completed results. A successful check is a measurement for that target and network, not carrier-wide proof.

## Recovery

The service detected and recovered from a killed capture child and a simulated network-rebind request. It then stopped the owned process tree. Isolated checks cover bounded retry backoff and reset. Real suspend/resume, physical adapter switching and reboot recovery have not been exhaustively exercised.

## Installation and cleanup

- Optional engine removal/reinstallation was exercised for ByeDPI and Zapret.
- Bulk service removal removed the service and its two owned firewall rules, preserving the shared packet-filter driver.
- Fresh installation after bulk removal succeeded. Known legacy scan files and capture debug output are cleaned.
- The Windows installer/uninstaller was tested against temporary fixtures: tampering and traversal were rejected; foreign and modified files were preserved.
- Real Windows Settings application removal was not invoked against the user's live installation/data. The underlying service cleanup was tested separately.

## Local checks

Core, dependency archive, capture-boundary/configuration and Rust checks pass. Generated configurations are checked by the pinned sing-box binary, including the loopback WireGuard SOCKS configuration. Installer fixtures invoke the actual scripts without service or registry changes.

The main-menu screenshot was captured with foreground focus preserved; the earlier DPI-related clipping was fixed. The executable has the embedded prism resource.

## Signing

No publisher certificate with a private key was available. No signed release is claimed. Release tooling signs the paired app/service and scripts, hashes every package file and signs the detached manifest. Development binaries reject signed-update execution without configured publisher trust.
