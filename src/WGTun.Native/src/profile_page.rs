use crate::{b, locale, n, s, small, title, App, ACCENT, BG, INK, LINE, PANEL, SOFT, SUB};
use eframe::egui::{self, RichText, Stroke, Vec2};
use serde_json::json;

impl App {
    pub(super) fn profiles(&mut self, ui: &mut egui::Ui) {
        title(ui, "Yönlendirme", "Profiller");
        small(ui, "Yöntemi incele, kullanmak istediğin profili seç.");
        ui.add_space(22.);
        let locked = self.connected || self.pending != 0;
        ui.add_enabled_ui(!locked, |ui| {
            let view = self.profile_view.unwrap_or_else(|| n(&self.settings, "profile"));
            ui.horizontal_wrapped(|ui| {
                for (kind, label) in [(0, "WireGuard"), (2, "ByeDPI"), (3, "Zapret"), (4, "Diğer · Yakında")] {
                    let highlighted = (view == 0 || view == 1) && kind == 0 || view == kind;
                    let response = ui.add_enabled(kind != 4,
                        egui::Button::new(RichText::new(locale::tr(label)).size(12.).color(if highlighted { INK } else { SUB }))
                            .min_size(Vec2::new(0., 34.)).corner_radius(8)
                            .fill(if highlighted { SOFT } else { BG })
                            .stroke(Stroke::new(1_f32, if highlighted { ACCENT } else { LINE })));
                    if response.clicked() {
                        self.profile_view = Some(if kind == 0 && n(&self.settings, "profile") == 1 { 1 } else { kind });
                    }
                }
            });
            ui.add_space(10.);
            ui.separator();
            ui.add_space(17.);
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
            if view == 2 {
                small(ui, "YAPILANDIRMA");
                let preset = s(&self.settings["dpi"], "presetId");
                let name = if preset == "byedpi-standard" { locale::tr("Standart ByeDPI").to_owned() }
                    else { self.dpi_presets.iter().find(|p| s(p, "id") == preset).map(|p| s(p, "name")).unwrap_or(preset) };
                ui.label(name);
                small(ui, "Seçili uygulamaların trafiğinde DPI aşma yöntemini kullanır.");
                ui.add_space(10.);
                if ui.button(locale::tr("Yapılandır")).clicked() {
                    self.dpi_draft = self.settings["dpi"].clone();
                    self.dpi_draft["engine"] = json!("byedpi");
                    self.goodcheck_result = serde_json::Value::Null;
                    self.dpi_dialog = true;
                    self.call("presets", vec![], None);
                }
            } else if view == 3 {
                small(ui, "YAPILANDIRMA");
                let preset = s(&self.settings["zapretDpi"], "presetId");
                ui.label(if preset.is_empty() { "Zapret standard".to_owned() } else { self.dpi_presets.iter().find(|p| s(p, "id") == preset).map(|p| s(p, "name")).unwrap_or(preset) });
                if ui.button(locale::tr("Configure")).clicked() {
                    self.dpi_draft = self.settings["zapretDpi"].clone();
                    if !self.dpi_draft.is_object() { self.dpi_draft = json!({"presetId":"zapret-standard", "dns":""}); }
                    self.dpi_draft["engine"] = json!("zapret");
                    self.goodcheck_result = serde_json::Value::Null;
                    self.dpi_dialog = true;
                    self.call("presets", vec![], None);
                }
                small(ui, "IPv6 seçili trafikte desteklenmez. İlk kullanımdan önce hizmeti kur veya onar.");
            } else {
                small(ui, "Profil");
                let mut provider = if view == 1 { 1 } else { 0 };
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
                    small(ui, "Cloudflare hesabı ve bağlantı ayarları otomatik hazırlanır.");
                    if self.warp_preparing { ui.spinner(); small(ui, "Cloudflare hesabı hazırlanıyor…"); }
                    else if let Some(error) = self.warp_error.clone() {
                        ui.colored_label(egui::Color32::LIGHT_RED, error);
                        if ui.add_enabled(!self.offline && !self.inspect_only && b(&self.scan, "serviceReady"), egui::Button::new(locale::tr("Tekrar dene"))).clicked() { self.prepare_warp(); }
                    } else if b(&self.scan, "hasPreparedWarpProfile") { small(ui, "Cloudflare profili hazır."); }
                    ui.add_space(9.);
                    ui.collapsing(locale::tr("Hesap seçenekleri"), |ui| {
                        egui::ComboBox::from_id_salt("account").width(ui.available_width())
                            .selected_text(locale::tr(["Ücretsiz WARP hesabı", "WARP / WARP+ lisans anahtarı", "Mevcut wgcf hesabını içe aktar"][self.account]))
                            .show_ui(ui, |ui| {
                                for (index, label) in ["Ücretsiz WARP hesabı", "WARP / WARP+ lisans anahtarı", "Mevcut wgcf hesabını içe aktar"].iter().enumerate() {
                                    ui.selectable_value(&mut self.account, index, locale::tr(label));
                                }
                            });
                        if self.account == 1 {
                            ui.add(egui::TextEdit::singleline(&mut self.license).password(true).hint_text(locale::tr("Lisans anahtarı")));
                            if ui.button(locale::tr("Anahtarı kullan")).clicked() {
                                self.call("warp-license", vec![], Some(json!({"license":self.license})));
                                self.license.clear();
                            }
                        } else if self.account == 2 && ui.button(locale::tr("Hesap dosyası seç")).clicked() {
                            if let Some(path) = rfd::FileDialog::new().add_filter("wgcf", &["toml"]).pick_file() {
                                self.call("import-warp", vec![path.to_string_lossy().into_owned()], None);
                            }
                        }
                        ui.hyperlink_to(locale::tr("Cloudflare WARP kullanım koşulları"), "https://www.cloudflare.com/application/terms/");
                    });
                }
            }
            ui.add_space(27.);
            ui.separator();
            ui.add_space(10.);
            let target = self.profile_view.unwrap_or(view);
            let ready = target != 1 || !s(&self.settings, "wireGuardProfileName").is_empty();
            ui.horizontal(|ui| {
                small(ui, "Uygulamak için Seç düğmesine bas.");
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.add_enabled(ready && target != n(&self.settings, "profile"),
                        egui::Button::new(RichText::new(locale::tr("Seç")).color(BG).strong()).fill(ACCENT)
                            .corner_radius(8).min_size(Vec2::new(65., 34.))).clicked() {
                        self.settings["profile"] = json!(target);
                        self.settings["connectionMethod"] = json!(if target == 3 { 2 } else if target == 2 { 1 } else { 0 });
                        self.settings["dpi"]["engine"] = json!("byedpi");
                        self.save();
                    }
                });
            });
        });
        self.routing_lock_notice(ui);
    }
}
