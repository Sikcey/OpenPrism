# Selective packet boundary — 0.3.0

The broker starts WGTun.Tun, which embeds pinned sing-box 1.14.4 and sing-tun.
ProxiFyre and the Windows Packet Filter prerequisite have been removed from
installation and runtime. There is no default-route Windows TUN adapter.

WinDivert captures outbound TCP/UDP. The engine resolves the real socket owner
using the Windows endpoint tables and QueryFullProcessImageName. TCP lookup
matches the complete tuple. Ambiguous UDP shared bindings, inaccessible owners,
pre-existing TCP connections without a captured SYN, unsupported fragments,
link-local/multicast and native DNS remain direct. This is fail-open routing,
not a kill switch or an atomic kernel per-process enforcement boundary.

WGTun's existing rule compiler supplies ordered rules to sing-box's matcher.
Exact Windows paths/names are adapted to anchored case-insensitive regexes.
Direct exceptions win. Only positive selections enter the in-process sing-tun
gVisor stack. Excluded packets are reinjected without proxy/TUN processing;
they can still incur the WinDivert queue overhead. Their DNS is not rewritten.

Selected flows use sing-box's actual router and WireGuard/SOCKS outbound.
ByeDPI and Zapret remain optional engine processes. Zapret additionally uses
its confined IPv4 leased-port relay; its IPv6 flows remain native. Selected
TCP can recover a readable TLS SNI/HTTP Host with a bounded 750 ms, 64 KiB
inspection and exact byte replay, then resolve through the configured DoH
transport. This does not encrypt or attribute the original Windows DNS query.
ECH, absent SNI and server-first traffic retain the original destination.

The broker owns the process job. The packet stack and transport start before
interception readiness. A five-second broker heartbeat and explicit stop file
release interception; hard process termination closes WinDivert handles in
Windows. Disconnect closes capture before engines. Existing proxied TCP
connections must reconnect. Retries remain bounded and explicit disconnect
cancels recovery. Flow memory is bounded and expired UDP flows are collected.
Reply injection rechecks the original endpoint owner before delivery.

Verified: real Windows TCP/UDP round trips through the production engine,
three separate selected/ignored/direct-exempt executables, direct traffic
after disconnect and after hard-killing a capture worker. Test-created driver
registrations were removed and DNS configuration was preserved. IPv4 and IPv6
packet ingress/return and excluded/unknown ownership are tested in the actual
sing-tun stack. The host lacks a non-link-local IPv6 address, so native global
IPv6 was skipped. Real ISP/Discord/QUIC and provider interoperability still
require user testing; earlier ProxiFyre results do not validate this backend.
