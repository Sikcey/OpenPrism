# Native UI verification

Run on an interactive Windows desktop after the Rust executable is built. From the repository root:

```powershell
.\scripts\test-native-ui.ps1 -Executable <absolute-path-to-rust-exe> -OutputDirectory .\artifacts\native-ui -Page connection
```

Repeat for each page name supported by the native executable, including onboarding, profiles, settings and the rule editor. The harness passes `--smoke-no-network --page=<page>`; confirm the executable implements both switches before interpreting results. Each run overwrites that page's PNG and JSON. It starts only the supplied executable, captures its window with PrintWindow, samples three seconds of idle CPU and working set, and terminates only its own process. No service installation or network action is performed by the harness; the executable must honor smoke mode. A blank PNG or unavailable module list is inconclusive. Initial and observed minimum sizes are physical window pixels and depend on DPI and window decorations; inspect at 100% scaling as well as normal scaling. The tiny resize is a practical observation, not a query of an application's declared minimum.

Inspect JSON for startup error, exit code, cleanup, bounds, idle CPU, working set and loaded module paths. Check the executable's PE imports/build dependencies and module snapshot for CLR, WPF, Windows Forms and WebView runtimes; a process name or one module snapshot cannot prove their absence. Compare builds at the same DPI and idle conditions. The harness does not validate interactive behavior or prove that no network traffic occurred.

## Visual and interaction acceptance

- Compare each screen against `mockups/wgtun.html`, applying the explicit requirements below where the prototype disagrees. At 100% DPI the initial window is compact, approximately 760 × 700, with a 140–162 px sidebar; confirm the minimum window keeps controls usable without clipping.
- Dark palette: background `#131619`, panels `#1b1f23`, text `#edf2f4`, secondary text `#a0abb2`, borders `#30373c`, accent `#20af92` and soft teal `#173b34`. Check subtle borders, rounded panels, spacing and readable Segoe UI typography.
- Connection has the centered power hero, status and active profile, traffic scope controls and compact two-column application cards. Verify selected, disabled, focus, hover and disconnected states without initiating a connection in smoke mode.
- Profiles is an accordion in the sidebar, with its children accessible and the active item clear; verify expand/collapse, profile options and their contextual controls.
- Rules are accessible from Connection, not a separate Rules sidebar item. The editor offers exactly IPv4 address, IPv6 address, IPv4 network/CIDR, IPv6 network/CIDR, process name, application path and folder path. Check name, type, value and enabled state; no direction, routing action, priority or protocol controls. Verify create, edit, delete and enable/disable in a disposable test configuration.
- No Karma, standalone Rules navigation, prototype labels, sample-only or forbidden strings, or decorative count/status badges. Check all pages and dialogs, including empty and error states.
- First onboarding scan actually discovers service/dependency readiness and installed browser/Discord apps, displays results and offers install buttons for missing components. It must not silently install apps or start registration, setup or a tunnel. Exercise scan and missing states with controlled fixtures or a disposable VM.
- Separately, in a disposable VM and with explicit consent, verify one-time service setup and any required UAC prompt. After setup, verify background Windows startup resumes the last validated profile and subsequent normal startup/connection does not prompt for UAC. These live service and tunnel checks are outside this smoke harness; do not run them against a user's existing installation.

The current HTML prototype contains a Rules navigation item, rule protocol/action controls and a method-count badge; the explicit acceptance requirements above take precedence over those prototype details.
