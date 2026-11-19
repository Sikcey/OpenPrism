use crate::{b, locale, n, s, small, title, App, BG, INK, LINE, PANEL, SUB};
use eframe::egui::{self, RichText, Stroke, Vec2};
use serde_json::json;

impl App {
    pub(super) fn profiles(&mut self, ui: &mut egui::Ui) {
        title(ui, "Yönlendirme", "Profiller");
        ui.label(
            RichText::new(locale::tr(
                "Bağlantı yöntemlerini ve stratejilerini burada yapılandır.",
            ))
            .size(15.)
            .color(SUB),
        );
        ui.add_space(22.);
        let locked = self.connected || self.pending != 0;
        ui.add_enabled_ui(!locked, |ui| {
            let view = self.profile_view.unwrap_or_else(|| n(&self.settings, "profile"));
            ui.horizontal_wrapped(|ui| {
                for (kind, label) in [(0, "WireGuard"), (2, "ByeDPI"), (3, "Zapret")] {
                    let highlighted = (view == 0 || view == 1) && kind == 0 || view == kind;
                    let response = ui.add_enabled(kind != 4,
                        egui::Button::new(RichText::new(locale::tr(label)).size(12.).color(if highlighted { INK } else { SUB }))
                            .min_size(Vec2::new(0., 40.)).corner_radius(0)
                            .fill(BG)
                            .stroke(Stroke::NONE));
                    if highlighted { crate::widgets::gradient_fill(ui.painter(), egui::Rect::from_min_size(response.rect.left_bottom()-Vec2::new(0.,2.),Vec2::new(response.rect.width(),2.)),0.,1.); }
                    if response.clicked() {
                        self.profile_view = Some(if kind == 0 && n(&self.settings, "profile") == 1 { 1 } else { kind });
                        self.settings["profile"] = json!(self.profile_view.unwrap());
                        self.settings["connectionMethod"] = json!(if kind == 3 { 2 } else if kind == 2 { 1 } else { 0 });
                        self.save();
                    }
                }
            });
            ui.add_space(10.);
            ui.separator();
            ui.add_space(17.);
            egui::Frame::new().fill(PANEL).stroke(Stroke::new(1_f32,LINE)).corner_radius(14).inner_margin(24).show(ui, |ui| {
            ui.set_min_width(ui.available_width());
            let view = self.profile_view.unwrap_or_else(|| n(&self.settings, "profile"));
            ui.label(RichText::new(if view == 3 { "Zapret" } else if view == 2 { "ByeDPI" } else { "WireGuard" }).size(18.));
            ui.add_space(5.);
            small(ui, if view == 3 {
                "Deneysel IPv4 modu. Seçilen trafik ayrı relay ve Zapret filtresinden geçer."
            } else if view == 2 {
                "VPN tüneli kurmadan DPI tabanlı engellere karşı alternatif bir bağlantı yöntemi kullan."
            } else {
                "Trafiğini şifreli bir tünel üzerinden yönlendir. Cloudflare WARP kullanabilir veya kendi yapılandırmanı ekleyebilirsin."
            });
            ui.add_space(20.);
            if view == 2 || view == 3 {
                let engine = if view == 3 { "zapret" } else { "byedpi" };
                if s(&self.dpi_draft, "engine") != engine {
                    let key = if view == 3 { "zapretDpi" } else { "dpi" };
                    self.dpi_draft = self.settings[key].clone();
                    if !self.dpi_draft.is_object() { self.dpi_draft = json!({}); }
                    self.dpi_draft["engine"] = json!(engine);
                    self.call("presets", vec![], None);
                }
                self.dpi_config(ui);
            } else {
                small(ui, "Profil");
                let mut provider = if view == 1 { 1 } else { 0 };
                let before = provider;
                ui.scope(|ui| {
                    ui.spacing_mut().interact_size.y = 38.;
                    egui::ComboBox::from_id_salt("profile_provider").width(ui.available_width())
                        .selected_text(locale::tr(if provider == 1 { "Özel WireGuard yapılandırması" } else { "Cloudflare WARP" }))
                        .show_ui(ui, |ui| {
                            ui.selectable_value(&mut provider, 0, "Cloudflare WARP");
                            ui.selectable_value(&mut provider, 1, locale::tr("Özel WireGuard yapılandırması"));
                        });
                });
                self.profile_view = Some(provider);
                if before != provider { self.settings["profile"] = json!(provider); self.save(); }
                ui.add_space(6.);
                if provider == 1 {
                    let file = s(&self.settings, "wireGuardProfileName");
                    small(ui, if file.is_empty() { "Kendi sunucuna ait .conf dosyasını içe aktar." } else { &file });
                    ui.add_space(7.);
                    if ui.add(egui::Button::new(locale::tr(".conf dosyası seç")).fill(PANEL).corner_radius(7)).clicked() {
                        if let Some(path) = rfd::FileDialog::new().add_filter("WireGuard", &["conf"]).pick_file() {
                            self.call("import-wireguard", vec![path.to_string_lossy().into_owned()], None);
                        }
                    }
                } else {
                    let prepared = b(&self.scan, "hasPreparedWarpProfile");
                    if !prepared {
                        small(ui, "Ücretsiz WARP hesabını OpenPrism Kurulumu hazırlar.");
                    }
                    ui.add_enabled_ui(!self.offline && !self.inspect_only, |ui| {
                        egui::ComboBox::from_id_salt("account").width(ui.available_width())
                            .selected_text(locale::tr(if prepared { "Kayıtlı WARP hesabı" } else { "WARP hesabı" }))
                            .show_ui(ui, |ui| {
                                if ui.add_enabled(b(&self.scan, "hasWarpAccount"), egui::Button::selectable(false, locale::tr("WARP / WARP+ lisans anahtarı"))).clicked() {
                                    ui.close();
                                    self.license_dialog = true;
                                }
                                if ui.selectable_label(false, locale::tr("Mevcut wgcf hesabını içe aktar")).clicked() {
                                    ui.close();
                                    if let Some(path) = rfd::FileDialog::new().add_filter("wgcf", &["toml"]).pick_file() {
                                        self.call("import-warp", vec![path.to_string_lossy().into_owned()], None);
                                    }
                                }
                            });
                    });
                }
            }
            });
        });
        if self.license_dialog {
            let mut open = true;
            let mut submit = false;
            egui::Window::new(locale::tr("WARP / WARP+ lisans anahtarı"))
                .open(&mut open)
                .collapsible(false)
                .resizable(false)
                .show(ui.ctx(), |ui| {
                    let field = ui.add(egui::TextEdit::singleline(&mut self.license)
                        .password(true).hint_text(locale::tr("Lisans anahtarı")));
                    let clicked = ui.button(locale::tr("Anahtarı kullan")).clicked();
                    submit = !locked && !self.license.trim().is_empty() && (clicked
                        || field.lost_focus() && ui.input(|input| input.key_pressed(egui::Key::Enter)));
                });
            if submit {
                let license = std::mem::take(&mut self.license);
                self.call("warp-license", vec![], Some(json!({"license":license.trim()})));
            }
            if submit || !open {
                self.license.clear();
                self.license_dialog = false;
            }
        }
        self.routing_lock_notice(ui);
    }
}
