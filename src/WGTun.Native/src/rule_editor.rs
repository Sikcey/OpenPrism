use crate::{
    default_rule_name, locale, n, rule_display_name, s, small, ACCENT, BG, INK, LINE, PANEL, SUB,
};
use eframe::egui::{self, RichText, Stroke, Vec2};
use serde_json::{json, Value};

#[derive(Default, PartialEq)]
pub enum Action {
    #[default]
    None,
    Save,
    Delete,
    Cancel,
}

pub fn render(
    ctx: &egui::Context,
    rule: &mut Value,
    editing: bool,
    pending: bool,
    error: &str,
) -> Action {
    let mut action = Action::None;
    let response = egui::Modal::new(egui::Id::new("rule_editor"))
        .backdrop_color(egui::Color32::from_black_alpha(175))
        .frame(
            egui::Frame::new()
                .fill(PANEL)
                .stroke(Stroke::new(1_f32, LINE))
                .corner_radius(14)
                .inner_margin(24),
        )
        .show(ctx, |ui| {
            ui.set_width((ctx.content_rect().width() - 100.).clamp(280., 440.));
            ui.spacing_mut().item_spacing.y = 8.;
            ui.horizontal(|ui| {
                ui.label(
                    RichText::new(locale::tr(if editing {
                        "Kuralı düzenle"
                    } else {
                        "Kural ekle"
                    }))
                    .size(20.)
                    .color(INK),
                );
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .add_enabled(
                            !pending,
                            egui::Button::new(RichText::new("×").size(22.).color(SUB)).frame(false),
                        )
                        .clicked()
                    {
                        action = Action::Cancel;
                    }
                });
            });
            small(ui, "Yönlendirilecek trafiği belirle.");
            ui.add_space(13.);
            ui.add_enabled_ui(!pending, |ui| {
                small(ui, "Kural adı · isteğe bağlı");
                let mut name = if s(rule, "name").is_empty() {
                    String::new()
                } else {
                    rule_display_name(rule)
                };
                ui.add_sized(
                    [ui.available_width(), 38.],
                    egui::TextEdit::singleline(&mut name).hint_text(locale::tr("Örn. Ev ağı")),
                );
                rule["name"] = json!(name);
                ui.add_space(10.);
                small(ui, "Kural türü");
                let before = n(rule, "type");
                let mut kind = before;
                ui.scope(|ui| {
                    ui.spacing_mut().interact_size.y = 38.;
                    egui::ComboBox::from_id_salt("rule_type")
                        .width(ui.available_width())
                        .selected_text(type_label(kind))
                        .show_ui(ui, |ui| {
                            small(ui, "IP bazlı");
                            for kind_value in 1..=4 {
                                ui.selectable_value(&mut kind, kind_value, type_label(kind_value));
                            }
                            ui.separator();
                            ui.selectable_value(&mut kind, 5, locale::tr("İşlem adı"));
                            ui.selectable_value(&mut kind, 6, locale::tr("Uygulama yolu"));
                            ui.selectable_value(&mut kind, 7, locale::tr("Klasör yolu"));
                        });
                });
                if kind != before {
                    rule["type"] = json!(kind);
                    rule["value"] = json!("");
                }
                ui.add_space(10.);
                let (label, placeholder, hint) = field(kind);
                ui.add_space(10.);
                small(ui, label);
                let mut value = s(rule, "value");
                ui.horizontal(|ui| {
                    let width =
                        (ui.available_width() - if kind >= 6 { 106. } else { 0. }).max(100.);
                    ui.add_sized(
                        [width, 38.],
                        egui::TextEdit::singleline(&mut value).hint_text(placeholder),
                    );
                    if kind >= 6
                        && ui
                            .add_sized(
                                [98., 38.],
                                egui::Button::new(locale::tr(if kind == 6 {
                                    ".exe seç"
                                } else {
                                    "Klasör seç"
                                }))
                                .corner_radius(8),
                            )
                            .clicked()
                    {
                        let selected = if kind == 6 {
                            rfd::FileDialog::new()
                                .add_filter("Windows uygulaması", &["exe"])
                                .pick_file()
                        } else {
                            rfd::FileDialog::new().pick_folder()
                        };
                        if let Some(path) = selected {
                            value = path.to_string_lossy().into_owned();
                            rule["type"] = json!(kind);
                        }
                    }
                });
                rule["value"] = json!(value.trim());
                small(ui, hint);
            });
            ui.add_space(14.);
            ui.separator();
            if !error.is_empty() {
                ui.colored_label(egui::Color32::from_rgb(255, 168, 155), error);
            }
            ui.add_space(6.);
            ui.horizontal(|ui| {
                if editing && crate::widgets::removal_button(ui, "Sil", !pending).clicked()
                {
                    action = Action::Delete;
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .add_enabled(
                            !pending,
                            egui::Button::new(
                                RichText::new(locale::tr(if pending {
                                    "Kontrol ediliyor…"
                                } else {
                                    "Kaydet"
                                }))
                                .color(BG)
                                .strong(),
                            )
                            .fill(ACCENT)
                            .corner_radius(8)
                            .min_size(Vec2::new(76., 36.)),
                        )
                        .clicked()
                    {
                        if s(rule, "name").trim().is_empty() {
                            rule["name"] =
                                json!(default_rule_name(n(rule, "type"), &s(rule, "value")));
                        }
                        action = Action::Save;
                    }
                    if ui
                        .add_enabled(
                            !pending,
                            egui::Button::new(RichText::new(locale::tr("Vazgeç")).color(SUB))
                                .frame(false)
                                .min_size(Vec2::new(66., 36.)),
                        )
                        .clicked()
                    {
                        action = Action::Cancel;
                    }
                });
            });
        });
    if !pending && response.should_close() {
        action = Action::Cancel;
    }
    action
}

fn type_label(kind: i64) -> &'static str {
    locale::tr(match kind {
        1 => "IPv4 adresi",
        2 => "IPv6 adresi",
        3 => "IPv4 ağı / CIDR",
        4 => "IPv6 ağı / CIDR",
        5 => "İşlem adı",
        6 => "Uygulama yolu",
        _ => "Klasör yolu",
    })
}
fn field(kind: i64) -> (&'static str, &'static str, &'static str) {
    match kind {
        1 => (
            "IP adresi",
            "192.168.1.10",
            "Yalnızca bu IPv4 adresine giden trafik eşleştirilir.",
        ),
        2 => (
            "IP adresi",
            "2001:db8::1",
            "Yalnızca bu IPv6 adresine giden trafik eşleştirilir.",
        ),
        3 => (
            "Ağ / CIDR",
            "192.168.1.0/24",
            "Bu IPv4 ağındaki adreslere giden trafik eşleştirilir.",
        ),
        4 => (
            "Ağ / CIDR",
            "2001:db8::/32",
            "Bu IPv6 ağındaki adreslere giden trafik eşleştirilir.",
        ),
        5 => (
            "Uygulama yolu",
            "Discord.exe",
            "İşlem adını düzenle veya uygulama klasörünü seç.",
        ),
        6 => (
            "Uygulama yolu",
            "C:\\Program Files\\Zen Browser\\zen.exe",
            "Uygulamanın tam dosya yolunu düzenle veya klasör seç.",
        ),
        _ => (
            "Uygulama yolu",
            "C:\\Program Files\\Zen Browser",
            "Seçilen klasördeki ve alt klasörlerindeki uygulamalar eşleştirilir.",
        ),
    }
}
