//! Only the existing trusted package installer and backend own system changes.
use serde_json::Value;
use std::{
    io::{Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::mpsc::Sender,
    thread,
    time::{Duration, Instant},
};

#[derive(Clone, Debug)]
pub struct App {
    pub value: Value,
    pub selected: bool,
}
#[derive(Clone)]
pub struct Plan {
    pub package: PathBuf,
    pub location: PathBuf,
    pub byedpi: bool,
    pub zapret: bool,
    pub startup: bool,
    pub shortcut: bool,
    pub consent: bool,
    pub apps: Vec<App>,
}
pub enum Event {
    Progress(f32, &'static str),
    Scanned(Vec<App>, bool),
    Installed,
    Removed,
    Failed(String),
    Folder(PathBuf),
    FolderCancelled,
}

pub fn installed_location() -> Option<PathBuf> {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};
    let key = RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenPrism")
        .ok()?;
    let path: std::ffi::OsString = key.get_value("InstallLocation").ok()?;
    let path = PathBuf::from(path);
    (path.is_absolute() && path.join("package-manifest.json").is_file()).then_some(path)
}

fn uninstall_command(script: &Path, location: &Path) -> Result<Command, String> {
    let system = std::env::var_os("SystemRoot").ok_or("Windows dizini bulunamadı.")?;
    let mut cmd =
        command(&PathBuf::from(system).join("System32/WindowsPowerShell/v1.0/powershell.exe"));
    cmd.args([
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
    ])
    .arg(script)
    .arg("-InstallDirectory")
    .arg(location)
    .current_dir(std::env::temp_dir());
    Ok(cmd)
}

pub fn handoff_uninstall(location: &Path) -> Result<(), String> {
    uninstall_command(&location.join("uninstall-openprism.ps1"), location)?
        .arg("-Interactive")
        .spawn()
        .map(|_| ())
        .map_err(|_| "Kaldırma sihirbazı başlatılamadı.".into())
}

pub fn uninstall(location: PathBuf, tx: Sender<Event>) {
    let _ = tx.send(Event::Progress(
        0.,
        "Windows hizmeti ve OpenPrism dosyaları kaldırılıyor…",
    ));
    let script =
        std::env::temp_dir().join(format!("OpenPrism-Uninstall-{}.ps1", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&script)
            .map_err(|_| "Geçici kaldırma dosyası oluşturulamadı.")?;
        let written = file.write_all(include_bytes!("../../scripts/uninstall-openprism.ps1"));
        drop(file);
        let result = written
            .map_err(|_| "Geçici kaldırma dosyası yazılamadı.".to_owned())
            .and_then(|_| run(uninstall_command(&script, &location)?, None).map(|_| ()));
        let _ = std::fs::remove_file(&script);
        result
    })();
    let _ = tx.send(match result {
        Ok(()) => Event::Removed,
        Err(_) => Event::Failed("OpenPrism kaldırılamadı. Yönetici iznini ve kurulum dosyalarını denetleyip yeniden dene.".into()),
    });
}

pub fn free_bytes(raw: &str) -> Option<u64> {
    use std::{os::windows::ffi::OsStrExt, ptr::null_mut};
    use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
    let local = std::env::var("LOCALAPPDATA").ok()?;
    let path = PathBuf::from(raw.replace("%LOCALAPPDATA%", &local));
    if !path.is_absolute() || raw.starts_with("\\\\") {
        return None;
    }
    let existing = path.ancestors().find(|path| path.is_dir())?;
    let wide = existing
        .as_os_str()
        .encode_wide()
        .chain([0])
        .collect::<Vec<_>>();
    let mut free = 0;
    let success = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut free, null_mut(), null_mut()) };
    (success != 0).then_some(free)
}

pub fn payload_bytes(package: &Path) -> Option<u64> {
    let manifest = package.join("package-manifest.json");
    if manifest.metadata().ok()?.len() > 1024 * 1024 {
        return None;
    }
    let bytes = std::fs::read(manifest).ok()?;
    let manifest: Value =
        serde_json::from_slice(bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes)).ok()?;
    manifest["files"]
        .as_array()?
        .iter()
        .try_fold(0_u64, |total, file| {
            total.checked_add(file["size"].as_u64()?)
        })
}

