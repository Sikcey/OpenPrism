use crate::{ACCENT, INK, LINE, PANEL, SUB};
use eframe::egui::{self, Color32, FontId, Pos2, Sense, Stroke, Vec2};

pub use crate::branding::gradient_colors;

pub fn removal_button(ui: &mut egui::Ui, text: &str, enabled: bool) -> egui::Response {
    ui.add_enabled(enabled, egui::Button::new(crate::locale::tr(text)))
}

pub fn icon(ui: &mut egui::Ui, kind: &str, color: Color32, size: f32) -> egui::Response {
    let (r, response) = ui.allocate_exact_size(Vec2::splat(size), Sense::hover());
    if kind == "prism" {
        crate::branding::prism(ui.painter(), r, 1.6);
    } else {
        draw_icon(ui.painter(), r.center(), kind, color);
    }
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
pub fn gradient_fill(p: &egui::Painter, rect: egui::Rect, radius: f32, alpha: f32) {
    crate::branding::rounded_gradient(p, rect, radius, alpha);
}
pub fn power_gradient(p: &egui::Painter, center: Pos2, radius: f32, hovered: bool) {
    let colors = gradient_colors();
    let color_at = |x: f32, y: f32| {
        let t = ((x + y + 1.4) / 2.8).clamp(0., 1.) * 3.;
        let j = (t as usize).min(2);
        let f = t - j as f32;
        let channel = |get: fn(Color32) -> u8| {
            let value = get(colors[j]) as f32 * (1. - f) + get(colors[j + 1]) as f32 * f;
            (value * if hovered { 1.12 } else { 1. }).min(255.) as u8
        };
        Color32::from_rgb(channel(|c| c.r()), channel(|c| c.g()), channel(|c| c.b()))
    };
    let mut mesh = egui::Mesh::default();
    mesh.colored_vertex(center, color_at(0., 0.));
    for ring in 1..=33u32 {
        let fraction = ring.min(32) as f32 / 32.;
        for i in 0..192u32 {
            let angle = std::f32::consts::TAU * i as f32 / 192.;
            let (y, x) = angle.sin_cos();
            let distance = if ring == 33 {
                radius + 0.5
            } else {
                (radius - 0.5) * fraction
            };
            mesh.colored_vertex(
                center + Vec2::new(x, y) * distance,
                if ring == 33 {
                    Color32::TRANSPARENT
                } else {
                    color_at(x * fraction, y * fraction)
                },
            );
            let a = 1 + (ring - 1) * 192 + i;
            let b = 1 + (ring - 1) * 192 + (i + 1) % 192;
            if ring == 1 {
                mesh.add_triangle(0, a, b);
            } else {
                mesh.add_triangle(a - 192, a, b);
                mesh.add_triangle(a - 192, b, b - 192);
            }
        }
    }
    p.add(egui::Shape::mesh(mesh));
}
pub fn nav(ui: &mut egui::Ui, kind: &str, text: &str, selected: bool) -> bool {
    let (r, response) =
        ui.allocate_exact_size(Vec2::new(ui.available_width(), 52.), Sense::click());
    if selected {
        gradient_fill(ui.painter(), r, 10., 0.25);
        let clip = ui.painter().with_clip_rect(egui::Rect::from_min_max(
            r.min,
            Pos2::new(r.left() + 9., r.bottom()),
        ));
        clip.rect_stroke(
            r.shrink(1.),
            10.,
            Stroke::new(2_f32, ACCENT),
            egui::StrokeKind::Inside,
        );
    } else if response.hovered() {
        gradient_fill(ui.painter(), r, 10., 0.16);
    }
    let color = if selected { INK } else { SUB };
    draw_icon(
        ui.painter(),
        r.left_center() + Vec2::new(24., 0.),
        kind,
        color,
    );
    ui.painter().text(
        r.left_center() + Vec2::new(50., 0.),
        egui::Align2::LEFT_CENTER,
        crate::locale::tr(text),
        FontId::proportional(15.),
        color,
    );
    response.clicked()
}
pub fn segments(ui: &mut egui::Ui, id: &str, labels: &[&str], selected: usize) -> Option<usize> {
    let mut clicked = None;
    egui::Frame::new()
        .fill(Color32::from_rgb(23, 24, 30))
        .stroke(Stroke::new(1_f32, LINE))
        .corner_radius(10)
        .inner_margin(5)
        .show(ui, |ui| {
            let width =
                (ui.available_width() - 4. * (labels.len() - 1) as f32) / labels.len() as f32;
            ui.spacing_mut().item_spacing.x = 4.;
            ui.horizontal(|ui| {
                for (i, label) in labels.iter().enumerate() {
                    let (r, response) =
                        ui.allocate_exact_size(Vec2::new(width, 46.), Sense::click());
                    if i == selected {
                        gradient_fill(ui.painter(), r, 8., 0.23);
                    }
                    ui.painter().text(
                        r.center(),
                        egui::Align2::CENTER_CENTER,
                        crate::locale::tr(label),
                        FontId::new(14., egui::FontFamily::Name("bold".into())),
                        if i == selected { INK } else { SUB },
                    );
                    if response.clicked() {
                        clicked = Some(i);
                    }
                }
            });
        });
    let _ = id;
    clicked
}
pub fn toggle(ui: &mut egui::Ui, checked: &mut bool) -> egui::Response {
    let (r, mut response) = ui.allocate_exact_size(Vec2::new(46., 26.), Sense::click());
    if response.clicked() {
        *checked = !*checked;
        response.mark_changed();
    }
    ui.painter().rect_filled(
        r.shrink2(Vec2::new(0., 1.)),
        14.,
        if *checked { ACCENT } else { LINE },
    );
    ui.painter().circle_filled(
        Pos2::new(
            if *checked {
                r.right() - 13.
            } else {
                r.left() + 13.
            },
            r.center().y,
        ),
        10.,
        Color32::WHITE,
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
