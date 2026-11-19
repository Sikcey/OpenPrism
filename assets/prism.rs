use eframe::egui::{self, Color32, Pos2, Rect, Shape, Stroke, Vec2};

pub fn window_icon() -> egui::IconData {
    let image = image::load_from_memory(include_bytes!("icon.png"))
        .expect("embedded OpenPrism icon")
        .into_rgba8();
    let (width, height) = image.dimensions();
    egui::IconData {
        rgba: image.into_raw(),
        width,
        height,
    }
}

pub fn wordmark(font_id: egui::FontId) -> egui::text::LayoutJob {
    let mut wordmark = egui::text::LayoutJob::default();
    for (index, letter) in "OpenPrism".chars().enumerate() {
        wordmark.append(
            letter.encode_utf8(&mut [0; 4]),
            0.,
            egui::TextFormat {
                font_id: font_id.clone(),
                color: gradient(index as f32 / 8., 1.),
                ..Default::default()
            },
        );
    }
    wordmark
}

pub fn gradient_colors() -> [Color32; 4] {
    [
        Color32::from_rgb(120, 228, 234),
        Color32::from_rgb(173, 145, 236),
        Color32::from_rgb(239, 171, 208),
        Color32::from_rgb(242, 198, 127),
    ]
}

pub fn gradient(t: f32, brightness: f32) -> Color32 {
    let colors = gradient_colors();
    let position = t.clamp(0., 1.) * 3.;
    let index = (position as usize).min(2);
    let [a, b] = [colors[index], colors[index + 1]];
    let channel = |a: u8, b: u8| {
        (egui::lerp(a as f32..=b as f32, position - index as f32) * brightness).min(255.) as u8
    };
    Color32::from_rgb(
        channel(a.r(), b.r()),
        channel(a.g(), b.g()),
        channel(a.b(), b.b()),
    )
}

pub fn paint_gradient(
    painter: &egui::Painter,
    shapes: impl IntoIterator<Item = Shape>,
    color_at: impl Fn(Pos2) -> Color32,
) {
    let clip_rect = painter.clip_rect();
    let shapes = shapes
        .into_iter()
        .map(|shape| egui::epaint::ClippedShape { clip_rect, shape })
        .collect();
    for primitive in painter.ctx().tessellate(shapes, painter.pixels_per_point()) {
        if let egui::epaint::Primitive::Mesh(mut mesh) = primitive.primitive {
            for vertex in &mut mesh.vertices {
                // The tessellator supplies the transparent feather along the outline.
                vertex.color = color_at(vertex.pos).gamma_multiply(vertex.color.a() as f32 / 255.);
            }
            painter.add(Shape::mesh(mesh));
        }
    }
}

pub fn rounded_gradient(painter: &egui::Painter, rect: Rect, radius: f32, brightness: f32) {
    if !rect.is_positive() {
        return;
    }
    let radius = radius.min(rect.width() / 2.).min(rect.height() / 2.);
    let ctx = painter.ctx();
    let id = egui::Id::new(("openprism-gradient", brightness.to_bits()));
    let texture = ctx
        .data(|data| data.get_temp::<egui::TextureHandle>(id))
        .unwrap_or_else(|| {
            let pixels = (0..256)
                .map(|index| gradient(index as f32 / 255., brightness))
                .collect();
            let texture = ctx.load_texture(
                "OpenPrism gradient",
                egui::ColorImage::new([256, 1], pixels),
                egui::TextureOptions::LINEAR,
            );
            ctx.data_mut(|data| data.insert_temp(id, texture.clone()));
            texture
        });
    let slope = 0.364;
    let span = rect.width() + rect.height() * slope;
    let shape = egui::epaint::ClippedShape {
        clip_rect: painter.clip_rect(),
        shape: Shape::rect_filled(rect, radius, Color32::WHITE),
    };
    for primitive in ctx.tessellate(vec![shape], painter.pixels_per_point()) {
        if let egui::epaint::Primitive::Mesh(mut mesh) = primitive.primitive {
            mesh.texture_id = texture.id();
            for vertex in &mut mesh.vertices {
                let offset = vertex.pos - rect.left_top();
                let t = ((offset.x + offset.y * slope) / span).clamp(0., 1.);
                vertex.uv = Pos2::new((t * 255. + 0.5) / 256., 0.5);
            }
            painter.add(Shape::mesh(mesh));
        }
    }
}

pub fn prism(painter: &egui::Painter, rect: Rect, width: f32) {
    let point = |x: f32, y: f32| rect.min + rect.size() * Vec2::new(x / 36., y / 36.);
    let apex = point(18., 3.);
    let right = point(33., 29.);
    let left = point(3., 29.);
    let center = point(20.2, 19.2);
    let stroke = Stroke::new(width * 0.75, Color32::WHITE);
    let shapes = [
        Shape::closed_line(vec![apex, right, left], Stroke::new(width, Color32::WHITE)),
        Shape::line(vec![apex, center, left], stroke),
        Shape::line_segment([center, right], stroke),
        Shape::line_segment([point(10.5, 16.), point(25.5, 16.)], stroke),
    ];
    paint_gradient(painter, shapes, |position| {
        let offset = position - point(3., 5.);
        let axis = point(33., 31.) - point(3., 5.);
        let position = (offset.dot(axis) / axis.length_sq()).clamp(0., 1.);
        let stops: [(f32, u32); 4] = [
            (0., 0x8de7e7),
            (0.5, 0xb69be6),
            (0.75, 0xe1a6cf),
            (1., 0xefc982),
        ];
        let index = if position < 0.5 {
            0
        } else if position < 0.75 {
            1
        } else {
            2
        };
        let [(start, a), (end, b)] = [stops[index], stops[index + 1]];
        let t = (position - start) / (end - start);
        let channel = |shift| {
            egui::lerp(
                ((a >> shift) & 255u32) as f32..=((b >> shift) & 255u32) as f32,
                t,
            ) as u8
        };
        Color32::from_rgb(channel(16), channel(8), channel(0))
    });
}
