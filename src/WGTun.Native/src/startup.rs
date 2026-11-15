use eframe::egui;
use std::{io, sync::mpsc, thread};
use winreg::{enums::*, RegKey};

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

pub struct Startup {
    pub enabled: Option<bool>,
    pub pending: bool,
    tx: mpsc::Sender<Option<bool>>,
    rx: mpsc::Receiver<Result<bool, String>>,
}

fn read() -> io::Result<bool> {
    let key = match RegKey::predef(HKEY_CURRENT_USER).open_subkey(RUN_KEY) {
        Ok(key) => key,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };
    match key.get_raw_value("WGTun") {
        Ok(_) => Ok(true),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e),
    }
}

fn write(enabled: bool) -> io::Result<bool> {
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(RUN_KEY)?;
    if enabled {
        let exe = std::env::current_exe()?;
        key.set_value("WGTun", &format!("\"{}\" --startup", exe.display()))?;
    } else if let Err(e) = key.delete_value("WGTun") {
        if e.kind() != io::ErrorKind::NotFound {
            return Err(e);
        }
    }
    Ok(enabled)
}

impl Startup {
    pub fn new(ctx: egui::Context, load: bool) -> Self {
        let (tx, jobs) = mpsc::channel();
        let (out, rx) = mpsc::channel();
        thread::spawn(move || {
            for job in jobs {
                let result = match job {
                    Some(enabled) => write(enabled)
                        .map_err(|e| format!("Windows başlangıç ayarı kaydedilemedi: {e}")),
                    None => read().map_err(|e| format!("Windows başlangıç ayarı okunamadı: {e}")),
                };
                if out.send(result).is_err() {
                    break;
                }
                ctx.request_repaint();
            }
        });
        if load {
            let _ = tx.send(None);
        }
        Self {
            enabled: None,
            pending: load,
            tx,
            rx,
        }
    }
    pub fn set(&mut self, enabled: bool) -> Result<(), String> {
        if !self.pending {
            self.tx
                .send(Some(enabled))
                .map_err(|_| "Windows başlangıç ayarı kaydedilemedi".to_owned())?;
            self.pending = true;
        }
        Ok(())
    }
    pub fn poll(&mut self) -> Option<Result<bool, String>> {
        let result = self.rx.try_recv().ok()?;
        self.pending = false;
        if let Ok(enabled) = &result {
            self.enabled = Some(*enabled);
        }
        Some(result)
    }
}
