mod backend;
#[path = "../../assets/prism.rs"]
mod branding;
use branding::prism;

use backend::{Event, Plan};
use eframe::egui::{
    self, Align, Color32, CornerRadius, FontId, Frame, Layout, Pos2, Rect, RichText, Sense, Stroke,
    StrokeKind, Vec2,
};
use std::{
    path::PathBuf,
    sync::mpsc::{self, Receiver},
    time::Duration,
};

const STEPS: [&str; 6] = [
    "Hoş geldin",
    "Konum",
    "Bileşenler",
    "Uygulamalar",
    "Kurulum",
    "Tamamlandı",
];
const TEXT: Color32 = Color32::from_rgb(243, 241, 235);
const MUTED: Color32 = Color32::from_rgb(146, 148, 159);
const CYAN: Color32 = Color32::from_rgb(159, 220, 221);
const LINE: Color32 = Color32::from_rgb(41, 43, 51);

fn rgb(value: u32) -> Color32 {
    Color32::from_rgb((value >> 16) as u8, (value >> 8) as u8, value as u8)
}

fn gradient_rect(painter: &egui::Painter, rect: Rect, radius: f32, bright: bool) {
    branding::rounded_gradient(painter, rect, radius, if bright { 1.12 } else { 1. });
}

fn checkmark(painter: &egui::Painter, center: Pos2, size: f32) {
    painter.add(egui::Shape::line(
        vec![
            center + Vec2::new(-0.40, 0.) * size,
            center + Vec2::new(-0.10, 0.30) * size,
            center + Vec2::new(0.42, -0.32) * size,
        ],
        Stroke::new(size / 7., CYAN),
    ));
}

fn label(ui: &mut egui::Ui, text: impl Into<String>, size: f32, color: Color32) {
    ui.label(RichText::new(text.into()).size(size).color(color));
}

fn semibold(size: f32) -> FontId {
    FontId::new(size, egui::FontFamily::Name("semibold".into()))
}

fn disk_size(bytes: Option<u64>) -> String {
    match bytes {
        Some(bytes) if bytes >= 1024_u64.pow(3) => {
            format!("{:.1} GB", bytes as f64 / 1024_f64.powi(3)).replace('.', ",")
        }
        Some(bytes) => format!("{:.0} MB", bytes as f64 / 1024_f64.powi(2)),
        None => "—".into(),
    }
}

fn button(ui: &mut egui::Ui, title: &str, primary: bool, enabled: bool) -> bool {
    let text = ui.painter().layout_no_wrap(
        title.to_owned(),
        semibold(13.),
        if primary { rgb(0x17151a) } else { TEXT },
    );
    let (rect, response) = ui.allocate_exact_size(
        Vec2::new(text.size().x + if primary { 44. } else { 34. }, 38.),
        if enabled {
            Sense::click()
        } else {
            Sense::hover()
        },
    );
    let mut painter = ui.painter().clone();
    if !enabled {
        painter.set_opacity(0.45);
    }
    if primary {
        gradient_rect(&painter, rect, 7., enabled && response.hovered());
    } else {
        painter.rect(
            rect,
            7,
            rgb(if enabled && response.hovered() {
                0x25232d
            } else {
                0x1c1d23
            }),
            Stroke::new(
                1_f32,
                rgb(if enabled && response.hovered() {
                    0xa99dc6
                } else {
                    0x393b45
                }),
            ),
            StrokeKind::Inside,
        );
    }
    if response.has_focus() {
        painter.rect_stroke(
            rect.expand(2.),
            8,
            Stroke::new(2_f32, CYAN),
            StrokeKind::Outside,
        );
    }
    painter.galley(rect.center() - text.size() / 2., text, TEXT);
    response.widget_info(|| egui::WidgetInfo::labeled(egui::WidgetType::Button, enabled, title));
    if enabled && response.hovered() {
        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
    }
    response.clicked()
}

