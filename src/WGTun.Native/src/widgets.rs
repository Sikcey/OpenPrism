use crate::{ACCENT, BG, INK, LINE, PANEL, SOFT, SUB};
use eframe::egui::{self, Color32, FontId, Pos2, RichText, Sense, Stroke, Vec2};

pub fn icon(ui: &mut egui::Ui, kind: &str, color: Color32, size: f32) -> egui::Response {
    let (r, response) = ui.allocate_exact_size(Vec2::splat(size), Sense::hover());
    draw_icon(ui.painter(), r.center(), kind, color);
    response
}
pub fn draw_icon(p: &egui::Painter, c: Pos2, kind: &str, color: Color32) {
    let stroke = Stroke::new(1.4_f32, color);
    let v = |x, y| c + Vec2::new(x, y);
    match kind {
        "connection" => {
            p.circle_stroke(c, 6., stroke);
            p.circle_stroke(c, 2., stroke);
        }
        "profiles" => {
            p.rect_stroke(
                egui::Rect::from_center_size(c, Vec2::new(12., 14.)),
                2.,
                stroke,
                egui::StrokeKind::Inside,
            );
            for y in [-3., 1., 5.] {
                p.line_segment([v(-3., y), v(3., y)], stroke);
            }
        }
        "services" => {
            for y in [-5., 0., 5.] {
                p.line_segment([v(-6., y), v(6., y)], stroke);
                p.circle_filled(v(-3., y), 1.5, color);
            }
        }
        "logs" => {
            p.rect_stroke(
                egui::Rect::from_center_size(c, Vec2::new(12., 14.)),
                2.,
                stroke,
                egui::StrokeKind::Inside,
            );
            for y in [-3., 0., 3.] {
                p.line_segment([v(-3., y), v(3., y)], stroke);
            }
        }
        "settings" => {
            p.circle_stroke(c, 4., stroke);
            for i in 0..8 {
                let a = i as f32 * std::f32::consts::TAU / 8.;
                let d = Vec2::angled(a);
                p.line_segment([c + d * 6., c + d * 8.], stroke);
            }
        }
        "pencil" => {
            // Compact outlined edit icon, with a thin shaft and a squared nib.
            p.line_segment([v(-6., -1.), v(-6., 6.)], stroke);
            p.line_segment([v(-6., 6.), v(2., 6.)], stroke);
            p.line_segment([v(2., 6.), v(2., 3.)], stroke);
            p.add(egui::Shape::closed_line(
                vec![v(-2., 2.), v(-1., -1.), v(5., -7.), v(8., -4.), v(2., 2.)],
                stroke,
            ));
            p.line_segment([v(3., -5.), v(6., -2.)], stroke);
        }
        "arrow" => {
            p.line_segment([v(-4., 4.), v(5., -5.)], stroke);
            p.line_segment([v(-2., -5.), v(5., -5.)], stroke);
            p.line_segment([v(5., -5.), v(5., 2.)], stroke);
        }
        "cloud" => {
            p.circle_stroke(v(-3., 0.), 5., stroke);
            p.circle_stroke(v(2., -3.), 6., stroke);
            p.circle_stroke(v(7., 1.), 4., stroke);
            p.rect_filled(egui::Rect::from_min_max(v(-7., 0.), v(10., 6.)), 2., PANEL);
            p.line_segment([v(-6., 5.), v(8., 5.)], stroke);
        }
        _ => {
            p.circle_stroke(c, 6., stroke);
            p.line_segment([v(-4., 4.), v(4., -4.)], stroke);
        }
    }
}
pub fn nav(ui: &mut egui::Ui, kind: &str, text: &str, selected: bool) -> bool {
    let (r, response) =
        ui.allocate_exact_size(Vec2::new(ui.available_width(), 37.), Sense::click());
    if selected || response.hovered() {
        ui.painter()
            .rect_filled(r, 8., if selected { SOFT } else { BG });
    }
    let color = if selected { INK } else { SUB };
    draw_icon(
        ui.painter(),
        r.left_center() + Vec2::new(17., 0.),
        kind,
        color,
    );
    ui.painter().text(
        r.left_center() + Vec2::new(36., 0.),
        egui::Align2::LEFT_CENTER,
        crate::locale::tr(text),
        FontId::proportional(13.),
        color,
    );
    response.clicked()
}
pub fn segments(ui: &mut egui::Ui, id: &str, labels: &[&str], selected: usize) -> Option<usize> {
    let mut clicked = None;
    egui::Frame::new()
        .fill(PANEL)
        .stroke(Stroke::new(1.0_f32, LINE))
        .corner_radius(9)
        .inner_margin(4)
        .show(ui, |ui| {
            let width =
                (ui.available_width() - 4. * (labels.len() - 1) as f32) / labels.len() as f32;
            ui.spacing_mut().item_spacing.x = 4.;
            ui.horizontal(|ui| {
                for (i, label) in labels.iter().enumerate() {
                    if ui
                        .push_id((id, i), |ui| {
                            ui.add_sized(
                                [width, 30.],
                                egui::Button::new(
                                    RichText::new(crate::locale::tr(label))
                                        .size(11.)
                                        .color(if i == selected { INK } else { SUB }),
                                )
                                .fill(if i == selected { SOFT } else { PANEL })
                                .stroke(Stroke::NONE)
                                .corner_radius(6),
                            )
                        })
                        .inner
                        .clicked()
                    {
                        clicked = Some(i);
                    }
                }
            });
        });
    clicked
}
pub fn toggle(ui: &mut egui::Ui, checked: &mut bool) -> egui::Response {
    let (r, mut response) = ui.allocate_exact_size(Vec2::new(28., 18.), Sense::click());
    if response.clicked() {
        *checked = !*checked;
        response.mark_changed();
    }
    ui.painter().rect_filled(
        r.shrink2(Vec2::new(0., 1.)),
        9.,
        if *checked { ACCENT } else { LINE },
    );
    ui.painter().circle_filled(
        Pos2::new(
            if *checked {
                r.right() - 8.
            } else {
                r.left() + 8.
            },
            r.center().y,
        ),
        5.,
        PANEL,
    );
    response.widget_info(|| {
        egui::WidgetInfo::selected(
            egui::WidgetType::Checkbox,
            ui.is_enabled(),
            *checked,
            "Kural seçimi",
        )
    });
    response
}
