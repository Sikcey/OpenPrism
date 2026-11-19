#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod bridge;
#[path = "../../../assets/prism.rs"]
mod branding;
mod dpi_config;
mod locale;
mod log_view;
mod profile_page;
mod rule_editor;
mod settings_view;
mod software_runtime;
#[cfg(windows)]
mod startup;
mod tray;
mod widgets;
use bridge::Bridge;
use eframe::egui::{self, Color32, RichText, Stroke, Vec2};
use serde_json::{json, Value};

const BG: Color32 = Color32::from_rgb(8, 9, 11);
const PANEL: Color32 = Color32::from_rgb(16, 17, 21);
const LINE: Color32 = Color32::from_rgb(40, 42, 50);
const ACCENT: Color32 = Color32::from_rgb(137, 230, 230);
const SOFT: Color32 = Color32::from_rgb(23, 59, 52);
const SUB: Color32 = Color32::from_rgb(146, 148, 159);
const INK: Color32 = Color32::from_rgb(243, 241, 235);
fn valid_settings(v: &Value) -> bool {
    v.is_object() && v["rules"].is_array() && v["dpi"].is_object() && v["profile"].is_number()
}

fn s(v: &Value, k: &str) -> String {
    v.get(k).and_then(Value::as_str).unwrap_or("").to_owned()
}
fn b(v: &Value, k: &str) -> bool {
    v.get(k).and_then(Value::as_bool).unwrap_or(false)
}
fn n(v: &Value, k: &str) -> i64 {
    v.get(k).and_then(Value::as_i64).unwrap_or(0)
}
fn default_rule_name(kind: i64, value: &str) -> String {
    if kind == 6 || kind == 7 {
        let trimmed = value.trim_end_matches(['\\', '/']);
        if !trimmed.ends_with(':') {
            return trimmed
                .rsplit(['\\', '/'])
                .next()
                .unwrap_or(value)
                .to_owned();
        }
    }
    value.to_owned()
}
fn rule_display_name(rule: &Value) -> String {
    let name = s(rule, "name");
    let value = s(rule, "value");
    if name.trim().is_empty() || name == value {
        default_rule_name(n(rule, "type"), &value)
    } else {
        name
    }
}
fn card(ui: &mut egui::Ui, f: impl FnOnce(&mut egui::Ui)) {
    egui::Frame::new()
        .fill(PANEL)
        .stroke(Stroke::new(1_f32, LINE))
        .corner_radius(10)
        .inner_margin(12)
        .show(ui, |ui| {
            ui.set_min_width(ui.available_width());
            f(ui);
        });
}
fn small(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(locale::tr(text)).size(11.).color(SUB));
}
fn title(ui: &mut egui::Ui, eyebrow: &str, title: &str) {
    ui.label(
        RichText::new(locale::tr(eyebrow).to_uppercase())
            .size(11.)
            .strong()
            .color(SUB),
    );
    ui.label(RichText::new(locale::tr(title)).font(egui::FontId::new(
        34.,
        egui::FontFamily::Name("bold".into()),
    )));
    ui.add_space(0.);
}
fn types(i: i64) -> &'static str {
    locale::tr(match i {
        1 => "IPv4 adresi",
        2 => "IPv6 adresi",
        3 => "IPv4 aralığı",
        4 => "IPv6 aralığı",
        5 => "İşlem adı",
        6 => "Uygulama yolu",
        7 => "Klasör yolu",
        _ => "Kural",
    })
}
struct App {
    ui_context: egui::Context,
    bridge: Bridge,
    scan_bridge: Bridge,
    goodcheck_running: bool,
    goodcheck_hosts: String,
    goodcheck_result: Value,
    goodcheck_selected: String,
    dpi_presets: Vec<Value>,
    #[cfg(windows)]
    startup: startup::Startup,
    settings: Value,
    scan: Value,
    page: String,
    notice: String,
    toasts: Vec<(String, f64)>,
    connected: bool,
    service_fault: bool,
    offline: bool,
    pending: usize,
    preserve_notice: bool,
    license: String,
    license_dialog: bool,
    rule: Option<Value>,
    rule_index: Option<usize>,
    log_view: log_view::LogView,
    update_snapshot: Value,
    validating: bool,
    rule_error: String,
    dpi_dialog: bool,
    dpi_draft: Value,
    remove_confirm: bool,
    local_logs: std::collections::VecDeque<String>,
    backend_logs: std::collections::VecDeque<String>,
    logs_error: String,
    profile_view: Option<i64>,
    capture: Option<String>,
    capture_frame: u8,
    tray: Option<tray::Tray>,
    quitting: bool,
    inspect_only: bool,
}
impl App {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        Self::new_context(cc.egui_ctx.clone())
    }
    fn new_context(ctx: egui::Context) -> Self {
        locale::apply_fonts(&ctx);
        let mut visuals = egui::Visuals::dark();
        visuals.override_text_color = Some(INK);
        visuals.panel_fill = BG;
        visuals.window_fill = PANEL;
        visuals.widgets.inactive.bg_fill = PANEL;
        visuals.widgets.inactive.bg_stroke = Stroke::new(1_f32, LINE);
        visuals.widgets.hovered.bg_fill = SOFT;
        visuals.selection.bg_fill = SOFT;
        ctx.set_visuals(visuals);
        ctx.style_mut(|style| {
            style.spacing.interact_size = Vec2::new(32., 30.);
            style.spacing.button_padding = Vec2::new(12., 7.);
            style.spacing.item_spacing = Vec2::new(8., 7.);
            style.visuals.widgets.inactive.weak_bg_fill = BG;
            style.visuals.widgets.active.bg_fill = SOFT;
            style.visuals.widgets.open.bg_fill = PANEL;
            style.visuals.extreme_bg_color = BG;
            style.visuals.window_stroke = Stroke::new(1_f32, LINE);
            style.visuals.widgets.inactive.weak_bg_fill = PANEL;
            style.visuals.widgets.hovered.weak_bg_fill = SOFT;
            style.visuals.widgets.active.weak_bg_fill = SOFT;
            style.visuals.widgets.open.weak_bg_fill = SOFT;
            for widget in [
                &mut style.visuals.widgets.inactive,
                &mut style.visuals.widgets.hovered,
                &mut style.visuals.widgets.active,
                &mut style.visuals.widgets.open,
            ] {
                widget.corner_radius = egui::CornerRadius::same(7);
            }
            style.animation_time = 0.;
        });
        let args: Vec<String> = std::env::args().collect();
        let offline = args.iter().any(|x| {
            x == "--smoke-no-network" || x == "--screenshot-friendly" || x.starts_with("--capture=")
        });
        let page = args
            .iter()
            .find_map(|x| x.strip_prefix("--page="))
            .unwrap_or("connection")
            .to_owned();
        let bridge = Bridge::new(ctx.clone());
        let mut initial_pending = 0;
        if !offline {
            for action in ["load-settings", "status", "presets"] {
                initial_pending += usize::from(bridge.call(action, vec![], None));
            }
        }
        let fixture = args.iter().any(|a| a == "--fixture") && offline;
        let mut result = Self {
            ui_context: ctx.clone(),
            bridge,
            scan_bridge: Bridge::new(ctx.clone()),
            goodcheck_running: false,
            goodcheck_hosts: "discord.com\nyoutube.com".into(),
            goodcheck_result: Value::Null,
            goodcheck_selected: String::new(),
            dpi_presets: Vec::new(),
            #[cfg(windows)]
            startup: startup::Startup::new(ctx.clone(), !offline),
            settings: json!({"profile":0,"connectionMethod":0,"scope":1,"wireGuardProfileName":"","dpi":{"engine":"byedpi","presetId":"byedpi-standard","customArguments":"","dns":""},"applications":[],"rules":[],"closeToTray":true,"autoConnect":false,"ruleSchemaVersion":2,"language":"tr","detailedLogs":false}),
            scan: Value::Null,
            page,
            notice: if offline {
                "Çevrimdışı görünüm".into()
            } else {
                "Hizmet hazırlanıyor".into()
            },
            toasts: Vec::new(),
            connected: false,
            service_fault: false,
            offline,
            pending: initial_pending,
            preserve_notice: false,
            license: String::new(),
            license_dialog: false,
            rule: None,
            rule_index: None,
            log_view: Default::default(),
            update_snapshot: Value::Null,
            validating: false,
            rule_error: String::new(),
            dpi_dialog: false,
            dpi_draft: json!({}),
            remove_confirm: false,
            local_logs: Default::default(),
            backend_logs: Default::default(),
            logs_error: String::new(),
            profile_view: None,
            capture: args
                .iter()
                .find_map(|a| a.strip_prefix("--capture=").map(str::to_owned)),
            capture_frame: 0,
            tray: if offline {
                None
            } else {
                tray::Tray::new(ctx.clone())
            },
            quitting: false,
            inspect_only: args.iter().any(|a| a == "--inspect-only"),
        };
        if fixture {
            result.scan = json!({"serviceInstalled":true,"serviceReady":true,"hasPreparedWarpProfile":true,"components":[{"name":"ByeDPI","installed":true},{"name":"Zapret","installed":false}]});
            result.settings["rules"] = json!([
                {"id":"fixture-discord","type":5,"name":"Discord","value":"Discord.exe","enabled":true},
                {"id":"fixture-edge","type":5,"name":"Microsoft Edge","value":"msedge.exe","enabled":true},
                {"id":"fixture-network","type":3,"name":"Yerel ağ","value":"192.168.0.0/16","enabled":false}
            ]);
        }
        if offline && args.iter().any(|a| a == "--fixture-errors") {
            result.service_fault = true;
            result.scan = json!({"serviceInstalled":false,"serviceReady":false});
            result.notify_failure("connect");
            result.notify_failure("status");
            result.notify_failure("resume");
            for (_, started) in &mut result.toasts {
                *started -= 0.5;
            }
        }
        if result.page == "rule" {
            result.page = "connection".into();
            result.rule =
                Some(json!({"id":"fixture-rule","type":1,"name":"","value":"","enabled":true}));
        }
        if !offline && result.tray.is_none() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
            result.notice = "Sistem tepsisi açılamadı; pencere açık tutuluyor".into();
        }
        if offline {
            if args.iter().any(|arg| arg == "--zapret-profile") {
                result.page = "profiles".into();
                result.profile_view = Some(3);
                result.settings["profile"] = json!(3);
            }
            if let Some(language) = args.iter().find_map(|arg| arg.strip_prefix("--language=")) {
                result.settings["language"] = json!(language);
            }
            if args.iter().any(|arg| arg == "--dpi-config") {
                result.page = "profiles".into();
                result.settings["profile"] = json!(2);
                result.dpi_dialog = true;
                result.dpi_draft = result.settings["dpi"].clone();
            }
        }
        locale::set_language(
            result
                .settings
                .get("language")
                .and_then(Value::as_str)
                .unwrap_or("tr"),
        );
        locale::apply_fonts(&result.ui_context);
        result
    }
    fn notify_failure(&mut self, action: &str) {
        let message = match locale::language() {
            "en" => "The operation failed. Check your connection and retry, or repair the service in Services.",
            "ru" => "\u{041e}\u{043f}\u{0435}\u{0440}\u{0430}\u{0446}\u{0438}\u{044f} \u{043d}\u{0435} \u{0432}\u{044b}\u{043f}\u{043e}\u{043b}\u{043d}\u{0435}\u{043d}\u{0430}. \u{041f}\u{0440}\u{043e}\u{0432}\u{0435}\u{0440}\u{044c}\u{0442}\u{0435} \u{0441}\u{043e}\u{0435}\u{0434}\u{0438}\u{043d}\u{0435}\u{043d}\u{0438}\u{0435} \u{0438} \u{043f}\u{043e}\u{0432}\u{0442}\u{043e}\u{0440}\u{0438}\u{0442}\u{0435} \u{043f}\u{043e}\u{043f}\u{044b}\u{0442}\u{043a}\u{0443} \u{0438}\u{043b}\u{0438} \u{0432}\u{043e}\u{0441}\u{0441}\u{0442}\u{0430}\u{043d}\u{043e}\u{0432}\u{0438}\u{0442}\u{0435} \u{0441}\u{043b}\u{0443}\u{0436}\u{0431}\u{0443}.",
            "zh" => "\u{64cd}\u{4f5c}\u{5931}\u{8d25}\u{3002}\u{8bf7}\u{68c0}\u{67e5}\u{8fde}\u{63a5}\u{5e76}\u{91cd}\u{8bd5}\u{ff0c}\u{6216}\u{5728}\u{670d}\u{52a1}\u{4e2d}\u{4fee}\u{590d}\u{670d}\u{52a1}\u{3002}",
            _ => "\u{0130}\u{015f}lem ba\u{015f}ar\u{0131}s\u{0131}z. Ba\u{011f}lant\u{0131}n\u{0131}z\u{0131} kontrol edip tekrar deneyin veya Hizmetler b\u{00f6}l\u{00fc}m\u{00fc}nden hizmeti onar\u{0131}n.",
        }.to_owned();
        if matches!(action, "connect" | "status" | "resume") {
            self.service_fault = true;
        }
        self.notice = message.clone();
        self.toasts
            .push((message, self.ui_context.input(|i| i.time)));
        if self.toasts.len() > 3 {
            self.toasts.remove(0);
        }
        self.ui_context.request_repaint();
    }
    fn call(&mut self, a: &str, mut args: Vec<String>, v: Option<Value>) {
        if self.offline {
            self.notice = "Çevrimdışı görünüm: işlem yapılmadı".into();
            return;
        }
        if a == "scan" {
            args.push("--status-only".into());
        }
        let bridge = if a == "goodcheck" {
            &self.scan_bridge
        } else {
            &self.bridge
        };
        if bridge.call(a, args, v) {
            self.pending += 1;
        } else {
            self.notice = "Hizmet işlemi başlatılamadı".into();
        }
    }
    fn save(&mut self) {
        self.call("save-settings", vec![], Some(self.settings.clone()));
    }
    fn log_line(lines: &mut std::collections::VecDeque<String>, line: &str) {
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs()
            % 86400;
        let time = format!(
            "{:02}:{:02}:{:02} UTC",
            seconds / 3600,
            seconds / 60 % 60,
            seconds % 60
        );
        for part in line.lines() {
            if lines.len() == 100 {
                lines.pop_front();
            }
            lines.push_back(format!(
                "{time} {}",
                part.chars().take(1000).collect::<String>()
            ));
        }
    }
    fn replies(&mut self) {
        #[cfg(windows)]
        if let Some(tray) = self.tray.as_mut() {
            tray.refresh_language();
        }
        #[cfg(windows)]
        if let Some(Err(error)) = self.startup.poll() {
            Self::log_line(&mut self.local_logs, &error);
            self.notify_failure("startup");
        }
        while let Ok(reply) = self
            .bridge
            .rx
            .try_recv()
            .or_else(|_| self.scan_bridge.rx.try_recv())
        {
            if reply.action == "progress" {
                if let Ok(value) = reply.result {
                    self.notice = s(&value, "progress");
                    Self::log_line(&mut self.local_logs, &self.notice);
                }
                continue;
            }
            self.pending = self.pending.saturating_sub(1);
            match &reply.result {
                Ok(v) => {
                    let message = s(v, "message");
                    if !message.is_empty() {
                        Self::log_line(
                            &mut self.local_logs,
                            &format!("{}: {}", reply.action, message),
                        );
                    }
                }
                Err(e) => Self::log_line(&mut self.local_logs, &format!("{}: {}", reply.action, e)),
            }
            match reply.result {
                Err(e) => {
                    Self::log_line(&mut self.local_logs, &e);
                    self.notify_failure(&reply.action);
                    if reply.action == "goodcheck" {
                        self.goodcheck_running = false;
                        self.goodcheck_result = json!({"code":"error","message":self.notice});
                    }
                    if reply.action == "logs" {
                        self.logs_error = self.notice.clone();
                    }

                    self.validating = false;
                    if reply.action == "validate-rule" {
                        self.rule_error = self.notice.clone();
                    }
                }
                Ok(v) => {
                    if v.get("success").and_then(Value::as_bool) == Some(false) {
                        self.notify_failure(&reply.action);
                    }
                    match reply.action.as_str() {
                        "load-settings" => {
                            if valid_settings(&v) {
                                self.settings = v;
                                locale::set_language(
                                    self.settings
                                        .get("language")
                                        .and_then(Value::as_str)
                                        .unwrap_or("tr"),
                                );
                                locale::apply_fonts(&self.ui_context);
                                self.settings["connectionMethod"] =
                                    json!(if n(&self.settings, "profile") == 3 {
                                        2
                                    } else if n(&self.settings, "profile") == 2 {
                                        1
                                    } else {
                                        0
                                    });
                            } else {
                                if b(&v, "success") {
                                    self.notice = s(&v, "message");
                                }
                            }
                        }
                        "scan" => {
                            self.scan = v;
                            self.service_fault = !b(&self.scan, "serviceReady");
                        }
                        "status" => {
                            self.connected = b(&v, "connected");
                            if !self.preserve_notice {
                                if b(&v, "success") {
                                    self.notice = s(&v, "message");
                                }
                            }
                            self.preserve_notice = false;
                        }
                        "goodcheck" => {
                            self.goodcheck_running = false;
                            self.goodcheck_selected = v["results"]
                                .as_array()
                                .and_then(|r| r.iter().find(|r| n(r, "successes") > 0))
                                .map(|r| s(r, "presetId"))
                                .unwrap_or_default();
                            self.goodcheck_result = if v["code"].is_null() {
                                json!({"code":"error","message":s(&v,"message")})
                            } else {
                                v
                            };
                        }
                        "install-component" | "remove-component" => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            self.call("scan", vec![], None);
                        }
                        "presets" => {
                            self.dpi_presets = v.as_array().cloned().unwrap_or_default();
                        }
                        "check-updates" => {
                            self.update_snapshot = v;
                        }
                        "validate-rule" => {
                            self.validating = false;
                            self.rule_error = if b(&v, "success") {
                                String::new()
                            } else {
                                self.notice.clone()
                            };
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            if b(&v, "success") {
                                if let Some(rule) = self.rule.take() {
                                    let rules = self.settings["rules"].as_array_mut().unwrap();
                                    if let Some(i) = self.rule_index {
                                        if i < rules.len() {
                                            rules[i] = rule;
                                        }
                                    } else {
                                        rules.push(rule);
                                    }
                                    self.save();
                                }
                            }
                        }
                        "connect" | "disconnect" | "resume" => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            self.preserve_notice = !b(&v, "success");
                            self.call("status", vec![], None);
                        }
                        "install-service" => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            self.call("scan", vec![], None);
                            if b(&v, "success") {
                                self.save();
                            }
                        }
                        "clear-logs" => {
                            self.call("logs", vec![], None);
                        }
                        "logs" => {
                            if b(&v, "success") {
                                self.logs_error.clear();
                                self.backend_logs.clear();
                                if let Some(lines) = v["lines"].as_array() {
                                    for line in lines {
                                        if let Some(line) = line.as_str() {
                                            if self.backend_logs.len() >= 100 {
                                                self.backend_logs.pop_front();
                                            }
                                            self.backend_logs
                                                .push_back(line.chars().take(1000).collect());
                                        }
                                    }
                                }
                            } else {
                                self.logs_error = self.notice.clone();
                            }
                        }
                        "remove-service" => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            self.call("scan", vec![], None);
                        }
                        "import-wireguard" | "import-warp" | "warp-license" => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                            if b(&v, "success") {
                                self.call("load-settings", vec![], None);
                                self.call("scan", vec![], None);
                            }
                        }
                        "save-settings" => {
                            if !b(&v, "success") {
                                if b(&v, "success") {
                                    self.notice = s(&v, "message");
                                }
                            }
                        }
                        _ => {
                            if b(&v, "success") {
                                self.notice = s(&v, "message");
                            }
                        }
                    }
                }
            }
        }
    }
    fn connection(&mut self, ui: &mut egui::Ui) {
        title(ui, "Genel bakış", "Bağlantın kontrolünde.");
        ui.label(
            RichText::new(locale::tr(
                "Nasıl bağlanacağını seç ve tek dokunuşla başla.",
            ))
            .size(15.)
            .color(SUB),
        );
        ui.add_space(24.);
        let profile = n(&self.settings, "profile");
        egui::Frame::new()
            .fill(PANEL)
            .stroke(Stroke::new(1_f32, LINE))
            .corner_radius(16)
            .inner_margin(36)
            .show(ui, |ui| {
                ui.set_min_width(ui.available_width());
                ui.set_min_height(205.);
                ui.set_max_height(205.);
                ui.horizontal_centered(|ui| {
                    let text_width = (ui.available_width() - 180.).max(100.);
                    ui.allocate_ui_with_layout(
                        Vec2::new(text_width, 130.),
                        egui::Layout::top_down(egui::Align::LEFT),
                        |ui| {
                            ui.add_space(13.);
                            ui.label(
                                RichText::new(locale::tr(if self.connected {
                                    "\u{25cf}  Ba\u{011f}l\u{0131}"
                                } else {
                                    "\u{25cf}  Ba\u{011f}l\u{0131} de\u{011f}il"
                                }))
                                .size(13.)
                                .color(SUB),
                            );
                            ui.add_space(12.);
                            ui.label(
                                RichText::new(locale::tr(if self.connected {
                                    "Bağlantı etkin."
                                } else {
                                    "Hazır olduğunda bağlan."
                                }))
                                .font(egui::FontId::new(
                                    27.,
                                    egui::FontFamily::Name("bold".into()),
                                ))
                                .color(INK),
                            );
                            ui.add_space(10.);
                            ui.label(
                                RichText::new(locale::tr(
                                    "Seçtiğin profil ile trafiğini yönlendir.",
                                ))
                                .size(15.)
                                .color(SUB),
                            );
                        },
                    );
                    ui.add_space((ui.available_width() - 130.).max(0.));
                    let (r, response) =
                        ui.allocate_exact_size(Vec2::splat(130.), egui::Sense::click());
                    let c = r.center();
                    let p = ui.painter();
                    for i in (0..24).rev() {
                        let t = (24 - i) as f32 / 24.;
                        p.circle_filled(
                            c,
                            70. + i as f32,
                            Color32::from_rgb(
                                16 + (14. * t) as u8,
                                17 + (9. * t) as u8,
                                21 + (23. * t) as u8,
                            ),
                        );
                    }
                    p.circle_filled(c, 74., Color32::from_rgb(30, 27, 39));
                    p.circle_filled(c, 68., Color32::from_rgb(38, 33, 49));
                    widgets::power_gradient(p, c, 64., response.hovered());
                    let points: Vec<_> = (0..=40)
                        .map(|i| {
                            let a = 0.65 + i as f32 / 40. * (std::f32::consts::TAU - 1.3);
                            c + Vec2::new(a.sin() * 16., -a.cos() * 16.)
                        })
                        .collect();
                    p.add(egui::Shape::line(points, Stroke::new(3_f32, BG)));
                    p.line_segment(
                        [c + Vec2::new(0., -22.), c + Vec2::new(0., -2.)],
                        Stroke::new(3_f32, BG),
                    );
                    if response
                        .on_hover_text(locale::tr("Bağlan / bağlantıyı kes"))
                        .clicked()
                        && self.pending == 0
                    {
                        if self.connected {
                            self.call("disconnect", vec![], None);
                        } else if !self.scan.is_null()
                            && !b(&self.scan, "serviceReady")
                            && !self.offline
                        {
                            self.page = "services".into();
                            self.service_fault = true;
                            self.notify_failure("connect");
                        } else if !self.scan.is_null()
                            && (profile == 2 || profile == 3)
                            && !self.scan["components"].as_array().is_some_and(|items| {
                                items.iter().any(|item| {
                                    s(item, "name")
                                        == if profile == 3 { "Zapret" } else { "ByeDPI" }
                                        && b(item, "installed")
                                })
                            })
                        {
                            self.page = "services".into();
                            self.notify_failure("connect");
                            self.preserve_notice = true;
                        } else if !self.scan.is_null()
                            && profile == 0
                            && !b(&self.scan, "hasPreparedWarpProfile")
                        {
                            self.page = "profiles".into();
                        } else {
                            self.call("connect", vec![], Some(self.settings.clone()));
                        }
                    }
                });
            });
        ui.add_space(14.);
        let locked = self.connected || self.pending != 0;
        ui.add_enabled_ui(!locked, |ui| {
            ui.label(
                RichText::new(locale::tr("Bağlantı yöntemi"))
                    .size(13.)
                    .strong()
                    .color(SUB),
            );
            ui.add_space(0.);
            let mut method = match profile {
                2 => 1,
                3 => 2,
                _ => 0,
            };
            let before = method;
            ui.scope(|ui| {
                ui.spacing_mut().interact_size.y = 48.;
                ui.spacing_mut().button_padding.y = 8.;
                egui::ComboBox::from_id_salt("connection-method")
                    .width(ui.available_width())
                    .selected_text(["WireGuard", "ByeDPI", "Zapret"][method])
                    .show_ui(ui, |ui| {
                        for (i, label) in ["WireGuard", "ByeDPI", "Zapret"].iter().enumerate() {
                            ui.selectable_value(&mut method, i, *label);
                        }
                    });
            });
            if method != before {
                self.settings["profile"] = json!(match method {
                    1 => 2,
                    2 => 3,
                    _ => 0,
                });
                self.settings["connectionMethod"] = json!(method);
                self.save();
            }
            ui.add_space(12.);
            ui.horizontal(|ui| {
                ui.label(
                    RichText::new(locale::tr("Yapılandırmayı değiştirmek mi istiyorsun?"))
                        .color(SUB),
                );
                if ui
                    .link(RichText::new(locale::tr("Profilleri düzenle →")).color(ACCENT))
                    .clicked()
                {
                    self.page = "profiles".into();
                }
            });
            ui.add_space(15.);
            ui.label(
                RichText::new(locale::tr("Trafik yönlendirme")).font(egui::FontId::new(
                    21.,
                    egui::FontFamily::Name("bold".into()),
                )),
            );
            ui.add_space(8.);
            ui.label(
                RichText::new(locale::tr("Uygulamalar için bağlantı kurallarını seç."))
                    .size(15.)
                    .color(SUB),
            );
            ui.add_space(18.);
            if let Some(scope) = widgets::segments(
                ui,
                "scope",
                &["Tüm trafik", "Yalnızca seçilenler", "Seçilenler hariç"],
                n(&self.settings, "scope") as usize,
            ) {
                self.settings["scope"] = json!(scope);
                self.save();
            }
            ui.add_space(9.);

            let rules = self.settings["rules"]
                .as_array()
                .cloned()
                .unwrap_or_default();
            let mut changes = Vec::new();
            let mut edit = None;
            let mut add = false;
            for row in 0..(rules.len() + 1).div_ceil(2) {
                ui.columns(2, |columns| {
                    for (col, ui) in columns.iter_mut().enumerate() {
                        let i = row * 2 + col;
                        if i > rules.len() {
                            continue;
                        }
                        card(ui, |ui| {
                            ui.set_min_width(ui.available_width());
                            ui.set_min_height(62.);
                            ui.spacing_mut().item_spacing.x = 12.;
                            if i == rules.len() {
                                let (r, response) = ui.allocate_exact_size(
                                    Vec2::new(ui.available_width(), 62.),
                                    egui::Sense::click(),
                                );
                                let icon = egui::Rect::from_center_size(
                                    r.left_center() + Vec2::new(23., 0.),
                                    Vec2::splat(46.),
                                );
                                ui.painter()
                                    .rect_filled(icon, 10., Color32::from_rgb(39, 36, 52));
                                ui.painter().text(
                                    icon.center(),
                                    egui::Align2::CENTER_CENTER,
                                    "+",
                                    egui::FontId::proportional(25.),
                                    ACCENT,
                                );
                                ui.painter().text(
                                    r.left_center() + Vec2::new(58., 0.),
                                    egui::Align2::LEFT_CENTER,
                                    locale::tr("Kural ekle"),
                                    egui::FontId::new(15., egui::FontFamily::Name("bold".into())),
                                    INK,
                                );
                                if response.clicked() {
                                    add = true;
                                }
                                return;
                            }
                            let rule = &rules[i];
                            ui.horizontal(|ui| {
                                let (r, _) =
                                    ui.allocate_exact_size(Vec2::splat(46.), egui::Sense::hover());
                                ui.painter()
                                    .rect_filled(r, 10., Color32::from_rgb(39, 36, 52));
                                let label = rule_display_name(rule);
                                let initial = if n(rule, "type") < 5 {
                                    "IP".to_owned()
                                } else {
                                    label
                                        .chars()
                                        .next()
                                        .unwrap_or('A')
                                        .to_uppercase()
                                        .to_string()
                                };
                                ui.painter().text(
                                    r.center(),
                                    egui::Align2::CENTER_CENTER,
                                    initial,
                                    egui::FontId::proportional(12.),
                                    INK,
                                );
                                let controls = 80.;
                                let label_width = (ui.available_width() - controls - 8.).max(30.);
                                ui.allocate_ui_with_layout(
                                    Vec2::new(label_width, 36.),
                                    egui::Layout::top_down(egui::Align::LEFT),
                                    |ui| {
                                        ui.set_min_width(label_width);
                                        ui.add(
                                            egui::Label::new(
                                                RichText::new(label).size(15.).strong(),
                                            )
                                            .truncate(),
                                        )
                                        .on_hover_text(s(rule, "value"));
                                        small(
                                            ui,
                                            if n(rule, "type") == 5 {
                                                "Uygulama"
                                            } else {
                                                types(n(rule, "type"))
                                            },
                                        );
                                    },
                                );
                                let mut enabled = b(rule, "enabled");
                                if widgets::toggle(ui, &mut enabled).changed() {
                                    changes.push((i, enabled));
                                }
                                let (r, response) =
                                    ui.allocate_exact_size(Vec2::splat(18.), egui::Sense::click());
                                widgets::draw_icon(ui.painter(), r.center(), "pencil", SUB);
                                if response
                                    .on_hover_text(locale::tr("Kuralı düzenle"))
                                    .clicked()
                                {
                                    edit = Some(i);
                                }
                            });
                        });
                    }
                });
                ui.add_space(1.);
            }
            if let Some(i) = edit {
                self.rule = Some(rules[i].clone());
                self.rule_index = Some(i);
                self.rule_error.clear();
            }
            if add {
                self.add_rule();
            }
            if !changes.is_empty() {
                for (i, value) in changes {
                    self.settings["rules"][i]["enabled"] = json!(value);
                }
                self.save();
            }
        });
        self.routing_lock_notice(ui);
    }
    fn routing_lock_notice(&self, ui: &mut egui::Ui) {
        if self.connected {
            ui.add_space(8.);
            small(ui, "Motor \u{00e7}al\u{0131}\u{015f}\u{0131}rken kural, metot ve profil de\u{011f}i\u{015f}tirilemez. \u{00d6}nce ba\u{011f}lant\u{0131}y\u{0131} kes.");
        }
    }
    fn settings_page(&mut self, ui: &mut egui::Ui) {
        title(ui, "Tercihler", "Ayarlar");
        small(ui, "OpenPrism\u{2019}i kendine g\u{00f6}re ayarla.");
        settings_view::section(ui, "Ba\u{015f}lang\u{0131}\u{00e7}");
        card(ui, |ui| {
            let mut auto = b(&self.settings, "autoConnect");
            if settings_view::toggle(ui, "Hizmeti başlat ve son profile bağlan", "Windows a\u{00e7}\u{0131}l\u{0131}\u{015f}\u{0131}nda son kullan\u{0131}lan profille ba\u{011f}lan.", &mut auto, self.pending == 0) {
                self.settings["autoConnect"] = json!(auto);
                self.save();
                self.call("startup", vec![], Some(json!({"enabled": auto})));
            }
            ui.separator();
            #[cfg(windows)]
            {
                let mut enabled = self.startup.enabled.unwrap_or(false);
                if settings_view::toggle(ui, "Arayüzü başlat", "Bilgisayar\u{0131}n\u{0131} a\u{00e7}t\u{0131}\u{011f}\u{0131}nda OpenPrism da a\u{00e7}\u{0131}ls\u{0131}n.", &mut enabled,
                    self.startup.enabled.is_some() && !self.startup.pending && !self.offline) {
                    if let Err(error) = self.startup.set(enabled) { Self::log_line(&mut self.local_logs, &error); self.notify_failure("startup"); }
                }
            }
        });
        if settings_view::general(ui, &mut self.settings) {
            self.save();
        }
        settings_view::section(ui, "Diagnostics");
        if ui
            .add_enabled(
                self.pending == 0 && !self.offline,
                egui::Button::new("Export diagnostics"),
            )
            .clicked()
        {
            if let Some(path) = rfd::FileDialog::new()
                .set_file_name("openprism-diagnostics.json")
                .save_file()
            {
                self.call(
                    "diagnostics-export",
                    vec![path.to_string_lossy().into_owned()],
                    None,
                );
            }
        }
        if ui
            .add_enabled(
                self.pending == 0 && !self.offline && !self.connected,
                egui::Button::new(locale::tr("İmzalı güncelleme paketi uygula")),
            )
            .clicked()
        {
            if let Some(path) = rfd::FileDialog::new().pick_folder() {
                self.call(
                    "apply-update",
                    vec![path.to_string_lossy().into_owned()],
                    None,
                );
            }
        }
        if settings_view::about(ui, &self.update_snapshot, self.pending != 0) {
            self.call("check-updates", vec![], None);
        }
    }
    fn services(&mut self, ui: &mut egui::Ui) {
        title(ui, "Sistem", "Hizmetler");
        ui.label(
            RichText::new(locale::tr("Bağlantı bileşenlerini kur, onar veya kaldır."))
                .size(15.)
                .color(SUB),
        );
        ui.add_space(26.);
        let installed = b(&self.scan, "serviceInstalled");
        card(ui, |ui| {
            for (index, name) in ["OpenPrism", "ByeDPI", "Zapret"].iter().enumerate() {
                if index > 0 {
                    ui.separator();
                }
                let present = if index == 0 {
                    installed
                } else {
                    self.scan["components"].as_array().is_some_and(|items| {
                        items
                            .iter()
                            .any(|item| s(item, "name") == *name && b(item, "installed"))
                    })
                };
                ui.horizontal(|ui| {
                    ui.set_min_height(64.);
                    let name_width = ui.available_width() * 0.36;
                    ui.allocate_ui_with_layout(
                        Vec2::new(name_width, 42.),
                        egui::Layout::top_down(egui::Align::LEFT),
                        |ui| {
                            ui.set_min_width(name_width);
                            ui.label(RichText::new(*name).font(egui::FontId::new(
                                15.,
                                egui::FontFamily::Name("bold".into()),
                            )));
                            small(
                                ui,
                                if index == 0 {
                                    "Bağlantı hizmeti"
                                } else {
                                    "Bağlantı bileşeni"
                                },
                            );
                        },
                    );
                    ui.label(
                        RichText::new(locale::tr(if present { "Kurulu" } else { "Kurulu değil" }))
                            .color(if present {
                                SUB
                            } else {
                                Color32::from_rgb(239, 201, 131)
                            }),
                    );
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        let enabled = self.pending == 0 && !self.connected && !self.offline;
                        if present && widgets::removal_button(ui, "Kaldır", enabled).clicked()
                        {
                            if index == 0 {
                                self.remove_confirm = true;
                            } else {
                                self.call("remove-component", vec![name.to_lowercase()], None);
                            }
                        }
                        if (index != 0 || present) && ui
                            .add_enabled(
                                enabled,
                                egui::Button::new(locale::tr(if present { "Onar" } else { "Kur" })),
                            )
                            .clicked()
                        {
                            if index == 0 {
                                self.call("install-service", vec!["core".into()], None);
                            } else {
                                self.call("install-component", vec![name.to_lowercase()], None);
                            }
                        }
                        if index == 0 && !present {
                            small(ui, "Hizmeti OpenPrism Kurulumu ile yükle.");
                        }
                    });
                });
            }
        });
    }
    fn logs_page(&mut self, ui: &mut egui::Ui) {
        let mut detailed = b(&self.settings, "detailedLogs");
        let actions = self.log_view.render(
            ui,
            &self.local_logs,
            &self.backend_logs,
            &self.logs_error,
            &mut detailed,
            self.pending != 0,
        );
        if actions.detailed_changed {
            self.settings["detailedLogs"] = json!(detailed);
            self.save();
        }
        if actions.clear {
            self.local_logs.clear();
            self.backend_logs.clear();
            self.call("clear-logs", vec![], None);
        }
        if actions.refresh {
            self.call("logs", vec![], None);
        }
    }
    fn add_rule(&mut self) {
        self.rule = Some(
            json!({"id":uuid::Uuid::new_v4().to_string(),"type":1,"value":"","name":"","enabled":true}),
        );
        self.rule_index = None;
        self.rule_error.clear();
    }
    fn dialogs(&mut self, ctx: &egui::Context) {
        if self.connected {
            self.rule = None;
            self.dpi_dialog = false;
        }
        if let Some(mut rule) = self.rule.clone() {
            let action = rule_editor::render(
                ctx,
                &mut rule,
                self.rule_index.is_some(),
                self.validating || self.connected || self.pending != 0,
                &self.rule_error,
            );
            self.rule = Some(rule.clone());
            match action {
                rule_editor::Action::Save => {
                    self.rule_error.clear();
                    if self.offline {
                        self.rule_error = "Offline preview".into();
                    } else {
                        self.validating = true;
                        self.call("validate-rule", vec![], Some(rule));
                    }
                }
                rule_editor::Action::Cancel => {
                    self.rule = None;
                    self.rule_error.clear();
                }
                rule_editor::Action::Delete => {
                    if let Some(i) = self.rule_index {
                        if let Some(rules) = self.settings["rules"].as_array_mut() {
                            if i < rules.len() {
                                rules.remove(i);
                            }
                        }
                        self.rule = None;
                        self.save();
                    }
                }
                rule_editor::Action::None => {}
            }
        }
        if self.remove_confirm {
            egui::Window::new(locale::tr("Hizmeti kaldır"))
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, Vec2::ZERO)
                .default_width(360.)
                .frame(
                    egui::Frame::window(&ctx.style())
                        .fill(PANEL)
                        .stroke(Stroke::new(1_f32, LINE))
                        .inner_margin(20)
                        .corner_radius(12),
                )
                .show(ctx, |ui| {
                    ui.label(locale::tr(
                        "WGTun hizmeti ve uygulamanın kurduğu tüm bileşenler kaldırılsın mı?",
                    ));
                    small(
                        ui,
                        "Etkin bağlantı kesilecek. Yeniden kullanmak için kurulum gerekir.",
                    );
                    ui.add_space(8.);
                    ui.horizontal(|ui| {
                        if ui.button(locale::tr("Kaldır")).clicked() {
                            self.remove_confirm = false;
                            self.call("remove-service", vec![], None);
                        }
                        if ui.button(locale::tr("Vazgeç")).clicked() {
                            self.remove_confirm = false;
                        }
                    });
                });
        }
    }
}
impl App {
    fn draw(&mut self, ctx: &egui::Context) {
        self.replies();
        loop {
            let action = self.tray.as_ref().and_then(|tray| tray.action());
            match action {
                Some(tray::Action::Open) => {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                    ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                }
                Some(tray::Action::Disconnect) => {
                    if self.pending == 0 {
                        self.call("disconnect", vec![], None);
                    }
                }
                Some(tray::Action::Exit) => {
                    self.quitting = true;
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
                None => break,
            }
        }
        if !self.offline
            && !self.quitting
            && self.tray.is_some()
            && b(&self.settings, "closeToTray")
            && ctx.input(|i| i.viewport().close_requested())
        {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
        }

        if let Some(path) = self.capture.clone() {
            let shot = ctx.input(|i| {
                i.events.iter().find_map(|e| {
                    if let egui::Event::Screenshot { image, .. } = e {
                        Some(image.clone())
                    } else {
                        None
                    }
                })
            });
            if let Some(image) = shot {
                let bytes: Vec<u8> = image.pixels.iter().flat_map(|p| p.to_array()).collect();
                if let Err(e) = image::save_buffer(
                    &path,
                    &bytes,
                    image.size[0] as u32,
                    image.size[1] as u32,
                    image::ColorType::Rgba8,
                ) {
                    eprintln!("Capture failed: {e}");
                }
                self.capture = None;
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            } else if self.capture_frame < 2 {
                self.capture_frame += 1;
                ctx.request_repaint();
            } else if self.capture_frame == 2 {
                self.capture_frame = 3;
                ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(Default::default()));
            }
        }
        egui::SidePanel::left("sidebar")
            .exact_width(252.)
            .frame(
                egui::Frame::new()
                    .fill(BG)
                    .stroke(Stroke::new(1_f32, LINE))
                    .inner_margin(22),
            )
            .show(ctx, |ui| {
                ui.add_space(26.);
                ui.spacing_mut().item_spacing.y = 0.;
                ui.horizontal(|ui| {
                    widgets::icon(ui, "prism", ACCENT, 36.);
                    ui.label(branding::wordmark(egui::FontId::new(22., egui::FontFamily::Name("bold".into()))));
                });
                ui.add_space(54.);
                for (id, label) in [
                    ("connection", "Bağlantı"),
                    ("profiles", "Profiller"),
                    ("services", "Hizmetler"),
                    ("settings", "Ayarlar"),
                ] {
                    if widgets::nav(ui, id, label, self.page == id) {
                        if id == "logs" && self.page != "logs" {
                            self.call("logs", vec![], None);
                        }
                        if id == "services" && self.page != "services" && !self.offline {
                            self.call("scan", vec![], None);
                        }
                        self.page = id.into();
                    }
                    ui.add_space(6.);
                }
            });
        egui::CentralPanel::default()
            .frame(
                egui::Frame::new()
                    .fill(BG)
                    .inner_margin(egui::Margin { left: 63, right: 33, top: 68, bottom: 24 }),
            )
            .show(ctx, |ui| {
                if self.service_fault {
                    egui::Frame::new().fill(Color32::from_rgb(90, 32, 37)).inner_margin(10).show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        ui.horizontal_wrapped(|ui| {
                            ui.label(locale::tr("Hizmete ula\u{015f}\u{0131}lam\u{0131}yor. OpenPrism hizmetini kurun veya onar\u{0131}n."));
                            if ui.button(locale::tr("Hizmetler")).clicked() { self.page = "services".into(); }
                        });
                    });
                }
                if !self.toasts.is_empty() {
                    egui::TopBottomPanel::bottom("toast_stack").resizable(false).show_inside(ui, |ui| {
                        let mut remove = None;
                        for (index, (message, started)) in self.toasts.iter().enumerate() {
                            let age = (ui.input(|i| i.time) - started).max(0.0);
                            let alpha = ((age * 5.0).min(1.0) * ((10.0 - age) * 3.0).clamp(0.0, 1.0)) as f32;
                            egui::Frame::new().fill(Color32::from_rgba_unmultiplied(36, 32, 36, (255.0 * alpha) as u8)).stroke(Stroke::new(1_f32, Color32::from_rgb(103,64,71))).inner_margin(12).corner_radius(10).show(ui, |ui| {
                                ui.horizontal_top(|ui| {
                                    ui.colored_label(Color32::from_rgb(255,133,138), "●");
                                    let width = (ui.available_width() - 36.).max(100.);
                                    ui.allocate_ui_with_layout(Vec2::new(width, 0.), egui::Layout::top_down(egui::Align::LEFT), |ui| {
                                        ui.set_min_width(width);
                                        ui.label(RichText::new(locale::tr("İşlem başarısız")).font(egui::FontId::new(12.,egui::FontFamily::Name("bold".into()))));
                                        ui.label(RichText::new(message).size(12.).color(Color32::from_rgb(193,171,176)));
                                        if ui.link(RichText::new(locale::tr("Hizmetler →")).size(12.).color(Color32::from_rgb(240,184,189))).clicked() { self.page = "services".into(); }
                                    });
                                    if ui.button("×").clicked() { remove = Some(index); }
                                });
                            });
                            if age >= 10.0 { remove = Some(index); } else { ui.ctx().request_repaint_after(std::time::Duration::from_millis(32)); }
                        }
                        if let Some(index) = remove { self.toasts.remove(index); }
                    });
                }
                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        match self.page.as_str() {
                            "profiles" => self.profiles(ui),
                            "settings" => self.settings_page(ui),
                            "services" => self.services(ui),
                            "onboarding" => self.services(ui),
                            "logs" => self.logs_page(ui),
                            _ => self.connection(ui),
                        }
                        ui.add_space(10.);

                    });
            });
        self.dialogs(ctx);
    }
}
impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _: &mut eframe::Frame) {
        self.draw(ctx);
    }
}
impl egui_software_backend::App for App {
    fn update(&mut self, ctx: &egui::Context, _: &mut egui_software_backend::SoftwareBackend) {
        self.draw(ctx);
    }
}
fn app_icon() -> egui::IconData {
    branding::window_icon()
}
fn main() -> eframe::Result {
    let args: Vec<String> = std::env::args().collect();
    let background = args.iter().any(|a| {
        a == "--background"
            || a == "--startup"
            || a == "--smoke-no-network"
            || a == "--screenshot-friendly"
            || a.starts_with("--capture=")
    });
    let hidden = args.iter().any(|a| a == "--background" || a == "--startup")
        && !args
            .iter()
            .any(|a| a == "--smoke-no-network" || a.starts_with("--capture="));
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("OpenPrism")
            .with_icon(app_icon())
            .with_inner_size([1200., 920.])
            .with_min_inner_size([900., 560.])
            .with_active(!background)
            .with_visible(!hidden),
        renderer: eframe::Renderer::Glow,
        ..Default::default()
    };
    if !args
        .iter()
        .any(|a| a == "--glow" || a.starts_with("--capture="))
    {
        return software_runtime::run(options.viewport, App::new_context)
            .map_err(eframe::Error::AppCreation);
    }
    eframe::run_native(
        "OpenPrism",
        options,
        Box::new(|cc| Ok(Box::new(App::new(cc)))),
    )
}

#[cfg(test)]
mod state_tests {
    use super::*;
    #[test]
    fn folder_labels_keep_matching_paths_and_custom_names() {
        let rule = json!({"type":7,"value":"C:\\Program Files (x86)\\Microsoft\\Edge\\","name":"C:\\Program Files (x86)\\Microsoft\\Edge\\"});
        assert_eq!(rule_display_name(&rule), "Edge");
        assert_eq!(
            s(&rule, "value"),
            "C:\\Program Files (x86)\\Microsoft\\Edge\\"
        );
        let custom = json!({"type":7,"value":"C:\\Apps\\Edge","name":"Tarayıcılar"});
        assert_eq!(rule_display_name(&custom), "Tarayıcılar");
        assert_eq!(default_rule_name(7, "C:\\"), "C:\\");
    }
    #[test]
    fn rejects_operation_result_as_settings() {
        assert!(!valid_settings(&json!({"success":false,"message":"error"})));
        assert!(valid_settings(&json!({"rules":[],"dpi":{},"profile":0})));
    }
}
