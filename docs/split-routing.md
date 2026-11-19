# Split routing and application connectivity

The shared TUN configuration previously enabled strict routing and native DNS hijacking in every mode. On Windows, strict routing can block DNS outside the TUN even for applications routed to the direct outbound. Application exclusions therefore could not undo all system-wide effects.

Split modes now set `dns_mode=disabled` and `strict_route=false`. Port 53 traffic remains direct, and Windows keeps its native DNS configuration. This applies to WireGuard, WARP, ByeDPI and Zapret. The configured engine resolver remains available internally; split mode does not force that resolver on other applications. An application requiring encrypted DNS should configure it in the application. Full-traffic mode retains native DNS hijacking and strict routing.

Explicit direct rules precede overlapping active rules and DNS rules. Full-traffic mode now retains explicit exclusions instead of discarding them. Windows process attribution still has limitations; direct routing through a TUN is not equivalent to excluding a process from capture at the driver level.

Validation: isolated regression checks cover both split scopes across all four profile values. The pinned sing-box executable accepts all eight generated configurations. Stremio's local server returned HTTP 200 on IPv4 and IPv6 while disconnected. A live test with the updated installed service remains necessary; no service replacement, UAC prompt or network interruption was triggered during diagnosis.
