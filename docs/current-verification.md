# Verification — OpenPrism 0.3.0 selective TUN

This is a new backend. The earlier 0.2.0 ProxiFyre validation is historical.

## Current checks

- Go packet-engine tests cover IPv4/IPv6 selection, excluded/unknown ownership,
  real sing-tun UDP payload/reply handling, IPv6 extension parsing and malformed
  packets, fragmented HTTP replay, TLS ClientHello recovery and bounded
  server-first fallback.
- The elevated production-engine self-test launches three real executables
  and completes TCP and UDP echo exchanges on this machine. Only the selected
  exe enters sing-tun; the ignored and direct-exempt exe stay direct.
- Closing the filter restores direct traffic. Hard-killing a separate capture
  worker also restores native traffic without executing its cleanup handler.
- The report checks DNS configuration and driver ownership/cleanup. Evidence
  is artifacts/steering-driver/result.json plus stdout.txt and stderr.txt.
- C# core, capture policy/configuration and dependency checks are part of the
  paired package build. Installer fixtures verify payload integrity, traversal
  rejection and preservation of unknown/modified files without installing.

## Limits

There is no assigned non-link-local IPv6 address on this host. Native global
IPv6 is skipped; IPv6 packet-stack tests pass. Zapret transport remains IPv4
only. No new real-ISP Discord/voice/streaming or custom WireGuard-provider
claim is made. Unknown ownership/unsupported packets are direct, not blocked.
This is an unsigned development build, not a certified privacy boundary.

Run scripts/test-steering.cmd for the current production-engine local test.
Build from source with scripts/prepare-update.ps1. UI, service and WGTun.Tun.exe
must come from the same build; the service checks the packet engine's baked-in
SHA-256 identity. Never mix the old UI/backend with the new packet engine.
