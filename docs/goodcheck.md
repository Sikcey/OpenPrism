# GoodCheck and ByeDPI

Initial setup keeps `byedpi-standard` (`--tlsrec 1+s`). GoodCheck runs only when requested in Profiles → ByeDPI → Configure. Selecting a method only previews it; Select commits the connection method. Applying a scan result updates ByeDPI settings without connecting or switching the method.

GoodCheckGoGo 0.8.5 is downloaded by service setup/repair from https://github.com/Storik4pro/GoodCheckGoGo/releases/tag/0.8.5. The upstream archive digest is pinned to `e140c49eee8e84e66d2188e51829d21ac8f4f4b466fed26a5412f49425ae7cc9`. Credits name Ori / Storik4pro. GoodCheck is not bundled in the app output.

The protected WGTun service runs the checker because upstream requires an elevated token. The scan uses a separate ephemeral loopback SOCKS listener, the pinned ByeDPI executable, Windows curl, 1–8 hostnames and two passes over ten fixed strategy candidates. It does not create a TUN interface. Global process/service cleanup and automatic connectivity/certificate fallback are disabled. A Windows job contains the scan's process tree. Cancel, service shutdown or the eight minute deadline terminate its children. Connect/resume and engine recovery cannot overlap a scan.

Results must cover the full generated list and expected host count. Ranking uses the worst score across passes. A zero-success strategy cannot be applied from results. Only known preset IDs cross into the elevated engine; arbitrary executable arguments remain rejected. These HTTPS tests do not establish that every destination or UDP application will work.

The interface supports Turkish, English, Russian and Simplified Chinese. Chinese rendering loads Microsoft YaHei from Windows when Chinese is selected. Technical tool logs retain their original diagnostic text.

Validation: strategy resolution, configuration generation, input validation, complete/incomplete result parsing, language persistence and translation tables have automated checks. The existing service upgrade was cancelled at Windows administrator approval during this session; a complete live scan through the upgraded service has not been verified. Use Services → Install / repair service in the new build before testing GoodCheck.
