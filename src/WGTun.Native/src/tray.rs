use eframe::egui;
use std::sync::mpsc::{self, Receiver};

pub enum Action {
    Open,
    Disconnect,
    Exit,
}

#[cfg(windows)]
pub struct Tray {
    _icon: tray_icon::TrayIcon,
    receiver: Receiver<Action>,
    labels: [tray_icon::menu::MenuItem; 3],
    language: &'static str,
}
#[cfg(windows)]
impl Tray {
    pub fn new(ctx: egui::Context) -> Option<Self> {
        use tray_icon::{
            menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem},
            Icon, TrayIconBuilder,
        };
        let menu = Menu::new();
        let open = MenuItem::new(crate::locale::tr("WGTun’u aç"), true, None);
        let disconnect = MenuItem::new(crate::locale::tr("Bağlantıyı kes"), true, None);
        let exit = MenuItem::new(crate::locale::tr("Çıkış"), true, None);
        menu.append(&open).ok()?;
        menu.append(&disconnect).ok()?;
        menu.append(&PredefinedMenuItem::separator()).ok()?;
        menu.append(&exit).ok()?;
        let (open_id, disconnect_id, exit_id) = (
            open.id().clone(),
            disconnect.id().clone(),
            exit.id().clone(),
        );
        let (sender, receiver) = mpsc::channel();
        let menu_sender = sender.clone();
        let menu_ctx = ctx.clone();
        MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
            let action = if event.id == open_id {
                Some(Action::Open)
            } else if event.id == disconnect_id {
                Some(Action::Disconnect)
            } else if event.id == exit_id {
                Some(Action::Exit)
            } else {
                None
            };
            if let Some(action) = action {
                let _ = menu_sender.send(action);
                menu_ctx.request_repaint();
            }
        }));
        tray_icon::TrayIconEvent::set_event_handler(Some(move |event| {
            if matches!(event, tray_icon::TrayIconEvent::DoubleClick { .. }) {
                let _ = sender.send(Action::Open);
                ctx.request_repaint();
            }
        }));
        let embedded = crate::app_icon();
        let icon = Icon::from_rgba(embedded.rgba, embedded.width, embedded.height).ok()?;
        let icon = TrayIconBuilder::new()
            .with_menu(Box::new(menu))
            .with_tooltip("OpenPrism")
            .with_icon(icon)
            .build()
            .ok()?;
        Some(Self {
            _icon: icon,
            receiver,
            labels: [open, disconnect, exit],
            language: crate::locale::language(),
        })
    }
    pub fn refresh_language(&mut self) {
        if self.language != crate::locale::language() {
            for (item, label) in self
                .labels
                .iter()
                .zip(["WGTun’u aç", "Bağlantıyı kes", "Çıkış"])
            {
                item.set_text(crate::locale::tr(label));
            }
            self.language = crate::locale::language();
        }
    }
    pub fn action(&self) -> Option<Action> {
        self.receiver.try_recv().ok()
    }
}

#[cfg(not(windows))]
pub struct Tray;
#[cfg(not(windows))]
impl Tray {
    pub fn new(_: egui::Context) -> Option<Self> {
        None
    }
    pub fn action(&self) -> Option<Action> {
        None
    }
}
