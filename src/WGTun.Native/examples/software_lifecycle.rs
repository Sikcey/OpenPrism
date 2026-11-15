//! Isolated renderer regression check: no service, settings, or network access.
#[cfg(windows)]
fn main() {
    use eframe::egui;
    use egui_software_backend::{App, SoftwareBackend, SoftwareBackendAppConfiguration};
    use std::sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    };
    use std::time::{Duration, Instant};
    #[link(name = "user32")]
    unsafe extern "system" {
        fn FindWindowW(class: *const u16, title: *const u16) -> isize;
        fn PostMessageW(hwnd: isize, message: u32, wparam: usize, lparam: isize) -> i32;
        fn IsWindowVisible(hwnd: isize) -> i32;
    }
    const TITLE: &str = "WGTun software lifecycle test";
    fn window() -> isize {
        let title: Vec<u16> = TITLE.encode_utf16().chain(Some(0)).collect();
        unsafe { FindWindowW(std::ptr::null(), title.as_ptr()) }
    }
    struct Check {
        wake: Arc<AtomicBool>,
        canceled: bool,
        reopened: Option<Instant>,
    }
    impl App for Check {
        fn update(&mut self, ctx: &egui::Context, _: &mut SoftwareBackend) {
            egui::CentralPanel::default().show(ctx, |ui| {
                ui.label("Renderer lifecycle check");
            });
            if ctx.input(|i| i.viewport().close_requested()) {
                assert!(!self.canceled, "unexpected duplicate close request");
                self.canceled = true;
                ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
                ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
                let wake = self.wake.clone();
                let ctx = ctx.clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_millis(300));
                    let hidden_ms = std::env::var("WGTUN_LIFECYCLE_HIDDEN_MS")
                        .ok()
                        .and_then(|v| v.parse().ok())
                        .unwrap_or(0);
                    std::thread::sleep(Duration::from_millis(hidden_ms));
                    assert_eq!(
                        unsafe { IsWindowVisible(window()) },
                        0,
                        "window did not hide"
                    );
                    wake.store(true, Ordering::Release);
                    // Same cross-thread notification path used by the tray.
                    ctx.request_repaint();
                });
            }
            if self.wake.swap(false, Ordering::AcqRel) {
                assert!(self.canceled);
                ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                self.reopened = Some(Instant::now());
            }
            if let Some(reopened) = self.reopened {
                if reopened.elapsed() > Duration::from_millis(200) {
                    assert_ne!(
                        unsafe { IsWindowVisible(window()) },
                        0,
                        "window did not reopen"
                    );
                    println!("PASS: OS close canceled, hidden, cross-thread repaint, reopened, explicit exit");
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                } else {
                    ctx.request_repaint_after(Duration::from_millis(250));
                }
            }
        }
    }
    std::thread::spawn(|| {
        let close_ms = std::env::var("WGTUN_LIFECYCLE_CLOSE_MS")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(800);
        std::thread::sleep(Duration::from_millis(close_ms));
        assert_ne!(window(), 0);
        assert_ne!(unsafe { PostMessageW(window(), 0x10, 0, 0) }, 0);
    });
    let mut config = SoftwareBackendAppConfiguration::new().title(Some(TITLE.into()));
    config.caching = false;
    egui_software_backend::run_app_with_software_backend(config, |_| Check {
        wake: Arc::new(AtomicBool::new(false)),
        canceled: false,
        reopened: None,
    })
    .unwrap();
}

#[cfg(not(windows))]
fn main() {
    eprintln!("This regression check requires Windows.");
}
