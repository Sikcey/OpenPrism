# Strategy sources

The Windows split/disorder and disorder/fake ByeDPI profiles are based on the
Windows examples in [hufrea/byedpi](https://github.com/hufrea/byedpi#readme),
[MIT license](https://github.com/hufrea/byedpi/blob/main/LICENSE). The other
ByeDPI profiles are local combinations of documented ByeDPI options. These are
candidates to test, not guarantees for any ISP or destination.

[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube/blob/main/general.bat)
publishes Windows Zapret configurations; its [license](https://github.com/Flowseal/zapret-discord-youtube/blob/main/LICENSE.txt)
is MIT. Its configurations use external payloads and host lists and cannot be
treated as interchangeable with WGTun's confined relay configuration.

## Turkish carrier candidates (2026-11-15)

- ByeDPI: [byedpi-turkey profiles](https://github.com/elrondforwin/byedpi-turkey/tree/128640323db2e0fc2345f4f25eec32a1435c3632/profiles), MIT, copyright maximilionus. The source reports T?rk Telekom and Superonline outcomes on Linux; these do not establish Windows success. T?rk Telekom's unsupported Windows md5sig option is omitted. License: dependencies/notices/byedpi-turkey-MIT.txt.
- Zapret: [SplitWire-Turkey legacy winws presets](https://github.com/cagritaskn/SplitWire-Turkey/blob/766422ed8ca9cb5360e8d9d540e80722a0508efc/src/SplitWireTurkey/Resources/zapret/zapret-winws/presets.txt), MIT, copyright ?a?r? Ta?k?n. Includes T?rk Telekom, Kablonet, Superonline, Turkcell and Vodafone hotspot candidates. Only TCP desync options were adapted; global wf capture and STUN ranges were removed. UDP 443 retains OpenPrism's existing fake candidate. This is not zapret2/Lua support. License: dependencies/notices/SplitWire-Turkey-MIT.txt.

No carrier success was independently measured. Run GoodCheck on the current network; it tests certificate-validated HTTPS TCP, not UDP/QUIC.