fn choice(
    ui: &mut egui::Ui,
    checked: &mut bool,
    name: &str,
    detail: &str,
    tag: &str,
    enabled: bool,
) {
    let width = ui.available_width();
    Frame::new()
        .fill(rgb(if *checked { 0x201f28 } else { 0x17181d }))
        .stroke(Stroke::new(
            1_f32,
            rgb(if *checked { 0x817c9a } else { 0x30313a }),
        ))
        .corner_radius(9)
        .inner_margin(13)
        .show(ui, |ui| {
            ui.set_min_width(width - 28.);
            ui.horizontal_top(|ui| {
                ui.add_enabled(enabled, egui::Checkbox::without_text(checked));
                let copy = ui
                    .vertical(|ui| {
                        let reserve = if tag.is_empty() { 0. } else { 90. };
                        ui.set_width((ui.available_width() - reserve).max(1.));
                        label(ui, name, 13., TEXT);
                        label(ui, detail, 12., MUTED);
                    })
                    .response;
                if enabled
                    && ui
                        .interact(copy.rect, copy.id.with("toggle"), Sense::click())
                        .clicked()
                {
                    *checked = !*checked;
                }
                if !tag.is_empty() {
                    Frame::new()
                        .stroke(Stroke::new(1_f32, rgb(0x41424b)))
                        .corner_radius(5)
                        .inner_margin(egui::Margin::symmetric(6, 2))
                        .show(ui, |ui| label(ui, tag, 10., MUTED));
                }
            });
        });
    ui.add_space(5.);
}

struct Installer {
    plan: Plan,
    registered: Option<PathBuf>,
    removing: bool,
    uninstall_only: bool,
    path: String,
    payload_size: Option<u64>,
    free_space: Option<u64>,
    step: usize,
    worker: Option<Receiver<Event>>,
    scanning: bool,
    scanned: bool,
    account: Option<bool>,
    progress: Option<(f32, &'static str)>,
    error: Option<String>,
    launch: bool,
}

impl Installer {
    fn new(package: PathBuf, uninstall_only: bool) -> Self {
        let path = r"%LOCALAPPDATA%\OpenPrism".to_owned();
        let free_space = backend::free_bytes(&path);
        let payload_size = backend::payload_bytes(&package);
        Self {
            registered: backend::installed_location(),
            removing: uninstall_only,
            uninstall_only,
            plan: Plan {
                package,
                location: PathBuf::new(),
                byedpi: false,
                zapret: false,
                startup: false,
                shortcut: true,
                consent: false,
                apps: Vec::new(),
            },
            path,
            payload_size,
            free_space,
            step: 0,
            worker: None,
            scanning: false,
            scanned: false,
            account: None,
            progress: None,
            error: None,
            launch: true,
        }
    }

    fn start(&mut self, scan: bool) {
        let (tx, rx) = mpsc::channel();
        let plan = self.plan.clone();
        self.error = None;
        self.scanning = scan;
        self.worker = Some(rx);
        if !scan {
            self.progress = Some((0., "Kurulum hazırlanıyor…"));
        }
        std::thread::spawn(move || {
            if scan {
                backend::scan(plan, tx)
            } else {
                backend::install(plan, tx)
            }
        });
    }

    fn receive(&mut self, ctx: &egui::Context) {
        while let Some(rx) = &self.worker {
            let event = match rx.try_recv() {
                Ok(event) => event,
                Err(mpsc::TryRecvError::Empty) => {
                    ctx.request_repaint_after(Duration::from_millis(40));
                    break;
                }
                Err(mpsc::TryRecvError::Disconnected) => {
                    Event::Failed("Kurulum işlemi beklenmedik şekilde sona erdi.".into())
                }
            };
            match event {
                Event::Progress(value, message) => self.progress = Some((value, message)),
                Event::Scanned(mut apps, account) => {
                    for app in &mut apps {
                        app.selected = self.plan.apps.iter().any(|old| {
                            old.selected && old.value["folderPath"] == app.value["folderPath"]
                        });
                    }
                    self.plan.apps = apps;
                    self.account = Some(account);
                    self.scanned = true;
                    self.worker = None;
                }
                Event::Installed => {
                    self.step = 5;
                    self.worker = None;
                }
                Event::Removed => {
                    self.step = 2;
                    self.progress = None;
                    self.worker = None;
                }
                Event::Failed(error) => {
                    self.error = Some(error);
                    self.worker = None;
                    self.progress = None;
                }
                Event::Folder(path) => {
                    self.path = path.display().to_string();
                    self.free_space = backend::free_bytes(&self.path);
                    self.worker = None;
                }
                Event::FolderCancelled => self.worker = None,
            }
        }
    }

