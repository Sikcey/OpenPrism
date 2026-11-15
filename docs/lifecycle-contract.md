# Lifecycle contract

Broker protocol 3 uses core-v3 and explicit feature capabilities. Incompatible installed services fail before connection or scan operations.

The GUI exposes install-component/remove-component for byedpi and zapret; core removal is only remove-service. install-service can bootstrap core with one selected optional engine. Setup downloads and hashes assets before elevation. The maintenance runner is a user-started, SID-restricted named pipe accepting only fixed operations; it does not accept arbitrary shell commands.

SCM start/stop pending states are retried. Protected application/component inventories identify owned payloads. Cleanup verifies hashes, rejects reparse paths, removes only matching app-owned firewall/driver registrations, and retains foreign or modified files. The shared ndisrd driver remains installed.

diagnostics-export writes an allowlisted JSON report without private keys, target hosts, application paths or raw logs. Crash events store fixed names and exception types. Detailed raw engine logs are separate and can contain connection metadata.

verify-recovery runs an isolated application-rule probe, kills only its own capture child, checks recovery and a simulated rebind, then disconnects and restores the prior saved startup profile. It does not toggle real adapters or suspend the computer.

IPv6 transport support is preliminary: no all-network claim is made. ByeDPI/Zapret are explicitly IPv4-only for matched traffic. WireGuard needs additional dual-stack and IPv6-only network testing.