pub fn location(raw: &str, package: &Path) -> Result<PathBuf, String> {
    let local = std::env::var("LOCALAPPDATA").map_err(|_| "LOCALAPPDATA bulunamadı.")?;
    let raw = raw.replace("%LOCALAPPDATA%", &local);
    if raw.trim() != raw || raw.contains(['\n', '\r', '"', '\0', '%']) {
        return Err("Geçerli, tam bir klasör yolu gir.".into());
    }
    let p = PathBuf::from(&raw);
    if !p.is_absolute()
        || raw.starts_with("\\\\")
        || raw.contains(':') && raw[2..].contains(':')
        || p.parent().is_none()
        || p.file_name().is_none()
    {
        return Err("Yerel diskte, kök olmayan bir klasör seç.".into());
    }
    if p.components().any(|c| {
        matches!(
            c,
            std::path::Component::ParentDir | std::path::Component::CurDir
        )
    }) {
        return Err("Yol . veya .. içeremez.".into());
    }
    for part in raw.split(['\\', '/']).skip(1) {
        if part.ends_with(['.', ' ']) || part.contains(['<', '>', '|', '?', '*']) {
            return Err("Klasör yolu geçersiz karakter içeriyor.".into());
        }
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        if [
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ]
        .contains(&stem.as_str())
        {
            return Err("Ayrılmış Windows klasör adı.".into());
        }
    }
    for ancestor in p.ancestors() {
        if let Ok(meta) = ancestor.symlink_metadata() {
            use std::os::windows::fs::MetadataExt;
            if meta.file_attributes() & 0x400 != 0 {
                return Err("Bağlantılı klasörler desteklenmiyor.".into());
            }
        }
    }
    let a = raw.trim_end_matches('\\').to_ascii_lowercase();
    let b = package
        .to_string_lossy()
        .trim_end_matches('\\')
        .to_ascii_lowercase();
    if a == b || a.starts_with(&(b.clone() + "\\")) || b.starts_with(&(a + "\\")) {
        return Err("Paket ve hedef klasörleri ayrı olmalı.".into());
    }
    if p.exists() && (!p.is_dir() || !p.join("package-manifest.json").is_file()) {
        return Err("Tanınmayan mevcut klasör korunuyor. Yeni bir klasör seç.".into());
    }
    Ok(p)
}