    fn next(&mut self, ctx: &egui::Context) {
        self.error = None;
        match self.step {
            1 => match backend::location(&self.path, &self.plan.package) {
                Ok(path) => {
                    self.plan.location = path;
                    self.step += 1;
                }
                Err(error) => self.error = Some(error),
            },
            4 => self.start(false),
            5 => {
                if self.launch
                    && std::process::Command::new(self.plan.location.join("WGTun.exe"))
                        .current_dir(&self.plan.location)
                        .spawn()
                        .is_err()
                {
                    self.error = Some("OpenPrism başlatılamadı.".into());
                    return;
                }
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
            _ => self.step += 1,
        }
    }

    fn start_uninstall(&mut self, ctx: &egui::Context) {
        let Some(location) = self.registered.clone() else {
            return;
        };
        self.error = None;
        if self.handoff_if_installed(ctx, &location) {
            return;
        }
        let (tx, rx) = mpsc::channel();
        self.step = 1;
        self.worker = Some(rx);
        std::thread::spawn(move || backend::uninstall(location, tx));
    }

    fn handoff_if_installed(&mut self, ctx: &egui::Context, location: &std::path::Path) -> bool {
        if !same_directory(&self.plan.package, location) {
            return false;
        }
        match backend::handoff_uninstall(location) {
            Ok(()) => ctx.send_viewport_cmd(egui::ViewportCommand::Close),
            Err(error) => self.error = Some(error),
        }
        true
    }

    fn uninstall_content(&self, ui: &mut egui::Ui) {
        let title = match self.step {
            2 => "OpenPrism kaldırıldı.",
            1 if self.error.is_some() => "Kaldırma tamamlanamadı",
            1 => "OpenPrism kaldırılıyor",
            _ => "OpenPrism’i kaldır",
        };
        label(ui, "OPENPRISM KALDIRMA", 11., CYAN);
        ui.add_space(9.);
        if self.step == 2 {
            let (rect, _) = ui.allocate_exact_size(Vec2::splat(58.), Sense::hover());
            ui.painter()
                .circle(rect.center(), 28., rgb(0x252937), Stroke::new(1_f32, CYAN));
            checkmark(ui.painter(), rect.center(), 27.);
            ui.add_space(13.);
        }
        ui.label(RichText::new(title).font(semibold(29.)).color(TEXT));
        ui.add_space(7.);
        match self.step {
            2 => label(
                ui,
                "Değiştirilmiş ve tanınmayan dosyalar korundu.",
                14.,
                MUTED,
            ),
            1 if self.error.is_none() => {
                let message = self
                    .progress
                    .map(|(_, message)| message)
                    .unwrap_or("Kaldırma hazırlanıyor…");
                label(ui, message, 14., MUTED);
                ui.add_space(22.);
                ui.horizontal(|ui| {
                    ui.spinner();
                    label(ui, "Windows yönetici onayı isteyebilir.", 12., MUTED);
                });
            }
            0 => {
                if let Some(location) = &self.registered {
                    label(ui, "Windows hizmeti, OpenPrism ayarları ve bu bilgisayarda saklanan WARP hesap bilgileri kaldırılacak.", 14., MUTED);
                    ui.add_space(24.);
                    label(ui, "Kurulum konumu", 12., CYAN);
                    label(ui, location.display().to_string(), 13., TEXT);
                } else {
                    label(
                        ui,
                        "Bu Windows kullanıcısı için kayıtlı bir OpenPrism kurulumu bulunamadı.",
                        14.,
                        MUTED,
                    );
                }
            }
            _ => {}
        }
        self.error_card(ui);
    }

    fn uninstall_footer(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        if self.worker.is_some() {
            return;
        }
        if self.step == 2 || self.registered.is_none() {
            if button(ui, "Kapat", true, true) {
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
            return;
        }
        let action = if self.error.is_some() {
            "Yeniden dene"
        } else {
            "Kaldır"
        };
        if button(ui, action, true, true) {
            self.start_uninstall(ctx);
        }
        if button(ui, "Vazgeç", false, self.worker.is_none()) {
            if self.uninstall_only {
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            } else {
                self.removing = false;
                self.step = 0;
                self.error = None;
            }
        }
    }

    fn content(&mut self, ui: &mut egui::Ui) {
        let (eyebrow, title, intro) = match self.step {
            0 => ("OPENPRISM’E HOŞ GELDİN", "Bağlantın, kontrolünde.", "OpenPrism’i birkaç adımda kur ve bağlantını sana uygun şekilde yönetmeye başla."),
            1 => ("ADIM 2 / 6", "Kurulum konumu", "OpenPrism’in kaydedileceği klasörü seç."),
            2 => ("ADIM 3 / 6", "Kurulumu özelleştir", "İhtiyacın olan bileşenleri seç. Bunları daha sonra da yönetebilirsin."),
            3 => ("ADIM 4 / 6 · İSTEĞE BAĞLI", "Uygulamaları otomatik algıla", "Bulunan uygulamalardan yönlendirmek istediklerini seç."),
            4 if self.progress.is_some() => ("ADIM 5 / 6", "OpenPrism kuruluyor", "Kurulum sırasında pencereyi açık tut."),
            4 => ("ADIM 5 / 6", "Kuruluma hazır", "Seçimlerini gözden geçir, ardından kurulumu başlat."),
            _ => ("KURULUM TAMAMLANDI", "Her şey hazır.", "OpenPrism kurulumu tamamlandı. Şimdi bağlantını kendi ihtiyaçlarına göre ayarlayabilirsin."),
        };
        label(ui, eyebrow, 11., CYAN);
        if self.step == 5 {
            ui.add_space(27.);
            let (r, _) = ui.allocate_exact_size(Vec2::splat(58.), Sense::hover());
            ui.painter()
                .circle(r.center(), 28., rgb(0x252937), Stroke::new(1_f32, CYAN));
            checkmark(ui.painter(), r.center(), 27.);
        }
        ui.add_space(9.);
        ui.label(RichText::new(title).font(semibold(29.)).color(TEXT));
        ui.add_space(4.);
        ui.vertical(|ui| {
            ui.set_max_width(470.);
            label(ui, intro, 14., MUTED);
        });
        ui.add_space(25.);
        match self.step {
            0 => {
                let (r, _) =
                    ui.allocate_exact_size(Vec2::new(ui.available_width(), 150.), Sense::hover());
                ui.painter().rect(
                    r,
                    12,
                    rgb(0x171a20),
                    Stroke::new(1_f32, LINE),
                    StrokeKind::Inside,
                );
                let mut mesh = egui::Mesh::default();
                let columns = 32;
                for y in 0..=16 {
                    for x in 0..=columns {
                        let u = x as f32 / columns as f32;
                        let v = y as f32 / 16.;
                        let strength =
                            (1. - ((u - 0.5).powi(2) * 7. + (1. - v).powi(2) * 1.9).sqrt()).max(0.);
                        let inset = 11. * (1. - (v.min(1. - v) * 14.).min(1.));
                        let point = Pos2::new(
                            egui::lerp(r.left() + inset..=r.right() - inset, u),
                            egui::lerp(r.top() + 1.0..=r.bottom() - 1.0, v),
                        );
                        mesh.colored_vertex(
                            point,
                            Color32::from_rgb(
                                (20. + strength * 30.) as u8,
                                (21. + strength * 18.) as u8,
                                (26. + strength * 33.) as u8,
                            ),
                        );
                        if x > 0 && y > 0 {
                            let i = y * (columns + 1) + x;
                            mesh.add_triangle(i, i - 1, i - columns - 1);
                            mesh.add_triangle(i - 1, i - columns - 2, i - columns - 1);
                        }
                    }
                }
                ui.painter().add(egui::Shape::mesh(mesh));
                prism(
                    ui.painter(),
                    Rect::from_center_size(r.center(), Vec2::splat(100.)),
                    1.7,
                );
                ui.add_space(20.);
                label(
                    ui,
                    "Devam ederek kurulum konumunu ve bileşenlerini seçebilirsin.",
                    12.,
                    MUTED,
                );
            }
            1 => {
                label(ui, "Hedef klasör", 12., TEXT);
                ui.horizontal(|ui| {
                    let width = (ui.available_width() - 94.).max(80.);
                    if ui
                        .add_sized([width, 38.], egui::TextEdit::singleline(&mut self.path))
                        .changed()
                    {
                        self.free_space = backend::free_bytes(&self.path);
                    }
                    if button(ui, "Gözat…", false, self.worker.is_none()) {
                        let (tx, rx) = mpsc::channel();
                        self.worker = Some(rx);
                        std::thread::spawn(move || {
                            let result = rfd::FileDialog::new()
                                .pick_folder()
                                .map_or(Event::FolderCancelled, Event::Folder);
                            let _ = tx.send(result);
                        });
                    }
                });
                ui.add_space(8.);
                label(
                    ui,
                    "Yolu doğrudan düzenleyebilir veya başka bir klasör seçebilirsin.",
                    12.,
                    MUTED,
                );
                ui.add_space(18.);
                ui.separator();
                ui.add_space(12.);
                ui.horizontal(|ui| {
                    label(
                        ui,
                        format!("Gerekli alan  {}", disk_size(self.payload_size)),
                        12.,
                        MUTED,
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        label(
                            ui,
                            format!("Kullanılabilir alan  {}", disk_size(self.free_space)),
                            12.,
                            MUTED,
                        );
                    });
                });
            }
            2 => {
                choice(
                    ui,
                    &mut true,
                    "OpenPrism",
                    "Uygulama ve temel bağlantı özellikleri",
                    "Gerekli",
                    false,
                );
                choice(
                    ui,
                    &mut self.plan.byedpi,
                    "ByeDPI",
                    "Alternatif bağlantı yöntemi",
                    "İsteğe bağlı",
                    true,
                );
                choice(
                    ui,
                    &mut self.plan.zapret,
                    "Zapret",
                    "Ek bağlantı yöntemi",
                    "İsteğe bağlı",
                    true,
                );
                ui.add_space(13.);
                label(ui, "Diğer seçenekler", 12., TEXT);
                ui.horizontal_wrapped(|ui| {
                    ui.checkbox(&mut self.plan.shortcut, "Masaüstü kısayolu oluştur");
                    ui.checkbox(&mut self.plan.startup, "Windows ile başlat");
                });
                ui.add_space(14.);
                label(
                    ui,
                    "Bağlantı hizmetinin kurulumu için Windows yönetici izni istenebilir.",
                    12.,
                    MUTED,
                );
            }
            3 => {
                label(
                    ui,
                    "Yalnızca seçtiğin uygulamalar için kural oluşturulur.",
                    12.,
                    MUTED,
                );
                ui.add_space(16.);
                ui.horizontal_wrapped(|ui| {
                    if button(ui, "Uygulamaları tara", false, self.worker.is_none()) {
                        self.start(true);
                    }
                    if self.worker.is_some() && self.scanning {
                        ui.spinner();
                    } else if self.scanned {
                        label(
                            ui,
                            format!("{} uygulama bulundu", self.plan.apps.len()),
                            12.,
                            MUTED,
                        );
                    } else {
                        label(ui, "İstersen bu adımı atlayabilirsin.", 12., MUTED);
                    }
                });
                if !self.plan.apps.is_empty() {
                    ui.add_space(12.);
                    ui.horizontal_wrapped(|ui| {
                        label(
                            ui,
                            format!(
                                "{} seçili",
                                self.plan.apps.iter().filter(|app| app.selected).count()
                            ),
                            12.,
                            MUTED,
                        );
                        for (name, selected) in [("Tümünü seç", true), ("Seçimi temizle", false)]
                        {
                            if ui.link(name).clicked() {
                                for app in &mut self.plan.apps {
                                    app.selected = selected;
                                }
                            }
                        }
                    });
                    for app in &mut self.plan.apps {
                        choice(
                            ui,
                            &mut app.selected,
                            app.value["name"].as_str().unwrap_or_default(),
                            app.value["folderPath"].as_str().unwrap_or_default(),
                            "",
                            true,
                        );
                    }
                }
            }
            4 => {
                if let Some((value, message)) = self.progress {
                    ui.horizontal(|ui| {
                        label(ui, "Kurulum ilerliyor", 12., TEXT);
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            label(ui, format!("{:.0}%", value * 100.), 12., TEXT)
                        });
                    });
                    let (r, _) =
                        ui.allocate_exact_size(Vec2::new(ui.available_width(), 6.), Sense::hover());
                    ui.painter().rect_filled(r, 3, LINE);
                    let width = r.width()
                        * ui.ctx()
                            .animate_value_with_time(ui.id().with("progress"), value, 0.25);
                    gradient_rect(
                        ui.painter(),
                        Rect::from_min_size(r.min, Vec2::new(width, r.height())),
                        3.,
                        false,
                    );
                    ui.add_space(10.);
                    label(ui, message, 12., MUTED);
                } else {
                    let components = [
                        Some("OpenPrism"),
                        self.plan.byedpi.then_some("ByeDPI"),
                        self.plan.zapret.then_some("Zapret"),
                    ]
                    .into_iter()
                    .flatten()
                    .collect::<Vec<_>>()
                    .join(", ");
                    let options = [
                        self.plan.shortcut.then_some("Masaüstü kısayolu"),
                        self.plan.startup.then_some("Windows ile başlat"),
                    ]
                    .into_iter()
                    .flatten()
                    .collect::<Vec<_>>()
                    .join(", ");
                    Frame::new()
                        .stroke(Stroke::new(1_f32, LINE))
                        .corner_radius(9)
                        .inner_margin(14)
                        .show(ui, |ui| {
                            for (index, (name, value)) in [
                                ("Konum", self.plan.location.display().to_string()),
                                ("Bileşenler", components),
                                (
                                    "Seçenekler",
                                    if options.is_empty() {
                                        "—".into()
                                    } else {
                                        options
                                    },
                                ),
                                (
                                    "Kurallar",
                                    format!(
                                        "{} uygulama",
                                        self.plan.apps.iter().filter(|app| app.selected).count()
                                    ),
                                ),
                            ]
                            .into_iter()
                            .enumerate()
                            {
                                if index > 0 {
                                    ui.separator();
                                }
                                ui.horizontal_top(|ui| {
                                    ui.allocate_ui(Vec2::new(130., 18.), |ui| {
                                        ui.set_min_width(130.);
                                        label(ui, name, 12., MUTED)
                                    });
                                    label(ui, value, 12., TEXT);
                                });
                            }
                        });
                    if self.account != Some(true) {
                        ui.add_space(15.);
                        ui.checkbox(&mut self.plan.consent, "Yeni bir ücretsiz WARP hesabı gerekirse Cloudflare koşullarını kabul ediyorum.");
                        ui.hyperlink_to(
                            "Cloudflare WARP kullanım koşulları",
                            "https://www.cloudflare.com/application/terms/",
                        );
                    }
                }
            }
            _ => {
                label(
                    ui,
                    format!(
                        "Kurallar için {} uygulama seçildi.",
                        self.plan.apps.iter().filter(|app| app.selected).count()
                    ),
                    12.,
                    MUTED,
                );
                ui.add_space(23.);
                ui.checkbox(&mut self.launch, "OpenPrism’i başlat");
            }
        }
        self.error_card(ui);
    }

