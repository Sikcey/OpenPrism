#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod bridge;
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

const BG: Color32 = Color32::from_rgb(19, 22, 25);
const PANEL: Color32 = Color32::from_rgb(27, 31, 35);
const LINE: Color32 = Color32::from_rgb(48, 55, 60);
const ACCENT: Color32 = Color32::from_rgb(32, 175, 146);
const SOFT: Color32 = Color32::from_rgb(23, 59, 52);
const SUB: Color32 = Color32::from_rgb(160, 171, 178);
const INK: Color32 = Color32::from_rgb(237, 242, 244);
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
fn button(ui: &mut egui::Ui, text: &str, selected: bool) -> bool {
    ui.add(
        egui::Button::new(RichText::new(locale::tr(text)).color(if selected { INK } else { SUB }))
            .fill(if selected { SOFT } else { PANEL })
            .stroke(Stroke::NONE)
            .corner_radius(7)
            .min_size(Vec2::new(0., 30.)),
    )
    .clicked()
}
fn small(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(locale::tr(text)).size(11.).color(SUB));
}
fn title(ui: &mut egui::Ui, eyebrow: &str, title: &str) {
    ui.label(
        RichText::new(locale::tr(eyebrow).to_uppercase())
            .size(10.)
            .color(SUB),
    );
    ui.label(RichText::new(locale::tr(title)).size(25.).strong());
    ui.add_space(12.);
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
    connected: bool,
    offline: bool,
    pending: usize,
    preserve_notice: bool,
    account: usize,
    license: String,
    rule: Option<Value>,
    rule_index: Option<usize>,
    log_view: log_view::LogView,
    update_snapshot: Value,
    validating: bool,
    rule_error: String,
    dpi_dialog: bool,
    dpi_draft: Value,
    warp_attempted: bool,
    warp_preparing: bool,
    warp_error: Option<String>,
    remove_confirm: bool,
    connect_after: bool,
    startup_loaded: bool,
    startup_resume_pending: bool,
    setup_step: u8,
    setup_installing: bool,
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
            for action in ["load-settings", "scan", "status", "presets"] {
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
            connected: false,
            offline,
            pending: initial_pending,
            preserve_notice: false,
            account: 0,
            license: String::new(),
            rule: None,
            rule_index: None,
            log_view: Default::default(),
            update_snapshot: Value::Null,
            validating: false,
            rule_error: String::new(),
            dpi_dialog: false,
            dpi_draft: json!({}),
            warp_attempted: false,
            warp_preparing: false,
            warp_error: None,
            remove_confirm: false,
            connect_after: false,
            startup_loaded: false,
            startup_resume_pending: true,
            setup_step: 0,
            setup_installing: false,
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
            result.settings["rules"] = json!([
                {"id":"fixture-discord","type":5,"name":"Discord","value":"Discord.exe","enabled":true},
                {"id":"fixture-edge","type":5,"name":"Microsoft Edge","value":"msedge.exe","enabled":true},
                {"id":"fixture-network","type":3,"name":"Yerel ağ","value":"192.168.0.0/16","enabled":false}
            ]);
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
    fn call(&mut self, a: &str, args: Vec<String>, v: Option<Value>) {
        if self.offline {
            self.notice = "Çevrimdışı görünüm: işlem yapılmadı".into();
            return;
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
            self.notice = error;
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
                    self.notice = e;
                    if reply.action == "goodcheck" {
                        self.goodcheck_running = false;
                        self.goodcheck_result = json!({"code":"error","message":self.notice});
                    }
                    if reply.action == "logs" {
                        self.logs_error = self.notice.clone();
                    }
                    if reply.action == "install-service" {
                        self.setup_installing = false;
                    }
                    self.validating = false;
                    if reply.action == "validate-rule" {
                        self.rule_error = self.notice.clone();
                    }
                    if reply.action == "prepare-warp" {
                        self.warp_preparing = false;
                        self.warp_error = Some(self.notice.clone());
                        self.connect_after = false;
                    }
                }
                Ok(v) => match reply.action.as_str() {
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
                                json!(if n(&self.settings, "profile") == 2 {
                                    1
                                } else {
                                    0
                                });
                            self.startup_loaded = true;
                            if self.startup_resume_pending
                                && !self.scan.is_null()
                                && b(&self.scan, "serviceReady")
                            {
                                self.startup_resume_pending = false;
                                if b(&self.settings, "autoConnect") && !self.inspect_only {
                                    self.call("resume", vec![], None);
                                }
                            }
                        } else {
                            self.notice = s(&v, "message");
                        }
                    }
                    "scan" => {
                        self.scan = v;
                        if self.page == "onboarding"
                            && self.setup_step == 2
                            && b(&self.scan, "serviceReady")
                        {
                            self.setup_step = 3;
                        }
                        let initial_scan = self.startup_resume_pending;
                        let resume = initial_scan && self.startup_loaded;
                        if resume {
                            self.startup_resume_pending = false;
                        }
                        if !b(&self.scan, "serviceReady") {
                            if initial_scan && self.page == "connection" {
                                self.page = "onboarding".into();
                            }
                        } else if resume
                            && self.startup_loaded
                            && b(&self.settings, "autoConnect")
                            && !self.inspect_only
                        {
                            self.call("resume", vec![], None);
                        }
                    }
                    "status" => {
                        self.connected = b(&v, "connected");
                        if !self.preserve_notice {
                            self.notice = s(&v, "message");
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
                        self.notice = s(&v, "message");
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
                            s(&v, "message")
                        };
                        self.notice = s(&v, "message");
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
                    "prepare-warp" => {
                        self.warp_preparing = false;
                        self.notice = s(&v, "message");
                        self.warp_error = if b(&v, "success") {
                            None
                        } else {
                            Some(self.notice.clone())
                        };
                        if b(&v, "success") {
                            self.call("scan", vec![], None);
                        }
                        if !b(&v, "success") {
                            self.connect_after = false;
                        }
                        if b(&v, "success") && self.connect_after {
                            self.connect_after = false;
                            self.page = "connection".into();
                            self.call("connect", vec![], Some(self.settings.clone()));
                        }
                    }
                    "connect" | "disconnect" | "resume" => {
                        self.notice = s(&v, "message");
                        self.preserve_notice = !b(&v, "success");
                        self.call("status", vec![], None);
                    }
                    "install-service" => {
                        self.notice = s(&v, "message");
                        self.setup_installing = false;
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
                            self.logs_error = s(&v, "message");
                        }
                    }
                    "remove-service" => {
                        self.notice = s(&v, "message");
                        self.call("scan", vec![], None);
                    }
                    "import-wireguard" | "import-warp" => {
                        self.notice = s(&v, "message");
                        if b(&v, "success") {
                            self.call("load-settings", vec![], None);
                            self.call("scan", vec![], None);
                        }
                    }
                    "save-settings" => {
                        if !b(&v, "success") {
                            self.notice = s(&v, "message");
                        }
                    }
                    _ => {
                        self.notice = s(&v, "message");
                    }
                },
            }
        }
        if !self.offline
            && !self.inspect_only
            && !self.warp_attempted
            && self.startup_loaded
            && self.scan.is_object()
            && b(&self.scan, "serviceReady")
            && n(&self.settings, "profile") == 0
            && !b(&self.scan, "hasPreparedWarpProfile")
            && self.pending == 0
        {
            self.prepare_warp();
        }
    }
    fn prepare_warp(&mut self) {
        self.warp_attempted = true;
        self.warp_error = None;
        if self.bridge.call(
            "prepare-warp",
            vec!["--accept-cloudflare-terms".into()],
            None,
        ) {
            self.pending += 1;
            self.warp_preparing = true;
            self.notice = "Cloudflare hesabı hazırlanıyor…".into();
        } else {
            self.warp_error = Some("Cloudflare hesabı hazırlama işlemi başlatılamadı.".into());
        }
    }
    fn connection(&mut self, ui: &mut egui::Ui) {
        title(ui, "Senin ağın, senin kuralların", "Bağlantın kontrolünde.");
        let profile = n(&self.settings, "profile");
        card(ui, |ui| {
            ui.set_min_width(ui.available_width());
            ui.vertical_centered(|ui| {
                ui.add_space(10.);
                let (r, response) = ui.allocate_exact_size(Vec2::splat(92.), egui::Sense::click());
                let c = r.center();
                let p = ui.painter();
                p.circle_stroke(c, 44., Stroke::new(1.0_f32, LINE));
                p.circle_filled(c, 34., SOFT);
                p.circle_stroke(c, 34., Stroke::new(1.0_f32, ACCENT));
                let points: Vec<_> = (0..=40)
                    .map(|i| {
                        let a = 0.65 + i as f32 / 40. * (std::f32::consts::TAU - 1.3);
                        c + Vec2::new(a.sin() * 13., -a.cos() * 13.)
                    })
                    .collect();
                p.add(egui::Shape::line(points, Stroke::new(2.0_f32, ACCENT)));
                p.line_segment(
                    [c + Vec2::new(0., -18.), c + Vec2::new(0., -1.)],
                    Stroke::new(2.0_f32, ACCENT),
                );
                if response
                    .on_hover_text(locale::tr("Bağlan / bağlantıyı kes"))
                    .clicked()
                    && self.pending == 0
                {
                    if self.connected {
                        self.call("disconnect", vec![], None);
                    } else if !b(&self.scan, "serviceReady") && !self.offline {
                        self.page = "onboarding".into();
                    } else if profile == 3 && !self.scan["components"].as_array().is_some_and(|items| items.iter().any(|item| s(item, "name") == "Zapret" && b(item, "installed"))) {
                        self.page = "services".into();
                        self.notice = locale::tr("Zapret deste\u{011f}i i\u{00e7}in Hizmetler ekran\u{0131}ndan hizmeti kur / onar. Kurulu hizmet eski veya Zapret dosyalar\u{0131} eksik.").into();
                        self.preserve_notice = true;
                    } else if profile == 0 && !b(&self.scan, "hasPreparedWarpProfile") {
                        self.connect_after = true;
                        self.page = "profiles".into();
                    } else {
                        self.call("connect", vec![], Some(self.settings.clone()));
                    }
                }
                ui.add_space(6.);
                ui.label(
                    RichText::new(locale::tr(if self.pending > 0 {
                        "İşlem sürüyor…"
                    } else if self.connected {
                        "Motor çalışıyor."
                    } else {
                        "Bağlanmaya hazırsın."
                    }))
                    .size(21.)
                    .strong(),
                );
                small(
                    ui,
                    if self.connected {
                        "Bağlantı motoru etkin"
                    } else {
                        "Bağlantı kapalı"
                    },
                );
                ui.add_space(12.);
            });
            ui.separator();
            ui.add_space(5.);
            ui.horizontal(|ui| {
                widgets::icon(
                    ui,
                    if profile == 0 { "cloud" } else { "profiles" },
                    if profile == 0 {
                        Color32::from_rgb(225, 139, 53)
                    } else {
                        ACCENT
                    },
                    32.,
                );
                ui.vertical(|ui| {
                    ui.label(
                        RichText::new(locale::tr(match profile {
                            1 => "Özel WireGuard Profili",
                            2 => "DPI aşma",
                            3 => "Zapret",
                            _ => "Cloudflare WARP",
                        }))
                        .size(13.)
                        .strong(),
                    );
                    small(ui, "Seçili profil");
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if button(ui, "Değiştir", false) {
                        self.page = "profiles".into();
                    }
                });
            });
        });
        let locked = self.connected || self.pending != 0;
        ui.add_enabled_ui(!locked, |ui| {
            ui.add_space(15.);
            ui.label(
                RichText::new(locale::tr("Trafik yönlendirme"))
                    .size(14.)
                    .strong(),
            );
            ui.add_space(5.);
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
            if n(&self.settings, "scope") == 0 {
                return;
            }
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
                            ui.set_min_height(43.);
                            if i == rules.len() {
                                if ui
                                    .add_sized(
                                        [ui.available_width(), 43.],
                                        egui::Button::new(
                                            RichText::new(locale::tr("+  Kural ekle"))
                                                .size(12.)
                                                .color(SUB),
                                        )
                                        .fill(PANEL)
                                        .stroke(Stroke::NONE),
                                    )
                                    .clicked()
                                {
                                    add = true;
                                }
                                return;
                            }
                            let rule = &rules[i];
                            ui.horizontal(|ui| {
                                let (r, _) =
                                    ui.allocate_exact_size(Vec2::splat(30.), egui::Sense::hover());
                                ui.painter().rect_filled(r, 8., BG);
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
                                let controls = 49.;
                                let label_width = (ui.available_width() - controls - 8.).max(30.);
                                ui.allocate_ui_with_layout(
                                    Vec2::new(label_width, 36.),
                                    egui::Layout::top_down(egui::Align::LEFT),
                                    |ui| {
                                        ui.add(
                                            egui::Label::new(
                                                RichText::new(label).size(12.).strong(),
                                            )
                                            .truncate(),
                                        )
                                        .on_hover_text(s(rule, "value"));
                                        small(ui, types(n(rule, "type")));
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
        small(ui, "WGTun\u{2019}u kendine g\u{00f6}re ayarla.");
        if settings_view::general(ui, &mut self.settings) {
            self.save();
        }
        settings_view::section(ui, "Ba\u{015f}lang\u{0131}\u{00e7}");
        card(ui, |ui| {
            #[cfg(windows)]
            {
                let mut enabled = self.startup.enabled.unwrap_or(false);
                if settings_view::toggle(ui, "Windows ile ba\u{015f}lat", "Bilgisayar\u{0131}n\u{0131} a\u{00e7}t\u{0131}\u{011f}\u{0131}nda WGTun da a\u{00e7}\u{0131}ls\u{0131}n.", &mut enabled,
                    self.startup.enabled.is_some() && !self.startup.pending && !self.offline) {
                    if let Err(error) = self.startup.set(enabled) { self.notice = error; }
                }
            }
            ui.separator();
            let mut auto = b(&self.settings, "autoConnect");
            if settings_view::toggle(ui, "Otomatik ba\u{011f}lan", "Windows a\u{00e7}\u{0131}l\u{0131}\u{015f}\u{0131}nda son kullan\u{0131}lan profille ba\u{011f}lan.", &mut auto, self.pending == 0) {
                self.settings["autoConnect"] = json!(auto);
                self.save();
                self.call("startup", vec![], Some(json!({"enabled": auto})));
            }
        });
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
        let installed = b(&self.scan, "serviceInstalled");
        if ui
            .add_enabled(
                self.pending == 0 && !self.connected,
                egui::Button::new(locale::tr(if installed {
                    "Hizmeti kur / onar"
                } else {
                    "Hizmeti kur"
                })),
            )
            .clicked()
        {
            self.call("install-service", vec!["core".into()], None);
        }
        ui.add_space(12.);
        card(ui, |ui| {
            for name in [
                "WGTun",
                "sing-box",
                "Capture",
                "Windows Packet Filter",
                "ByeDPI",
                "Zapret",
            ] {
                if name != "WGTun" {
                    ui.separator();
                }
                let component = self.scan["components"]
                    .as_array()
                    .and_then(|items| items.iter().find(|item| s(item, "name") == name))
                    .cloned()
                    .unwrap_or_default();
                let present = if name == "WGTun" {
                    installed
                } else {
                    b(&component, "installed")
                };
                ui.horizontal(|ui| {
                    ui.strong(if name == "Capture" {
                        locale::tr("Uygulama yakalama")
                    } else {
                        name
                    });
                    let version = s(&component, "version");
                    if !version.is_empty() {
                        small(ui, &format!("v{version}"));
                    }
                    if ["ByeDPI", "Zapret"].contains(&name)
                        && installed
                        && ui
                            .add_enabled(
                                self.pending == 0 && !self.connected,
                                egui::Button::new(locale::tr(if present {
                                    "Kaldır"
                                } else {
                                    "Kur"
                                })),
                            )
                            .clicked()
                    {
                        self.call(
                            if present {
                                "remove-component"
                            } else {
                                "install-component"
                            },
                            vec![name.to_lowercase()],
                            None,
                        );
                    }
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        small(
                            ui,
                            if b(&component, "running") {
                                "Çalışıyor"
                            } else if present {
                                "Hazır"
                            } else {
                                "Kurulu değil"
                            },
                        );
                    });
                });
                if !present {
                    small(ui, &s(&component, "message"));
                }
            }
        });
    }
    fn service_actions(&mut self, ui: &mut egui::Ui) {
        let installed = b(&self.scan, "serviceInstalled");
        ui.add_space(16.);
        small(ui, "WGTun hizmeti, sing-box ve uygulama yakalama bileşeni gereklidir; tüm hizmetleri kaldır işlemiyle kaldırılır.");
        ui.add_space(8.);
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui
                .add_enabled(
                    self.pending == 0 && installed && !self.connected,
                    egui::Button::new(locale::tr("Tüm hizmetleri kaldır")),
                )
                .clicked()
            {
                self.remove_confirm = true;
            }
        });
    }
    fn onboarding(&mut self, ui: &mut egui::Ui) {
        let locked = self.connected;
        ui.add_enabled_ui(!locked, |ui| {
        title(ui, "İlk kurulum", "OpenPrism'e hoş geldin");
        small(ui, &format!("{} {} / 4", locale::tr("Adım"), self.setup_step + 1));
        ui.add_space(10.);
        card(ui, |ui| match self.setup_step {
            0 => {
                ui.strong(locale::tr("Bağlantı yöntemini seç"));
                small(ui, "WireGuard, varsayılan olarak Cloudflare WARP kullanır. ByeDPI alternatif bir yöntemdir.");
                if let Some(method) = widgets::segments(
                    ui,
                    "setup-method",
                    &["WireGuard", "ByeDPI", "Zapret"],
                    match n(&self.settings, "profile") { 2 => 1, 3 => 2, _ => 0 },
                ) {
                    self.settings["profile"] = json!(match method { 0 => 0, 1 => 2, _ => 3 });
                    self.settings["connectionMethod"] = json!(method);
                    self.save();
                }
                if button(ui, "Devam", true) {
                    self.setup_step = 1;
                }
            }
            1 => {
                ui.strong(locale::tr("Hangi trafik yönlendirilsin?"));
                if let Some(scope) = widgets::segments(
                    ui,
                    "setup-scope",
                    &["Tüm trafik", "Seçilenler"],
                    usize::from(n(&self.settings, "scope") != 0),
                ) {
                    self.settings["scope"] = json!(scope);
                    self.save();
                }
                if n(&self.settings, "scope") != 0 {
                    let rules = self.settings["rules"]
                        .as_array()
                        .cloned()
                        .unwrap_or_default();
                    let selected = rules.iter().filter(|rule| b(rule, "enabled")).count();
                    ui.add_space(8.);
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new(locale::tr("Uygulamalar ve kurallar"))
                                .size(12.)
                                .strong(),
                        );
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            ui.label(
                                RichText::new(format!("{selected} / {} {}", rules.len(), locale::tr("seçili")))
                                    .size(11.)
                                    .color(if selected > 0 { ACCENT } else { SUB }),
                            );
                        });
                    });
                    let mut changes = Vec::new();
                    let mut edit = None;
                    egui::ScrollArea::vertical().id_salt("setup_rule_list").max_height(280.).show(ui, |ui| {
                        for (index, rule) in rules.iter().enumerate() {
                            let enabled = b(rule, "enabled");
                            egui::Frame::new().fill(BG).stroke(Stroke::new(1_f32, if enabled { SOFT } else { LINE })).corner_radius(8).inner_margin(10).show(ui, |ui| {
                                ui.set_min_width(ui.available_width());
                                ui.horizontal(|ui| {
                                    let (tile, _) = ui.allocate_exact_size(Vec2::splat(32.), egui::Sense::hover());
                                    ui.painter().rect_filled(tile, 8, if enabled { SOFT } else { PANEL });
                                    let label = rule_display_name(rule);
                                    let initial = if n(rule, "type") < 5 { "IP".to_owned() } else { label.chars().next().unwrap_or('A').to_uppercase().to_string() };
                                    ui.painter().text(tile.center(), egui::Align2::CENTER_CENTER, initial, egui::FontId::proportional(12.), if enabled { ACCENT } else { SUB });
                                    let width = (ui.available_width() - 64.).max(40.);
                                    ui.allocate_ui_with_layout(Vec2::new(width, 38.), egui::Layout::top_down(egui::Align::LEFT), |ui| {
                                        ui.add(egui::Label::new(RichText::new(label).size(13.).color(INK)).truncate()).on_hover_text(s(rule, "value"));
                                        ui.add(egui::Label::new(RichText::new(format!("{} · {}", types(n(rule, "type")), s(rule, "value"))).size(10.).color(SUB)).truncate()).on_hover_text(s(rule, "value"));
                                    });
                                    let mut value = enabled;
                                    if widgets::toggle(ui, &mut value).changed() { changes.push((index, value)); }
                                    let (rect, response) = ui.allocate_exact_size(Vec2::splat(18.), egui::Sense::click());
                                    widgets::draw_icon(ui.painter(), rect.center(), "pencil", SUB);
                                    if response.on_hover_text(locale::tr("Düzenle")).clicked() { edit = Some(index); }
                                });
                            });
                            ui.add_space(3.);
                        }
                        if rules.is_empty() {
                            ui.add_space(12.);
                            small(ui, "Henüz kural yok. Bir uygulama, klasör veya IP ekleyerek başla.");
                            ui.add_space(12.);
                        }
                    });
                    if !changes.is_empty() {
                        for (index, enabled) in changes {
                            self.settings["rules"][index]["enabled"] = json!(enabled);
                        }
                        self.save();
                    }
                    if let Some(index) = edit {
                        self.rule = Some(rules[index].clone());
                        self.rule_index = Some(index);
                        self.rule_error.clear();
                    }
                    ui.add_space(6.);
                    if button(ui, "+  Kural ekle", false) {
                        self.add_rule();
                    }
                    if selected == 0 {
                        small(ui, "Devam etmek için en az bir kuralı aç.");
                    }
                }
                ui.horizontal(|ui| {
                    if button(ui, "Geri", false) {
                        self.setup_step = 0;
                    }
                    let valid = n(&self.settings, "scope") == 0
                        || self.settings["rules"]
                            .as_array()
                            .is_some_and(|r| r.iter().any(|x| b(x, "enabled")));
                    if ui
                        .add_enabled(
                            valid && !self.validating,
                            egui::Button::new(locale::tr("Devam")),
                        )
                        .clicked()
                    {
                        self.setup_step = 2;
                    }
                });
            }
            2 => {
                ui.strong(locale::tr("Gerekli bileşenleri kur"));
                small(ui, "Kurulum gerekli dosyaları otomatik indirir ve Windows bir kez yönetici izni isteyebilir.");
                small(
                    ui,
                    if b(&self.scan, "serviceReady") {
                        "Hizmet hazır"
                    } else {
                        "Hizmet kurulumu gerekli"
                    },
                );
                small(
                    ui,
                    if b(&self.scan, "dependencyReady") {
                        "Motor hazır"
                    } else {
                        "Motor kurulumu gerekli"
                    },
                );
                if self.setup_installing {
                    ui.spinner();
                    small(ui, &self.notice);
                }
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(
                            !self.setup_installing,
                            egui::Button::new(locale::tr("Geri")),
                        )
                        .clicked()
                    {
                        self.setup_step = 1;
                    }
                    if ui
                        .add_enabled(
                            self.pending == 0 && !self.setup_installing,
                            egui::Button::new(locale::tr("Kurulumu başlat")),
                        )
                        .clicked()
                    {
                        self.setup_installing = true;
                        self.call("install-service", vec![match n(&self.settings, "profile") { 2 => "byedpi", 3 => "zapret", _ => "core" }.into()], None);
                        if self.offline {
                            self.setup_installing = false;
                        }
                    }
                });
            }
            _ => {
                ui.strong(locale::tr("Hizmet hazır"));
                small(
                    ui,
                    "Kurulum tamamlandı. Bağlantı sayfasından devam edebilirsin.",
                );
                if button(ui, "Bağlantıya geç", true) {
                    self.page = "connection".into();
                }
            }
        });
        });
        self.routing_lock_notice(ui);
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
        if self.dpi_dialog {
            self.dpi_config(ctx);
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
            .exact_width(190.)
            .frame(
                egui::Frame::new()
                    .fill(PANEL)
                    .stroke(Stroke::new(1_f32, LINE))
                    .inner_margin(12),
            )
            .show(ctx, |ui| {
                ui.add_space(15.);
                let mut word = egui::text::LayoutJob::default();
                word.append(
                    "Open",
                    0.,
                    egui::TextFormat {
                        font_id: egui::FontId::proportional(24.),
                        color: INK,
                        ..Default::default()
                    },
                );
                word.append(
                    "Prism",
                    0.,
                    egui::TextFormat {
                        font_id: egui::FontId::proportional(24.),
                        color: ACCENT,
                        ..Default::default()
                    },
                );
                ui.horizontal(|ui| {
                    ui.label(word);
                    widgets::icon(ui, "arrow", ACCENT, 18.);
                });
                ui.add_space(22.);
                for (id, label) in [
                    ("connection", "Bağlantı"),
                    ("profiles", "Profiller"),
                    ("services", "Hizmetler"),
                    ("logs", "Loglar"),
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
                    ui.add_space(4.);
                }
                ui.with_layout(egui::Layout::bottom_up(egui::Align::LEFT), |ui| {
                    small(
                        ui,
                        if b(&self.scan, "serviceReady") {
                            "● Hizmet hazır"
                        } else {
                            "● Hizmet hazır değil"
                        },
                    );
                });
            });
        egui::CentralPanel::default()
            .frame(
                egui::Frame::new()
                    .fill(BG)
                    .inner_margin(egui::Margin::symmetric(24, 18)),
            )
            .show(ctx, |ui| {
                if self.page == "services" {
                    egui::TopBottomPanel::bottom("service_actions")
                        .frame(
                            egui::Frame::new()
                                .fill(BG)
                                .inner_margin(egui::Margin::symmetric(0, 8)),
                        )
                        .show_inside(ui, |ui| self.service_actions(ui));
                }
                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        match self.page.as_str() {
                            "profiles" => self.profiles(ui),
                            "settings" => self.settings_page(ui),
                            "services" => self.services(ui),
                            "onboarding" => self.onboarding(ui),
                            "logs" => self.logs_page(ui),
                            _ => self.connection(ui),
                        }
                        ui.add_space(10.);
                        small(ui, &self.notice);
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
    let image = image::load_from_memory(include_bytes!("../../../assets/icon.png"))
        .expect("embedded prism icon")
        .into_rgba8();
    let (width, height) = image.dimensions();
    egui::IconData {
        rgba: image.into_raw(),
        width,
        height,
    }
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
            .with_inner_size([1050., 700.])
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