fn command(exe: &Path) -> Command {
    use std::os::windows::process::CommandExt;
    let mut c = Command::new(exe);
    c.creation_flags(0x08000000);
    c
}
// Drain both pipes concurrently, bound memory and time. Never forward raw backend output:
// settings/account material and third-party diagnostic output must not enter UI/logs.
fn run(mut c: Command, input: Option<&Value>) -> Result<Vec<u8>, String> {
    c.stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = c
        .spawn()
        .map_err(|_| "İşlem başlatılamadı. Paket dosyalarını ve izinleri denetle.")?;
    let mut stdin = child.stdin.take().unwrap();
    if let Some(v) = input {
        stdin
            .write_all(v.to_string().as_bytes())
            .map_err(|_| "Ayar isteği yazılamadı.")?;
    }
    drop(stdin);
    fn drain(mut pipe: impl Read) -> Vec<u8> {
        let mut out = Vec::new();
        let mut b = [0; 8192];
        while let Ok(n) = pipe.read(&mut b) {
            if n == 0 {
                break;
            }
            if out.len() + n <= 4 * 1024 * 1024 {
                out.extend_from_slice(&b[..n]);
            }
        }
        out
    }
    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();
    let a = thread::spawn(move || drain(stdout));
    let b = thread::spawn(move || {
        drain(stderr);
    });
    let start = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait().map_err(|_| "İşlem durumu okunamadı.")? {
            break status;
        }
        if start.elapsed() > Duration::from_secs(600) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(
                "İşlem zaman aşımına uğradı. Ağı ve yönetici onayını denetleyip yeniden dene."
                    .into(),
            );
        }
        thread::sleep(Duration::from_millis(80));
    };
    let output = a.join().unwrap_or_default();
    let _ = b.join();
    if !status.success() {
        return Err(
            "İşlem başarısız. Paket dosyalarını ve yönetici onayını denetle; yeniden dene."
                .into(),
        );
    }
    Ok(output)
}
fn api(root: &Path, args: &[&str], input: Option<&Value>) -> Result<Value, String> {
    let mut c = command(&root.join("backend/WGTun.Service.exe"));
    c.args(args);
    let output = run(c, input)?;
    let value: Value = serde_json::from_slice(&output)
        .map_err(|_| "Backend yanıtı geçersiz. Eşleşen güncel paketi kullan.".to_owned())?;
    if value.get("success") == Some(&Value::Bool(false)) {
        return Err(match args[0] {
            "prepare-warp" => "WARP hesabı hazırlanamadı. Cloudflare erişimini, ağını ve koşul onayını denetle. Kaydedilen hesap korunur; yeniden dene.",
            "prepare-components" => "Bileşen indirilemedi veya doğrulanamadı. Ağını ve paket sürümünü denetleyip yeniden dene.",
            "apply-installer-rules" => "Seçilen kurallar kaydedilemedi. Kullanıcı veri klasörü izinlerini denetle.",
            _ => "Backend işlemi tamamlanamadı. Eşleşen paketi ve izinleri denetle; yeniden dene.",
        }.into());
    }
    Ok(value)
}
fn script(plan: &Plan, validate: bool) -> Result<(), String> {
    // Embed the repository installer so a neighbouring script cannot inject commands.
    let temp =
        std::env::temp_dir().join(format!("OpenPrism-Installer-{}.ps1", uuid::Uuid::new_v4()));
    let mut f = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp)
        .map_err(|_| "Geçici kurulum dosyası oluşturulamadı.")?;
    let source = include_str!("../../scripts/install-openprism.ps1");
    f.write_all(source.as_bytes())
        .map_err(|_| "Kurulum dosyası yazılamadı.")?;
    drop(f);
    let system = std::env::var_os("SystemRoot").ok_or("Windows dizini bulunamadı.")?;
    let mut c =
        command(&PathBuf::from(system).join("System32/WindowsPowerShell/v1.0/powershell.exe"));
    c.args([
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
    ])
    .arg(&temp)
    .arg("-PackageDirectory")
    .arg(&plan.package)
    .arg("-InstallDirectory")
    .arg(&plan.location)
    .args([
        "-SelectedComponent",
        match (plan.byedpi, plan.zapret) {
            (true, true) => "byedpi,zapret",
            (true, false) => "byedpi",
            (false, true) => "zapret",
            (false, false) => "core",
        },
    ]);
    if validate {
        c.arg("-ValidateOnly");
    }
    let result = run(c, None).map(|_| ());
    let _ = std::fs::remove_file(temp);
    result
}
pub fn scan(plan: Plan, tx: Sender<Event>) {
    let result = (|| {
        script(&plan, true)?;
        let snapshot = api(&plan.package, &["scan"], None)?;
        let apps = snapshot["applications"]
            .as_array()
            .ok_or("Backend uygulama listesi eksik.")?
            .iter()
            .cloned()
            .map(|value| App {
                value,
                selected: false,
            })
            .collect();
        Ok((apps, snapshot["hasWarpAccount"].as_bool().unwrap_or(false)))
    })();
    let _ = tx.send(match result {
        Ok((a, w)) => Event::Scanned(a, w),
        Err(e) => Event::Failed(e),
    });
}
pub fn install(plan: Plan, tx: Sender<Event>) {
    let progress = |v, s| {
        let _ = tx.send(Event::Progress(v, s));
    };
    let result = (|| {
        location(&plan.location.to_string_lossy(), &plan.package)?;
        progress(0.04, "Paket bütünlüğü doğrulanıyor…");
        script(&plan, true)?;
        let snapshot = api(&plan.package, &["scan", "--status-only"], None)?;
        if !snapshot["hasWarpAccount"].as_bool().unwrap_or(false) && !plan.consent {
            return Err(
                "Ücretsiz WARP hesabı oluşturmak için Cloudflare koşullarını kabul et.".into(),
            );
        }
        progress(0.12, "Temel bileşenler doğrulanıyor ve hazırlanıyor…");
        api(&plan.package, &["prepare-components", "core"], None)?;
        for (enabled, name) in [(plan.byedpi, "byedpi"), (plan.zapret, "zapret")] {
            if enabled {
                progress(0.24, "Seçili ek bileşenler hazırlanıyor…");
                api(&plan.package, &["prepare-components", name], None)?;
            }
        }
        progress(
            0.36,
            if snapshot["hasWarpAccount"].as_bool().unwrap_or(false) {
                "Kaydedilmiş WARP hesabı güvenle hazırlanıyor…"
            } else {
                "Ücretsiz WARP hesabı oluşturuluyor…"
            },
        );
        let args = if plan.consent {
            vec!["prepare-warp", "--accept-cloudflare-terms"]
        } else {
            vec!["prepare-warp"]
        };
        api(&plan.package, &args, None)?;
        progress(
            0.56,
            "Dosyalar aşamalandırılıyor, OpenPrism hizmeti kuruluyor…",
        );
        script(&plan, false).map_err(|error| format!("Hizmet kurulumu: {error}"))?;
        progress(0.85, "Seçilen uygulama kuralları kaydediliyor…");
        let selected = Value::Array(
            plan.apps
                .iter()
                .filter(|app| app.selected)
                .map(|app| app.value.clone())
                .collect(),
        );
        api(&plan.location, &["apply-installer-rules"], Some(&selected))?;
        progress(0.93, "Windows başlangıç seçeneği uygulanıyor…");
        startup(&plan.location, plan.startup)?;
        if plan.shortcut {
            shortcut(&plan.location)?;
        }
        progress(1.0, "Kurulum tamamlandı.");
        Ok(())
    })();
    let _ = tx.send(match result {
        Ok(()) => Event::Installed,
        Err(e) => Event::Failed(e),
    });
}