    fn error_card(&self, ui: &mut egui::Ui) {
        if let Some(error) = &self.error {
            ui.add_space(15.);
            Frame::new()
                .fill(rgb(0x202126))
                .stroke(Stroke::new(1_f32, rgb(0x3a3b44)))
                .corner_radius(9)
                .inner_margin(14)
                .show(ui, |ui| label(ui, error, 13., rgb(0xe7a8ce)));
        }
    }
}

impl eframe::App for Installer {
    fn update(&mut self, ctx: &egui::Context, _: &mut eframe::Frame) {
        self.receive(ctx);
        let installing = self.worker.is_some() && (self.removing || self.step == 4);
        if installing && ctx.input(|i| i.viewport().close_requested()) {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
        }
        egui::CentralPanel::default()
            .frame(
                Frame::new()
                    .fill(rgb(0x101115))
                    .stroke(Stroke::new(1_f32, rgb(0x2b2c34)))
                    .corner_radius(14),
            )
            .show(ctx, |ui| {
                let bounds = ui.max_rect();
                let title = Rect::from_min_size(bounds.min, Vec2::new(bounds.width(), 47.));
                ui.painter().text(
                    title.left_center() + Vec2::new(19., 0.),
                    egui::Align2::LEFT_CENTER,
                    if self.removing {
                        "OpenPrism Kaldırma"
                    } else {
                        "OpenPrism Kurulumu"
                    },
                    FontId::proportional(12.),
                    rgb(0xb5b5bd),
                );
                let drag =
                    Rect::from_min_max(title.min, title.right_bottom() - Vec2::new(135., 0.));
                let title_response =
                    ui.interact(drag, ui.id().with("drag"), Sense::click_and_drag());
                if title_response.drag_started() {
                    ctx.send_viewport_cmd(egui::ViewportCommand::StartDrag);
                }
                if title_response.double_clicked() {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Maximized(
                        !ctx.input(|i| i.viewport().maximized.unwrap_or(false)),
                    ));
                }
                for (index, glyph) in ["−", "□", "×"].into_iter().enumerate() {
                    let rect = Rect::from_min_size(
                        Pos2::new(title.right() - (3 - index) as f32 * 45., title.top()),
                        Vec2::new(45., title.height()),
                    );
                    let response =
                        ui.interact(rect, ui.id().with(("window", index)), Sense::click());
                    if response.hovered() {
                        ui.painter().rect_filled(
                            rect,
                            0,
                            rgb(if index == 2 { 0x683041 } else { 0x25232d }),
                        );
                    }
                    ui.painter().text(
                        rect.center(),
                        egui::Align2::CENTER_CENTER,
                        glyph,
                        FontId::proportional(16.),
                        MUTED,
                    );
                    if response.clicked() {
                        match index {
                            0 => ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(true)),
                            1 => ctx.send_viewport_cmd(egui::ViewportCommand::Maximized(
                                !ctx.input(|i| i.viewport().maximized.unwrap_or(false)),
                            )),
                            _ if !installing => ctx.send_viewport_cmd(egui::ViewportCommand::Close),
                            _ => (),
                        }
                    }
                }
                let body = Rect::from_min_max(title.left_bottom(), bounds.max);
                let side = Rect::from_min_size(body.min, Vec2::new(219., body.height()));
                ui.painter().rect_filled(
                    side,
                    CornerRadius {
                        sw: 13,
                        ..CornerRadius::ZERO
                    },
                    rgb(0x0d0e11),
                );
                ui.painter().hline(
                    bounds.x_range(),
                    title.bottom(),
                    Stroke::new(1_f32, rgb(0x22242b)),
                );
                ui.painter().vline(
                    side.right(),
                    side.y_range(),
                    Stroke::new(1_f32, rgb(0x24252c)),
                );
                let brand = side.min + Vec2::new(22., 30.);
                prism(
                    ui.painter(),
                    Rect::from_min_size(brand, Vec2::splat(30.)),
                    1.35,
                );
                let wordmark = ui.painter().layout_job(branding::wordmark(semibold(19.)));
                ui.painter().galley(
                    brand + Vec2::new(40., 15. - wordmark.size().y / 2.),
                    wordmark,
                    TEXT,
                );
                ui.painter().text(
                    brand + Vec2::new(0., 77.),
                    egui::Align2::LEFT_CENTER,
                    if self.removing {
                        "KALDIRMA ADIMLARI"
                    } else {
                        "KURULUM ADIMLARI"
                    },
                    FontId::proportional(11.),
                    rgb(0x71737e),
                );
                let steps: &[&str] = if self.removing {
                    &["Onay", "Kaldırma", "Tamamlandı"]
                } else {
                    &STEPS
                };
                for (index, &name) in steps.iter().enumerate() {
                    let r = Rect::from_min_size(
                        brand + Vec2::new(0., 99. + index as f32 * 46.),
                        Vec2::new(175., 39.),
                    );
                    let center = r.left_center() + Vec2::new(20.5, 0.);
                    if index == self.step {
                        ui.painter().rect_filled(r, 8, rgb(0x1b1c22));
                        gradient_rect(
                            ui.painter(),
                            Rect::from_center_size(center, Vec2::splat(23.)),
                            12.,
                            false,
                        );
                    } else {
                        ui.painter().circle_stroke(
                            center,
                            11.,
                            Stroke::new(
                                1_f32,
                                rgb(if index < self.step {
                                    0x6babae
                                } else {
                                    0x3b3d47
                                }),
                            ),
                        );
                    }
                    if index < self.step {
                        checkmark(ui.painter(), center, 10.);
                    } else {
                        ui.painter().text(
                            center,
                            egui::Align2::CENTER_CENTER,
                            (index + 1).to_string(),
                            FontId::proportional(11.),
                            if index == self.step {
                                rgb(0x101115)
                            } else {
                                MUTED
                            },
                        );
                    }
                    ui.painter().text(
                        r.left_center() + Vec2::new(43., 0.),
                        egui::Align2::LEFT_CENTER,
                        name,
                        FontId::proportional(13.),
                        if index == self.step { TEXT } else { MUTED },
                    );
                }
                ui.painter().text(
                    side.left_bottom() + Vec2::new(30., -29.),
                    egui::Align2::LEFT_CENTER,
                    "Bağlantın, kontrolünde.",
                    FontId::proportional(11.),
                    rgb(0x656772),
                );
                let main = Rect::from_min_max(side.right_top(), bounds.max);
                let split = main.bottom() - 74.;
                let contents = Rect::from_min_max(
                    main.min + Vec2::new(48., 42.),
                    Pos2::new(main.right() - 48., split - 16.),
                );
                ui.scope_builder(egui::UiBuilder::new().max_rect(contents), |ui| {
                    ui.set_clip_rect(contents);
                    egui::ScrollArea::vertical()
                        .id_salt((self.removing, self.step))
                        .show(ui, |ui| {
                            ui.set_width(contents.width());
                            if self.removing {
                                self.uninstall_content(ui);
                            } else {
                                self.content(ui);
                            }
                        });
                });
                ui.painter()
                    .hline(main.x_range(), split, Stroke::new(1_f32, LINE));
                ui.painter().text(
                    Pos2::new(contents.left(), split + 37.),
                    egui::Align2::LEFT_CENTER,
                    "OpenPrism",
                    FontId::proportional(11.),
                    rgb(0x70727c),
                );
                let footer = Rect::from_min_max(
                    Pos2::new(contents.left(), split + 18.),
                    Pos2::new(contents.right(), main.bottom() - 18.),
                );
                ui.scope_builder(
                    egui::UiBuilder::new()
                        .max_rect(footer)
                        .layout(Layout::right_to_left(Align::Center)),
                    |ui| {
                        if self.removing {
                            self.uninstall_footer(ui, ctx);
                            return;
                        }
                        let available = self.worker.is_none();
                        let action = match self.step {
                            4 if self.error.is_some() => "Yeniden dene",
                            4 => "Kur",
                            5 => "Bitir",
                            _ => "Devam et",
                        };
                        let next = button(ui, action, true, available);
                        if self.step > 0 && self.step < 5 && button(ui, "Geri", false, available) {
                            self.error = None;
                            self.step -= 1;
                        } else if self.step == 0
                            && self.registered.is_some()
                            && button(ui, "Kaldır…", false, available)
                        {
                            self.error = None;
                            if let Some(location) = self.registered.clone() {
                                self.removing = !self.handoff_if_installed(ctx, &location);
                            }
                        } else if next {
                            self.next(ctx);
                        }
                    },
                );
            });
    }
}

