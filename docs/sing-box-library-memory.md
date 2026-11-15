# sing-box v1.14.4: Windows embedding and GUI memory

Research: 2026-11-14. Scope: documentation only. Preserve the installed C# broker/service; the user's 10 MB target applies only to the GUI. No tunnel, process, service, configuration, dependency or source changes were made.

## Decision

Keep the existing service-owned CLI engine and IPC boundary. A Windows DLL is technically feasible as a custom integration, but the inspected upstream v1.14.4 build does not supply a supported Windows libbox C ABI/build target. Embedding cannot remove the Go runtime or full engine's live allocations. Loading it into the GUI would charge those allocations to the GUI and work against its memory target. Loading it into the C# service would retain both runtimes and offers no demonstrated memory saving; no service rewrite is recommended.

## Official source evidence

- [v1.14.4 release](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4) is the pinned release. Use tag-specific evidence rather than current/testing documentation. Its [go.mod](https://github.com/SagerNet/sing-box/blob/v1.14.4/go.mod#L1-L3) declares Go 1.25.5.
- [CLI build documentation](https://github.com/SagerNet/sing-box/blob/v1.14.4/docs/installation/build-from-source.md#L32-L42) builds `./cmd/sing-box` with optional tags. This is the conventional Windows executable path.
- [libbox builder](https://github.com/SagerNet/sing-box/blob/v1.14.4/cmd/internal/build_libbox/main.go#L24-L41) dispatches only Android and Apple targets. It uses gomobile binding of `experimental/libbox`: Android AAR and Apple framework outputs, rather than a Windows DLL/header package. This establishes the absence of a Windows target in that builder, not impossibility of downstream ports or a promise of stable ABI.
- [Core Go package](https://github.com/SagerNet/sing-box/blob/v1.14.4/box.go) exposes engine construction/lifecycle in Go. That is a usable basis for a custom Go integration, not a directly callable C#/Rust ABI. [Registry construction](https://github.com/SagerNet/sing-box/blob/v1.14.4/include/registry.go) supplies protocol and DNS registrations that an integration must preserve as needed.
- Go's [build modes](https://go.dev/src/cmd/go/internal/help/helpdoc.go) define `c-shared` as a main package and its imports with only cgo-exported functions callable. [Platform support](https://github.com/golang/go/blob/master/src/internal/platform/supported.go#L167-L175) includes Windows amd64. This is Go toolchain support, distinct from sing-box's supported packaging.
- A custom DLL therefore needs a new main-package C facade, cgo/C toolchain, opaque engine handles, explicit allocation/free rules, errors, callbacks, cancellation and shutdown. C# would use P/Invoke; Rust would use C FFI. Follow [cgo export and pointer rules](https://pkg.go.dev/cmd/cgo#hdr-C_references_to_Go). Simply renaming the executable, compiling the CLI as a DLL, or expecting gomobile bindings to become Windows exports does not provide a usable engine API. No DLL build was attempted.

## Memory implications

The [Go GC guide](https://go.dev/doc/gc-guide) explains that the standard runtime includes GC, that heap size trades memory against collection CPU, and that virtual address reservations are not physical RAM. A DLL still needs runtime metadata, scheduler/thread and goroutine stacks, heap and GC, plus routing/DNS state, connection buffers and protocol state. These remain in its host process. Packaging may eliminate some process/CLI overhead; its amount is unmeasured and cannot justify a 10 MB guarantee. Shared executable code pages do not share mutable per-process heaps or make engine allocations disappear.

`GOMEMLIMIT` is a soft Go-runtime limit, not a hard process working-set limit; it excludes memory outside runtime management. A very small setting can increase GC CPU or still be exceeded. `GOGC` similarly trades CPU for heap space. Neither is a supported way to promise a full engine or GUI below 10 MB. Stripping symbols/compressing executable files primarily changes distribution size, not the engine's live heap.

libbox [setup](https://github.com/SagerNet/sing-box/blob/v1.14.4/experimental/libbox/setup.go#L82-L105) has OOM/GC limit handling, including Apple-specific defaults. It is not evidence of a Windows RAM guarantee. The [mobile builder](https://github.com/SagerNet/sing-box/blob/v1.14.4/cmd/internal/build_libbox/main.go#L178-L216) also applies `with_low_memory` to non-macOS Apple targets; do not assume it is a validated Windows optimization.

## Read-only local observations

Sampled on 2026-11-14 around 23:52 Europe/Istanbul using `Get-CimInstance Win32_Service`, `Get-CimInstance Win32_Process`, and `Get-Process`. No service/engine was started, stopped, restarted or probed with a connection command.

- Installed `WGTunBroker`: Running, PID 15832, executable `C:\Program Files\WGTun\Broker\WGTun.Service.exe`. First sample: working set 41,603,072 bytes (39.68 MiB), private bytes 10,563,584 (10.07 MiB). Later sample: 42,004,480 (40.06 MiB) and 10,817,536 (10.32 MiB). Idle here means no observed engine child, not proof of zero broker activity.
- Existing GUI PID 18004, `artifacts\WGTun-fixed\WGTun.exe`: working set 150,810,624 bytes (143.82 MiB), private bytes 110,202,880 (105.10 MiB), unchanged at both samples. Visibility/minimized state and GPU allocations were not inspected. This is a process snapshot, not an allocation profile or peak-load benchmark.
- No `sing-box` process was present in either process enumeration. Active-engine memory is therefore **unmeasured**, not zero and not inferred from archive size. No tunnel was activated to obtain a measurement.

[WorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64) measures resident physical pages, including shareable pages; [PrivateMemorySize64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64) measures non-shareable allocated bytes, not private resident RAM. Do not add them together or equate either with managed heap size. MiB here means 1,048,576 bytes; the user must choose decimal MB versus MiB and the acceptance metric before enforcing the GUI budget.

The service project targets .NET 10 and references the core project (`src/WGTun.Service/WGTun.Service.csproj:2`). Its observed process cost includes .NET runtime/GC, stacks and application state; these samples do not isolate their individual costs. A native Go DLL would not replace .NET. Microsoft documents [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) as potentially reducing memory, but that is a separate compatibility/build effort and outside this task. The service's cost is accepted: preserve it.

## Optional engine reductions, only if later needed

The authoritative [Windows release tag list](https://github.com/SagerNet/sing-box/blob/v1.14.4/release/DEFAULT_BUILD_TAGS_WINDOWS) includes many optional subsystems. Compare against the [v1.14.4 tag descriptions](https://github.com/SagerNet/sing-box/blob/v1.14.4/docs/installation/build-from-source.md#L43-L70), not assumptions about binary size translating to RAM.

- Current local configuration uses TUN `mixed`, userspace WireGuard, SOCKS/direct, UDP DNS and HTTPS DNS (`src/WGTun.Core/SingBoxConfiguration.cs:13`, `:39`, `:49`). Keep `with_wireguard` and `with_gvisor` for that configuration. Basic HTTPS DNS is registered separately from QUIC in the upstream registry; it does not alone require `with_quic`.
- Candidates to omit from a future purpose-built engine, when unused: QUIC/Hysteria/HTTP3, DHCP DNS, uTLS fingerprints, ACME, Clash API, Tailscale, CCM/OCM, Cloudflared, Naive/Cronet, USBIP, OpenVPN and OpenConnect. Keep features required by any accepted profile. Removing Naive can remove its Cronet DLL dependency; `libcronet.dll` is a Naive dependency, not a general sing-box embedding API. See the pinned build documentation's Windows Naive section.
- `mixed` means system TCP plus gVisor UDP; [TUN stack docs](https://github.com/SagerNet/sing-box/blob/v1.14.4/docs/configuration/inbound/tun.md#L512-L524) describe the alternatives. Removing gVisor requires a deliberate `system` configuration change and checking userspace WireGuard requirements too. It is not a safe drop-in tag deletion for the current configuration.
- Excluding optional code can reduce binary/code-page or initialization costs; inactive features may contribute little live heap. No per-tag RAM savings were measured. Preserve upstream linker requirements from the pinned docs/release flags and validate generated configurations plus real TCP/UDP/DNS/routing behavior before adopting a custom build.

## GUI-only recommendation

The inspected GUI uses Rust eframe/egui with the Glow renderer (`src/WGTun.Native/Cargo.toml:7`), with custom font loading (`src/WGTun.Native/src/main.rs:129`). Rust/native compilation alone does not guarantee a tiny GUI: renderer/driver allocations, textures/fonts and application state still count. No attribution to one component is possible from these snapshots.

Keep the engine in the existing broker and GUI IPC unchanged while profiling GUI allocations separately. For a strict 10 MB GUI budget, evaluate a small Win32 controls/GDI UI prototype against the chosen metric; avoid promising that an egui tweak or DLL conversion can reach it. Measure cold start, visible idle, tray idle and normal interactions without working-set trimming tricks. This is a future GUI implementation choice, not authorization to rewrite anything here. Optional engine trimming does not reduce the GUI process when the engine already runs separately.
