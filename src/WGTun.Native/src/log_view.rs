use std::collections::VecDeque;

use eframe::egui::{self, Color32, RichText, Stroke};

use crate::{locale, widgets, BG, INK, LINE, PANEL, SUB};

#[derive(Default)]
pub struct LogView {
    pub filter_string: String,
}

#[derive(Default)]
pub struct Actions {
    pub refresh: bool,
    pub clear: bool,
    pub detailed_changed: bool,
}

fn text(tr: &'static str, en: &'static str) -> &'static str {
    if locale::language() == "tr" {
        tr
    } else {
        locale::translate_english(en)
    }
}

// Keep the original line intact; the extracted time is only a display column.
fn timestamp(line: &str) -> Option<&str> {
    let bytes = line.as_bytes();
    for i in 0..bytes.len().saturating_sub(7) {
        if bytes[i..i + 2].iter().all(u8::is_ascii_digit)
            && bytes[i + 2] == b':'
            && bytes[i + 3..i + 5].iter().all(u8::is_ascii_digit)
            && bytes[i + 5] == b':'
            && bytes[i + 6..i + 8].iter().all(u8::is_ascii_digit)
        {
            return line.get(i..i + 8);
        }
    }
    None
}

fn severity(line: &str) -> Color32 {
    let lower = line.to_lowercase();
    if ["error", "fail", "hata", "başarısız"]
        .iter()
        .any(|s| lower.contains(s))
    {
        Color32::from_rgb(235, 105, 105)
    } else if ["warn", "uyarı"].iter().any(|s| lower.contains(s)) {
        Color32::from_rgb(235, 184, 92)
    } else {
        INK
    }
}

fn order_key(line: &str, now: u64) -> u64 {
    let Some(clock) = timestamp(line) else {
        return now;
    };
    let h = clock[0..2].parse::<u64>().unwrap_or(0);
    let m = clock[3..5].parse::<u64>().unwrap_or(0);
    let s = clock[6..8].parse::<u64>().unwrap_or(0);
    let seconds = h * 3600 + m * 60 + s;
    for date in line.as_bytes().windows(10) {
        if date[4] != b'-' || date[7] != b'-' {
            continue;
        }
        let Ok(date) = std::str::from_utf8(date) else {
            continue;
        };
        let (Ok(mut year), Ok(month), Ok(day)) = (
            date[0..4].parse::<i64>(),
            date[5..7].parse::<i64>(),
            date[8..10].parse::<i64>(),
        ) else {
            continue;
        };
        if !(1..=12).contains(&month) || !(1..=31).contains(&day) {
            continue;
        }
        year -= i64::from(month <= 2);
        let era = year.div_euclid(400);
        let y = year - era * 400;
        let shifted_month = month + if month > 2 { -3 } else { 9 };
        let days = era * 146097 + y * 365 + y / 4 - y / 100 + (153 * shifted_month + 2) / 5 + day
            - 1
            - 719468;
        return (days.max(0) as u64) * 86400 + seconds;
    }
    let candidate = now / 86400 * 86400 + seconds;
    if candidate > now + 60 {
        candidate.saturating_sub(86400)
    } else {
        candidate
    }
}

impl LogView {
    pub fn render(
        &mut self,
        ui: &mut egui::Ui,
        local: &VecDeque<String>,
        backend: &VecDeque<String>,
        error: &str,
        detailed: &mut bool,
        busy: bool,
    ) -> Actions {
        let mut actions = Actions::default();
        ui.horizontal_wrapped(|ui| {
            ui.vertical(|ui| {
                ui.label(RichText::new(text("Kayıtlar", "Logs")).size(25.).strong());
                ui.label(
                    RichText::new(text(
                        "En yeni kayıtlar önce gösterilir.",
                        "Newest entries are shown first.",
                    ))
                    .color(SUB),
                );
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui
                    .add_enabled(!busy, egui::Button::new(text("Temizle", "Clear")))
                    .clicked()
                {
                    actions.clear = true;
                }
                if ui
                    .add_enabled(!busy, egui::Button::new(text("Yenile", "Refresh")))
                    .clicked()
                {
                    actions.refresh = true;
                }
                ui.add(
                    egui::TextEdit::singleline(&mut self.filter_string)
                        .hint_text(text("Kayıtlarda ara", "Search logs"))
                        .desired_width(160.),
                );
            });
        });
        ui.add_space(12.);
        egui::Frame::new()
            .fill(PANEL)
            .stroke(Stroke::new(1_f32, LINE))
            .corner_radius(10)
            .inner_margin(12)
            .show(ui, |ui| {
                ui.horizontal_wrapped(|ui| {
                    ui.vertical(|ui| {
                        ui.label(
                            RichText::new(text("Ayrıntılı kayıt", "Detailed logging")).strong(),
                        );
                        ui.label(
                            RichText::new(text(
                                "Değişiklik bir sonraki bağlantıda uygulanır.",
                                "Changes apply on the next connection.",
                            ))
                            .color(SUB)
                            .size(12.),
                        );
                    });
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        actions.detailed_changed = ui
                            .add_enabled_ui(!busy, |ui| widgets::toggle(ui, detailed))
                            .inner
                            .changed();
                    });
                });
            });
        if !error.is_empty() {
            ui.label(RichText::new(error).color(Color32::from_rgb(235, 105, 105)));
        }
        ui.add_space(12.);
        let filter = self.filter_string.to_lowercase();
        // Only retain references to the most recent bounded input, never copies of log lines.
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        let mut entries: Vec<_> = local
            .iter()
            .rev()
            .take(100)
            .map(|line| (line, true))
            .chain(backend.iter().rev().take(100).map(|line| (line, false)))
            .collect();
        entries.sort_by_cached_key(|(line, _)| std::cmp::Reverse(order_key(line, now)));
        egui::Frame::new()
            .fill(BG)
            .stroke(Stroke::new(1_f32, LINE))
            .corner_radius(10)
            .inner_margin(10)
            .show(ui, |ui| {
                egui::ScrollArea::vertical()
                    .id_salt("log_view_rows")
                    .max_height(480.)
                    .show(ui, |ui| {
                        for (line, take_local) in entries {
                            if !filter.is_empty() && !line.to_lowercase().contains(&filter) {
                                continue;
                            }
                            let source = if line.contains("[Motor]") {
                                "Motor"
                            } else if line.contains("[Hizmet]") {
                                text("Hizmet", "Service")
                            } else if take_local {
                                "CLI"
                            } else {
                                text("Hizmet", "Service")
                            };
                            let color = severity(line);
                            ui.horizontal(|ui| {
                                let width = ui.available_width();
                                if width >= 260. {
                                    ui.add_sized(
                                        [70., 18.],
                                        egui::Label::new(
                                            RichText::new(timestamp(line).unwrap_or("—"))
                                                .monospace()
                                                .color(SUB),
                                        )
                                        .truncate(),
                                    );
                                    ui.add_sized(
                                        [62., 18.],
                                        egui::Label::new(
                                            RichText::new(source).monospace().color(SUB),
                                        )
                                        .truncate(),
                                    );
                                }
                                ui.add(
                                    egui::Label::new(RichText::new(line).monospace().color(color))
                                        .wrap(),
                                );
                            });
                        }
                    });
            });
        actions
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn dated_logs_sort_across_midnight_and_sources() {
        let old = order_key("[Motor] 2026-11-14T23:59:59Z old", 0);
        let new = order_key("[Hizmet] 2026-11-15T00:00:01Z new", 0);
        assert_eq!(new - old, 2);
        assert_eq!(order_key("00:00:00 local", new), new - 1);
    }
}