pub fn launch_from_executable(uninstall_only: bool) -> eframe::Result {
    let package = std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(ToOwned::to_owned))
        .unwrap_or_default();
    if uninstall_only {
        if let Some(location) = backend::installed_location() {
            if same_directory(&package, &location) && backend::handoff_uninstall(&location).is_ok()
            {
                return Ok(());
            }
        }
    }
    run(package, uninstall_only)
}

fn same_directory(left: &std::path::Path, right: &std::path::Path) -> bool {
    match (left.canonicalize(), right.canonicalize()) {
        (Ok(left), Ok(right)) => left
            .to_string_lossy()
            .eq_ignore_ascii_case(&right.to_string_lossy()),
        _ => false,
    }
}

pub fn run(package: PathBuf, uninstall_only: bool) -> eframe::Result {
    let title = if uninstall_only {
        "OpenPrism Kaldırma"
    } else {
        "OpenPrism Kurulumu"
    };
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([900., 619.])
            .with_min_inner_size([740., 560.])
            .with_decorations(false)
            .with_icon(branding::window_icon()),
        ..Default::default()
    };
    eframe::run_native(
        title,
        options,
        Box::new(move |cc| {
            let mut style = (*cc.egui_ctx.style()).clone();
            style.spacing.item_spacing = Vec2::new(8., 4.);
            style.visuals.panel_fill = rgb(0x101115);
            style.visuals.extreme_bg_color = rgb(0x0c0d10);
            style.visuals.selection.bg_fill = rgb(0x36595f);
            style.visuals.hyperlink_color = CYAN;
            cc.egui_ctx.set_style(style);
            let mut fonts = egui::FontDefinitions::default();
            let semibold_family = egui::FontFamily::Name("semibold".into());
            fonts.families.insert(
                semibold_family.clone(),
                fonts.families[&egui::FontFamily::Proportional].clone(),
            );
            if let Some(root) = std::env::var_os("SystemRoot").map(PathBuf::from) {
                for (file, family) in [
                    ("segoeui.ttf", egui::FontFamily::Proportional),
                    ("seguisb.ttf", semibold_family),
                ] {
                    if let Ok(font) = std::fs::read(root.join("Fonts").join(file)) {
                        fonts
                            .font_data
                            .insert(file.into(), egui::FontData::from_owned(font).into());
                        fonts
                            .families
                            .entry(family)
                            .or_default()
                            .insert(0, file.into());
                    }
                }
            }
            cc.egui_ctx.set_fonts(fonts);
            Ok(Box::new(Installer::new(
                package,
                uninstall_only,
            )))
        }),
    )
}
