use eframe::egui;
use serde_json::Value;
use std::{
    io::{BufRead, Read, Write},
    process::{Command, Stdio},
    sync::mpsc::{self, Receiver, Sender},
    thread,
    time::{Duration, Instant},
};

pub struct Reply {
    pub action: String,
    pub result: Result<Value, String>,
}
pub struct Bridge {
    tx: Sender<(String, Vec<String>, Option<Value>)>,
    pub rx: Receiver<Reply>,
}

fn allowed(action: &str, args: &[String]) -> bool {
    match action {
        "check-updates" | "clear-logs" | "logs" | "scan" | "load-settings" | "save-settings"
        | "status" | "connect" | "disconnect" | "resume" | "startup" | "goodcheck"
        | "goodcheck-cancel" | "remove-service" | "warp-license" | "validate-rule" | "detect"
        | "presets" => args.is_empty(),
        "install-service" => {
            args.is_empty()
                || (args.len() == 1 && matches!(args[0].as_str(), "core" | "byedpi" | "zapret"))
        }
        "prepare-warp" => args.is_empty() || args == ["--accept-cloudflare-terms"],
        "import-wireguard" | "import-warp" | "diagnostics-export" | "apply-update" => {
            args.len() == 1
        }
        "install-component" | "remove-component" => {
            args.len() == 1 && matches!(args[0].as_str(), "byedpi" | "zapret")
        }
        _ => false,
    }
}
fn backend_path() -> Result<std::path::PathBuf, String> {
    let dir = std::env::current_exe()
        .map_err(|_| "Uygulama yolu bulunamadı")?
        .parent()
        .ok_or("Uygulama yolu bulunamadı")?
        .to_owned();
    let bundled = dir.join("backend").join("WGTun.Service.exe");
    let adjacent = dir.join("WGTun.Service.exe");
    let protected = std::env::var_os("ProgramFiles").map(|p| {
        std::path::PathBuf::from(p)
            .join("WGTun")
            .join("Broker")
            .join("WGTun.Service.exe")
    });
    [Some(bundled), protected, Some(adjacent)]
        .into_iter()
        .flatten()
        .find(|p| p.is_file())
        .ok_or_else(|| "Hizmet hazır değil".into())
}
impl Bridge {
    pub fn new(ctx: egui::Context) -> Self {
        let (tx, jobs) = mpsc::channel::<(String, Vec<String>, Option<Value>)>();
        let (out, rx) = mpsc::channel();
        thread::spawn(move || {
            for (action, args, input) in jobs {
                let result = (|| -> Result<Value, String> {
                    if !allowed(&action, &args) {
                        return Err("Desteklenmeyen hizmet işlemi".into());
                    }
                    let mut cmd = Command::new(backend_path()?);
                    cmd.arg(&action)
                        .args(&args)
                        .stdin(Stdio::piped())
                        .stdout(Stdio::piped())
                        .stderr(Stdio::piped());
                    #[cfg(windows)]
                    {
                        use std::os::windows::process::CommandExt;
                        cmd.creation_flags(0x08000000);
                    }
                    let mut child = cmd.spawn().map_err(|_| "Hizmet hazır değil")?;
                    let stdout = child.stdout.take().ok_or("Hizmet çıktısı açılamadı")?;
                    let stderr = child.stderr.take().ok_or("Hizmet ilerlemesi açılamadı")?;
                    let reader = thread::spawn(move || {
                        let mut bytes = Vec::new();
                        let _ = stdout.take(1024 * 1024).read_to_end(&mut bytes);
                        bytes
                    });
                    let progress_out = out.clone();
                    let progress_ctx = ctx.clone();
                    let progress = thread::spawn(move || {
                        for line in std::io::BufReader::new(stderr)
                            .lines()
                            .map_while(Result::ok)
                        {
                            if line.len() > 4096 {
                                continue;
                            }
                            if let Ok(value) = serde_json::from_str::<Value>(&line) {
                                if value["progress"].is_string() {
                                    let _ = progress_out.send(Reply {
                                        action: "progress".into(),
                                        result: Ok(value),
                                    });
                                    progress_ctx.request_repaint();
                                }
                            }
                        }
                    });
                    if let Some(data) = input {
                        if let Some(mut stdin) = child.stdin.take() {
                            stdin
                                .write_all(data.to_string().as_bytes())
                                .map_err(|_| "Hizmet girdisi gönderilemedi")?;
                        }
                    } else {
                        drop(child.stdin.take());
                    }
                    let deadline = Instant::now()
                        + Duration::from_secs(if action == "goodcheck" {
                            600
                        } else if action == "install-service" {
                            600
                        } else if action == "prepare-warp" {
                            120
                        } else {
                            60
                        });
                    let status = loop {
                        if let Some(status) =
                            child.try_wait().map_err(|_| "Hizmet durumu alınamadı")?
                        {
                            break status;
                        }
                        if Instant::now() >= deadline {
                            let _ = child.kill();
                            let _ = child.wait();
                            return Err("Hizmet işlemi zaman aşımına uğradı".into());
                        }
                        thread::sleep(Duration::from_millis(50));
                    };
                    let bytes = reader.join().unwrap_or_default();
                    let _ = progress.join();
                    serde_json::from_slice(&bytes).map_err(|_| {
                        if status.success() {
                            "Hizmet yanıtı geçersiz".into()
                        } else {
                            "Hizmet işlemi başarısız".into()
                        }
                    })
                })();
                if out.send(Reply { action, result }).is_err() {
                    break;
                }
                ctx.request_repaint();
            }
        });
        Self { tx, rx }
    }
    pub fn call(&self, action: &str, args: Vec<String>, input: Option<Value>) -> bool {
        self.tx.send((action.into(), args, input)).is_ok()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_expected_actions_and_flags_are_allowed() {
        assert!(allowed("status", &[]));
        assert!(!allowed("shell", &[]));
        assert!(!allowed("status", &["--anything".into()]));
    }
}
