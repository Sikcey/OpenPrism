# Strategy and scan contract

The presets command returns both engines with id, engine, name, arguments, source and notes. Arguments are fixed by the broker; callers select an ID and cannot supply command lines.

ByeDPI uses settings.dpi; Zapret uses settings.zapretDpi. Saving one preserves the other. Legacy Zapret settings using the ByeDPI default ID resolve to the original Zapret candidate.

goodcheck accepts hosts (1–8 public hostnames) and engine (byedpi or zapret). It explores every fixed candidate and confirms the best four. Results contain presetId, name, engine, successes, total, message and hosts. Each host result identifies host, stage, success, statusCode, elapsedMs and outcome. Results are bounded, partial results survive cancellation, and goodcheck-cancel stops the active scan.

The scan measures certificate-validated HTTPS TCP through a scan-owned local proxy/relay. It does not activate application capture, register a tunnel, disable certificate verification or globally kill services/processes. It does not prove UDP/QUIC, streaming or ISP-wide success.

Broker protocol 3 and the core-v3, goodcheck-v2, zapret-profiles and capture-native-v1 capabilities prevent silent use of an older service.
