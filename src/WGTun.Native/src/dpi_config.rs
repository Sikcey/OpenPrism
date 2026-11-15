use crate::{b, locale, n, s, small, App, ACCENT, BG, INK, LINE, PANEL, SUB};
use eframe::egui::{self, RichText, Stroke, Vec2};
use serde_json::json;

impl App {
    pub(super) fn dpi_config(&mut self, ctx: &egui::Context) {
        let mut close = false;
        let engine = if s(&self.dpi_draft, "engine") == "zapret" {
            "zapret"
        } else {
            "byedpi"
        };
        let settings_key = if engine == "zapret" {
            "zapretDpi"
        } else {
            "dpi"
        };
        let standard = if engine == "zapret" {
            "zapret-standard"
        } else {
            "byedpi-standard"
        };
        let modal =
            egui::Modal::new(egui::Id::new("dpi_config"))
                .frame(
                    egui::Frame::new()
                        .fill(PANEL)
                        .stroke(Stroke::new(1_f32, LINE))
                        .corner_radius(14)
                        .inner_margin(24),
                )
                .show(ctx, |ui| {
                    ui.set_width((ctx.content_rect().width() - 64.).clamp(320., 520.));
                    ui.label(
                        RichText::new(locale::tr("ByeDPI yapılandırması"))
                            .size(20.)
                            .color(INK),
                    );
                    ui.add_space(10.);
                    ui.add_enabled_ui(!self.goodcheck_running, |ui| {
                        small(ui, "Strateji");
                        let mut preset = s(&self.dpi_draft, "presetId");
                        let name = self
                            .dpi_presets
                            .iter()
                            .find(|p| s(p, "id") == preset)
                            .map(|p| s(p, "name"))
                            .unwrap_or_else(|| {
                                if preset == standard {
                                    locale::tr("Standart").into()
                                } else {
                                    preset.clone()
                                }
                            });
                        egui::ComboBox::from_id_salt("dpi_preset")
                            .width(ui.available_width())
                            .selected_text(name)
                            .show_ui(ui, |ui| {
                                ui.selectable_value(
                                    &mut preset,
                                    standard.into(),
                                    locale::tr("Standart"),
                                );
                                for item in &self.dpi_presets {
                                    if s(item, "id") != standard
                                        && (s(item, "engine") == engine
                                            || (engine == "byedpi" && s(item, "engine").is_empty()))
                                    {
                                        ui.selectable_value(
                                            &mut preset,
                                            s(item, "id"),
                                            s(item, "name"),
                                        );
                                    }
                                }
                            });
                        self.dpi_draft["presetId"] = json!(preset);
                        self.dpi_draft["engine"] = json!(engine);
                        self.dpi_draft["customArguments"] = json!("");
                        ui.add_space(8.);
                        small(ui, "DNS · isteğe bağlı");
                        let mut dns = s(&self.dpi_draft, "dns");
                        ui.add_sized(
                            [ui.available_width(), 34.],
                            egui::TextEdit::singleline(&mut dns)
                                .hint_text(locale::tr("Varsayılan DNS")),
                        );
                        self.dpi_draft["dns"] = json!(dns);
                    });
                    ui.add_space(16.);
                    let check_open = self.goodcheck_running || !self.goodcheck_result.is_null();
                    egui::CollapsingHeader::new(RichText::new("GoodCheck").size(14.).color(INK))
                        .default_open(check_open)
                        .show(ui, |ui| {
                            small(ui, "İsteğe bağlı · Ağında çalışan stratejileri test et.");
                            ui.add_space(8.);
                            small(ui, "Test edilecek alan adları");
                            ui.add_enabled_ui(!self.goodcheck_running, |ui| {
                                ui.add_sized(
                                    [ui.available_width(), 56.],
                                    egui::TextEdit::multiline(&mut self.goodcheck_hosts)
                                        .hint_text("discord.com\nyoutube.com"),
                                );
                            });
                            small(ui, "Her satıra bir alan adı yaz · en fazla 8 alan adı.");
                            ui.add_space(8.);
                            if self.goodcheck_running {
                                ui.horizontal(|ui| {
                                    ui.spinner();
                                    small(ui, "GoodCheck taraması sürüyor…");
                                    if ui.button(locale::tr("Durdur")).clicked() {
                                        self.call("goodcheck-cancel", vec![], None);
                                    }
                                });
                                small(ui, &self.notice);
                            } else if ui
                                .add_enabled(
                                    !self.offline && !self.connected && self.pending == 0,
                                    egui::Button::new(locale::tr("GoodCheck ile tara")),
                                )
                                .clicked()
                            {
                                let hosts: Vec<_> = self
                                    .goodcheck_hosts
                                    .split(|c: char| c.is_whitespace() || c == ',')
                                    .filter(|h| !h.is_empty())
                                    .collect();
                                self.goodcheck_result = serde_json::Value::Null;
                                self.goodcheck_running = true;
                                self.call(
                                    "goodcheck",
                                    vec![],
                                    Some(json!({"hosts": hosts, "engine": engine})),
                                );
                            }
                            if !self.goodcheck_result.is_null() && !self.goodcheck_running {
                                let code = s(&self.goodcheck_result, "code");
                                small(
                        ui,
                        match code.as_str() {
                            "complete" if b(&self.goodcheck_result, "success") => {
                                "Tarama tamamlandı. Bir sonuç seçip Uygula düğmesine bas."
                            }
                            "complete" => "Test edilen alan adlarında çalışan strateji bulunamadı.",
                            "cancelled" => "Tarama durduruldu.",
                            "timeout" => "Tarama zaman aşımına uğradı.",
                            "engine-running" => "Taramadan önce bağlantıyı kes.",
                            "service-update" => {
                                "GoodCheck için Hizmetler ekranından hizmeti kur veya onar."
                            }
                            _ => "GoodCheck taraması tamamlanamadı.",
                        },
                    );
                                if !s(&self.goodcheck_result, "message").is_empty() {
                                    small(ui, &s(&self.goodcheck_result, "message"));
                                }
                                let results = self.goodcheck_result["results"]
                                    .as_array()
                                    .cloned()
                                    .unwrap_or_default();
                                ui.add_space(5.);
                                egui::ScrollArea::vertical()
                                    .id_salt("goodcheck_results")
                                    .max_height(150.)
                                    .show(ui, |ui| {
                                        for item in results {
                                            if !s(&item, "engine").is_empty()
                                                && s(&item, "engine") != engine
                                            {
                                                continue;
                                            }
                                            ui.add_enabled_ui(n(&item, "successes") > 0, |ui| {
                                                ui.radio_value(
                                                    &mut self.goodcheck_selected,
                                                    s(&item, "presetId"),
                                                    format!(
                                                        "{} · {}/{}",
                                                        s(&item, "name"),
                                                        n(&item, "successes"),
                                                        n(&item, "total")
                                                    ),
                                                );
                                            });
                                            if let Some(hosts) = item["hosts"].as_array() {
                                                for host in hosts {
                                                    ui.label(format!(
                                                        "{} ? {} ? {} ? {}",
                                                        s(host, "host"),
                                                        s(host, "stage"),
                                                        if b(host, "success") {
                                                            locale::tr("Başarılı")
                                                        } else {
                                                            locale::tr("Başarısız")
                                                        },
                                                        s(host, "outcome")
                                                    ));
                                                }
                                            }
                                        }
                                    });
                                if !self.goodcheck_selected.is_empty()
                                    && ui
                                        .add(
                                            egui::Button::new(
                                                RichText::new(locale::tr("Uygula")).color(BG),
                                            )
                                            .fill(ACCENT)
                                            .corner_radius(7),
                                        )
                                        .clicked()
                                {
                                    self.dpi_draft["presetId"] = json!(self.goodcheck_selected);
                                    self.settings[settings_key] = self.dpi_draft.clone();
                                    self.save();
                                    close = true;
                                }
                            }
                        });
                    ui.add_space(14.);
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui
                            .add_enabled(
                                !self.goodcheck_running && !self.connected,
                                egui::Button::new(RichText::new(locale::tr("Kaydet")).color(BG))
                                    .fill(ACCENT)
                                    .min_size(Vec2::new(76., 34.))
                                    .corner_radius(7),
                            )
                            .clicked()
                        {
                            self.settings[settings_key] = self.dpi_draft.clone();
                            self.save();
                            close = true;
                        }
                        if ui
                            .button(RichText::new(locale::tr("Vazgeç")).color(SUB))
                            .clicked()
                        {
                            close = true;
                        }
                    });
                });
        if close || modal.should_close() {
            if self.goodcheck_running {
                self.call("goodcheck-cancel", vec![], None);
            }
            self.dpi_dialog = false;
        }
    }
}
