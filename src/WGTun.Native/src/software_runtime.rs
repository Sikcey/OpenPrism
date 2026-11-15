//! Opt-in CPU rendering, preserving the application's egui widget tree.
//!
//! This runner uses softbuffer (GDI on Windows), without creating a GL context.
//! Keep Glow available: the upstream software runner does not implement egui's
//! screenshot or IME viewport commands yet.
use eframe::egui;
use egui_software_backend::{App, SoftwareBackendAppConfiguration};

pub fn run<A: App>(
    viewport: egui::ViewportBuilder,
    factory: impl FnMut(egui::Context) -> A,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let mut settings = SoftwareBackendAppConfiguration::new().viewport_builder(viewport);
    // Avoid retaining a rasterized copy of every primitive. This trades some
    // repaint CPU time for a smaller steady-state memory footprint.
    settings.caching = false;
    egui_software_backend::run_app_with_software_backend(settings, factory).map_err(|error| {
        Box::new(std::io::Error::other(error.to_string()))
            as Box<dyn std::error::Error + Send + Sync>
    })
}