fn startup(location: &Path, enabled: bool) -> Result<(), String> {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};
    let update = || -> std::io::Result<()> {
        let (key, _) = RegKey::predef(HKEY_CURRENT_USER)
            .create_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run")?;
        if enabled {
            let exe = location.join("WGTun.exe");
            key.set_value("WGTun", &format!("\"{}\" --startup", exe.display()))?;
        } else if let Err(error) = key.delete_value("WGTun") {
            if error.kind() != std::io::ErrorKind::NotFound {
                return Err(error);
            }
        }
        Ok(())
    };
    update().map_err(|_| "Windows başlangıç ayarı kaydedilemedi.".into())
}

fn shortcut(location: &Path) -> Result<(), String> {
    // COM arguments are data via environment variables; never interpolate a path into script source.
    let system = std::env::var_os("SystemRoot").ok_or("Windows dizini bulunamadı.")?;
    let mut c =
        command(&PathBuf::from(system).join("System32/WindowsPowerShell/v1.0/powershell.exe"));
    c.args(["-NoProfile","-NonInteractive","-Command", "$ErrorActionPreference='Stop'; $p=Join-Path ([Environment]::GetFolderPath('Desktop')) 'OpenPrism.lnk'; if(Test-Path -LiteralPath $p){$old=(New-Object -ComObject WScript.Shell).CreateShortcut($p); if($old.TargetPath -ne $env:OPENPRISM_TARGET){throw 'Existing shortcut preserved'}}; $s=(New-Object -ComObject WScript.Shell).CreateShortcut($p); $s.TargetPath=$env:OPENPRISM_TARGET; $s.WorkingDirectory=$env:OPENPRISM_LOCATION; $s.Save()"]);
    c.env("OPENPRISM_TARGET", location.join("WGTun.exe"))
        .env("OPENPRISM_LOCATION", location);
    run(c, None).map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reject_unsafe_locations() {
        let pkg = Path::new("C:\\payload");
        for s in [
            "C:\\",
            "relative",
            "C:\\payload",
            "C:\\payload\\child",
            "C:\\x\\..\\y",
            "C:\\NUL",
            "C:\\foo\";evil",
            "\\\\host\\share\\x",
            "C:\\x:ads",
        ] {
            assert!(location(s, pkg).is_err(), "{s}");
        }
        assert!(location("C:\\OpenPrism-Test-Nonexistent", pkg).is_ok());
    }
}
