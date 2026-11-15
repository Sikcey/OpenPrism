# WGTun local patches

Upstream: egui_software_backend 0.0.2, MIT OR Apache-2.0.
Original license files are retained. Only `src/winit.rs` runtime behavior is
changed; rasterization code is unchanged. The manifest omits upstream example,
integration-test, and development-only targets that are not vendored here.

Changes:

* Deliver OS close as a root viewport Close event, then honor CancelClose.
* Process completed root viewport commands after Context::run, checking close
  once after the command loop, including when that loop is empty.
* Call the app exit hook on accepted close.
* Preserve the earliest requested repaint deadline using Wait/WaitUntil.
* On a due hidden-window repaint, advance egui directly. Hidden Windows windows
  do not receive ordinary paint requests; this permits tray notifications to
  unhide the window without continuous polling.

Regression check: `cargo run --manifest-path src/WGTun.Native/Cargo.toml --example software_lifecycle --release`.
It never contacts the WGTun service or changes settings/network state.

Upstream source: https://github.com/DGriffin91/egui_software_backend
