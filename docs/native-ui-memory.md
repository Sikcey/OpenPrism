# Native UI memory measurements

Measured on this Windows/NVIDIA machine, 2026-11-15. MiB means 1,048,576 bytes.
These are process measurements, not a guarantee on another machine or profile.

| Isolated release UI | Working set MiB | Private bytes MiB |
| --- | ---: | ---: |
| Glow, egui 0.33, optimized release | 65.71 | 66.81 |
| Software, same UI/release, first sample | 22.27 | 9.43 |
| Software, final patched runtime/default | 22.01 | 8.79 |

All isolated samples used `--inspect-only --smoke-no-network`, with no fixture,
and a three-second startup interval. Glow additionally used `--glow` once
software became the default. Only the process created for each sample was
terminated. The user's running UI and service were left running. No working
set trimming, EmptyWorkingSet, or forced garbage collection was used.

The pre-existing UI showed approximately 145 MiB working set and 105 MiB private
bytes. It had a different lifetime and live state, so it is not the paired
comparison above. Offline flags exclude service calls, startup changes, and the
tray; real loaded profiles, logs, dialogs, and service replies can add memory.

The final software sample saves about 44 MiB working set and 58 MiB private
bytes versus the matched Glow sample. It does **not** meet a 10 MiB working-set
target. Private bytes below 10 MiB in this fixture are not a Task Manager or
total-system-memory guarantee.

## Implementation

The application retains its egui widget tree, upgraded from egui/eframe 0.31 to
0.33. The default runtime uses egui_software_backend 0.0.2 and softbuffer 0.4.8.
On Windows softbuffer presents a CPU bitmap through CreateDIBSection/BitBlt.
Raster primitive caching is disabled to avoid retaining rasterized copies.
This trades repaint CPU time for memory; idle rendering remains event-driven.
Release builds use size optimization, thin LTO, one codegen unit, and stripping.

`--glow` retains the hardware renderer. Screenshot capture (`--capture=...`)
selects Glow because upstream software screenshot commands are unsupported.
IME viewport commands also remain unsupported by this software runner.

The Glow sample loaded nvoglv64.dll, nvgpucomp64.dll, and nvspcap64.dll. The final
software sample loaded none of these NVIDIA modules. opengl32.dll remains a
static import through the linked Glow fallback; software mode creates no GL
context. Mapped image sizes (roughly 94 MiB nvgpucomp64 and 46.5 MiB nvoglv64)
are not resident/private attribution and must not be added to process memory.

The vendored runtime patch preserves OS close cancellation, hide/show, completed
viewport commands, and delayed/worker repaint dispatch. See
`vendor/egui_software_backend/WGTUN-PATCHES.md` and its retained licenses.

## Verification

* Release build and all three application unit tests pass.
* Native input changed the rendered page from Connection to Profiles, visually
  inspected through a local capture. Resizing remained responsive; OS close
  exited cleanly with code 0 in offline mode.
* The isolated `software_lifecycle` example passes OS close cancellation, hiding,
  worker-thread request_repaint, reopening, and explicit exit. This tests the
  same notification path used by tray handlers without touching the live tray.
* Two-second idle samples in that harness measured 0% of one CPU core both
  visible and hidden. The real offline UI also measured 0% over three seconds.
  Short samples are subject to CPU timer resolution; interaction is not free.

Build and regression commands:

```powershell
cargo build --manifest-path src/WGTun.Native/Cargo.toml --release
cargo test --manifest-path src/WGTun.Native/Cargo.toml
cargo run --manifest-path src/WGTun.Native/Cargo.toml --example software_lifecycle --release
```

Reproduce a paired process sample in PowerShell:

```powershell
$exe = (Resolve-Path src/WGTun.Native/target/release/wgtun-native.exe).Path
foreach ($renderer in @('software', 'glow')) {
    $flags = @('--inspect-only', '--smoke-no-network', "--$renderer")
    $owned = Start-Process $exe -ArgumentList $flags -PassThru
    try {
        Start-Sleep -Seconds 3
        $owned.Refresh()
        [pscustomobject]@{
            Renderer = $renderer
            WorkingMiB = $owned.WorkingSet64 / 1MB
            PrivateMiB = $owned.PrivateMemorySize64 / 1MB
            NvidiaModules = @($owned.Modules | Where-Object ModuleName -Match '^nv' |
                Select-Object -ExpandProperty ModuleName)
        } | ConvertTo-Json -Compress
    } finally {
        if (-not $owned.HasExited) { Stop-Process -Id $owned.Id }
        $owned.Dispose()
    }
}
```

For longer lifecycle idle sampling, set `WGTUN_LIFECYCLE_CLOSE_MS=3500` and
`WGTUN_LIFECYCLE_HIDDEN_MS=3500` when launching the example. These affect only
the regression example, not the shipped UI.

Primary sources:

* https://github.com/DGriffin91/egui_software_backend
* https://docs.rs/egui_software_backend/0.0.2/egui_software_backend/
* https://github.com/rust-windowing/softbuffer/blob/v0.4.8/src/backends/win32.rs
* https://github.com/emilk/egui/tree/0.33.3/crates/egui-winit
