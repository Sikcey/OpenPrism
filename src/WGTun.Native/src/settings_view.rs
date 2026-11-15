use crate::{card, locale, s, small, widgets, INK, SUB};
use eframe::egui::{self, RichText, Vec2};
use serde_json::{json, Value};

pub fn section(ui: &mut egui::Ui, label: &str) {
    ui.add_space(15.);
    ui.label(
        RichText::new(locale::tr(label).to_uppercase())
            .size(10.)
            .color(SUB),
    );
    ui.add_space(3.);
}
pub fn row(ui: &mut egui::Ui, label: &str, description: &str, control: impl FnOnce(&mut egui::Ui)) {
    ui.horizontal(|ui| {
        let width = (ui.available_width() - 140.).max(130.);
        ui.allocate_ui_with_layout(
            Vec2::new(width, 48.),
            egui::Layout::top_down(egui::Align::LEFT),
            |ui| {
                ui.label(RichText::new(locale::tr(label)).size(12.).color(INK));
                small(ui, description);
            },
        );
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), control);
    });
}
pub fn toggle(
    ui: &mut egui::Ui,
    label: &str,
    description: &str,
    value: &mut bool,
    enabled: bool,
) -> bool {
    let mut changed = false;
    row(ui, label, description, |ui| {
        ui.add_enabled_ui(enabled, |ui| {
            changed = widgets::toggle(ui, value)
                .on_hover_text(locale::tr(label))
                .changed();
        });
    });
    changed
}
pub fn general(ui: &mut egui::Ui, settings: &mut Value) -> bool {
    let mut changed = false;
    section(ui, "Genel");
    card(ui, |ui| {
        row(ui, "Uygulama dili", "Arayüzde kullanılacak dil.", |ui| {
            let mut language = locale::LANGUAGES
                .iter()
                .find(|(id, _)| *id == s(settings, "language"))
                .map(|(id, _)| *id)
                .unwrap_or("tr");
            let old = language;
            egui::ComboBox::from_id_salt("settings_language")
                .width(110.)
                .selected_text(
                    locale::LANGUAGES
                        .iter()
                        .find(|(id, _)| *id == language)
                        .unwrap()
                        .1,
                )
                .show_ui(ui, |ui| {
                    for (id, label) in locale::LANGUAGES {
                        ui.selectable_value(&mut language, id, label);
                    }
                });
            if old != language {
                settings["language"] = json!(language);
                locale::set_language(language);
                locale::apply_fonts(ui.ctx());
                changed = true;
            }
        });
        ui.separator();
        let mut tray = settings["closeToTray"].as_bool().unwrap_or(false);
        if toggle(
            ui,
            "Sistem tepsisine küçült",
            "Pencereyi kapattığında arka planda çalışmaya devam et.",
            &mut tray,
            true,
        ) {
            settings["closeToTray"] = json!(tray);
            changed = true;
        }
    });
    changed
}
pub fn about(ui: &mut egui::Ui, update: &Value, busy: bool) -> bool {
    let mut check = false;
    section(ui, "Hakkında");
    card(ui, |ui| {
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                ui.label(format!("WGTun  v{}", env!("CARGO_PKG_VERSION")));
                small(ui, "WireGuard ve DPI yönlendirme, tek yerde.");
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                check = ui
                    .add_enabled(
                        !busy,
                        egui::Button::new(locale::tr("Güncellemeleri kontrol et")).corner_radius(7),
                    )
                    .clicked();
            });
        });
        let message = s(update, "message");
        if !message.is_empty() {
            small(ui, &message);
        }
        if update["updateAvailable"].as_bool() == Some(true) {
            let url = s(update, "releaseUrl");
            if url.starts_with("https://github.com/Sikcey/OpenPrism/") {
                ui.hyperlink_to(locale::tr("Sürümü aç"), url);
            }
        }
        ui.separator();
        ui.horizontal_wrapped(|ui| {
            ui.hyperlink_to(
                locale::tr("Kaynak kodu ↗"),
                "https://github.com/Sikcey/OpenPrism",
            );
            ui.add_space(10.);
            ui.hyperlink_to(
                locale::tr("Sorun bildir ↗"),
                "https://github.com/Sikcey/OpenPrism/issues",
            );
        });
    });
    section(ui, "Credits");
    card(ui, |ui| {
        small(
            ui,
            "WGTun’a güç veren açık kaynak projelerine ve geliştiricilerine teşekkürler.",
        );
        for (name, author, purpose, license, url) in [
            (
                "ProxiFyre / Windows Packet Filter",
                "Vadim Smirnov / wiresock",
                "Application capture",
                "AGPL-3.0 / MIT",
                "https://github.com/wiresock/proxifyre",
            ),
            (
                "Zapret / WinDivert",
                "bol-van / basil00",
                "Trafik yönlendirme",
                "—",
                "https://github.com/bol-van/zapret-win-bundle",
            ),
            (
                "sing-box",
                "SagerNet",
                "Trafik yönlendirme",
                "GPL-3.0-or-later",
                "https://github.com/SagerNet/sing-box",
            ),
            (
                "ByeDPI",
                "hufrea",
                "DPI aşma",
                "MIT",
                "https://github.com/hufrea/byedpi",
            ),
            (
                "wgcf",
                "ViRb3",
                "WARP hesap ve profil araçları",
                "MIT",
                "https://github.com/ViRb3/wgcf",
            ),
            (
                "egui / eframe",
                "emilk",
                "Kullanıcı arayüzü",
                "MIT / Apache-2.0",
                "https://github.com/emilk/egui",
            ),
            (
                "egui_software_backend",
                "DGriffin91",
                "Yazılımsal çizim",
                "MIT / Apache-2.0",
                "https://github.com/DGriffin91/egui_software_backend",
            ),
        ] {
            ui.separator();
            ui.horizontal(|ui| {
                let width = (ui.available_width() - 140.).max(130.);
                ui.allocate_ui_with_layout(
                    Vec2::new(width, 43.),
                    egui::Layout::top_down(egui::Align::LEFT),
                    |ui| {
                        ui.hyperlink_to(format!("{name} ↗"), url);
                        small(ui, &format!("{author} · {}", locale::tr(purpose)));
                    },
                );
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    small(ui, license);
                });
            });
        }
    });
    ui.add_space(8.);
    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
        small(ui, "Değişiklikler otomatik kaydedilir");
    });
    check
}
