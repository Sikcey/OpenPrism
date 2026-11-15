# Capture boundary

## Runtime architecture

Application traffic is classified by the pinned ProxiFyre 2.7.0 executable and the Windows Packet Filter driver before being passed to a local SOCKS endpoint. No system-wide TUN is started.

- WireGuard/WARP: a loopback sing-box SOCKS inbound forwards to its userspace WireGuard endpoint.
- ByeDPI: the capture engine forwards directly to ByeDPI's loopback proxy.
- Zapret: the capture engine forwards to the reserved-port IPv4 relay; winws sees only the confined egress filter.

Only WireGuard/WARP start sing-box. All methods use the same application capture policy. Native DNS stays direct; local-network bypass is enabled. No Windows proxy setting, DNS server setting or route is rewritten.

## Matching and admission

Includes use executable names or absolute folder paths with a trailing separator. Exclusions use absolute folder paths, preventing similarly prefixed sibling folders from matching. Exclusions take priority over active rules. Exclude mode uses an explicit catch-all plus the requested folder exclusions.

ProxiFyre's name exclusions use substring matching. OpenPrism therefore rejects name-based exclusions and protocol-specific exclusions rather than claiming an exact boundary. Destination-IP and individual executable-path rules are currently unsupported by this capture adapter. Linked application folders are rejected. Unsupported combinations fail before processes or network capture start; there is no TUN fallback.

DNS is shared by Windows and cannot reliably be attributed to its originating application. Keeping it native is deliberate. The application's own encrypted resolver, when present, remains the application's responsibility.

## Installation and lifecycle

The capture payload is downloaded from a pinned official release archive and every deployed file is hashed. A pre-existing Windows Packet Filter driver is a shared prerequisite and is not removed. The app adds two firewall rules restricted to its protected capture executable; only matching app-owned registrations are removed.

The service owns the capture and transport process tree. Startup waits for the capture host's ready indication. Stop/crash cleanup closes the job and driver handles. Component and payload inventories allow removal of recognized files while preserving changed or unknown files.

## Validation and remaining limits

Live checks covered all routing scopes and direct-over-include precedence. Native route/DNS snapshots matched before, during and after capture. Selected HTTPS requests succeeded across ByeDPI, Zapret and WARP/WireGuard.

This is not a leak-proof boundary or kill switch. Unresolved process ownership, existing flows, fragmented traffic, driver conflicts and IPv6/UDP details need further validation. ByeDPI and Zapret matching is IPv4-only; matched IPv6 is blocked for application fallback, while unmatched IPv6 remains native. Streaming playback has not been tested.

Primary references: [ProxiFyre configuration](https://github.com/wiresock/proxifyre/blob/v2.7.0/docs/configuration.md), [Windows Packet Filter](https://github.com/wiresock/ndisapi), [sing-box SOCKS inbound](https://sing-box.sagernet.org/configuration/inbound/socks/).
