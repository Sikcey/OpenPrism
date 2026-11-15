use std::sync::{
    atomic::{AtomicU8, Ordering},
    OnceLock,
};

static LANGUAGE: AtomicU8 = AtomicU8::new(0);
pub const LANGUAGES: [(&str, &str); 4] = [
    ("tr", "Türkçe"),
    ("en", "English"),
    ("ru", "Русский"),
    ("zh", "简体中文"),
];

/// Select the active language. Unknown and missing values fall back to Turkish.
pub fn set_language(language: &str) {
    LANGUAGE.store(
        match language {
            "en" => 1,
            "ru" => 2,
            "zh" => 3,
            _ => 0,
        },
        Ordering::Relaxed,
    );
}

pub fn language() -> &'static str {
    match LANGUAGE.load(Ordering::Relaxed) {
        1 => "en",
        2 => "ru",
        3 => "zh",
        _ => "tr",
    }
}

pub fn tr(text: &str) -> &str {
    if language() == "tr" {
        return text;
    }
    translate_english(english(text))
}
pub fn translate_english(text: &str) -> &str {
    let index = match language() {
        "ru" => 1,
        "zh" => 2,
        _ => return text,
    };
    static TABLE: OnceLock<std::collections::HashMap<&'static str, [&'static str; 3]>> =
        OnceLock::new();
    let table = TABLE.get_or_init(|| {
        include_str!("translations.tsv")
            .lines()
            .filter_map(|line| {
                let columns: Vec<_> = line.split('\t').collect();
                if columns.len() == 3 {
                    Some((columns[0], [columns[0], columns[1], columns[2]]))
                } else {
                    None
                }
            })
            .collect()
    });
    table.get(text).map(|row| row[index]).unwrap_or(text)
}
pub fn apply_fonts(ctx: &eframe::egui::Context) {
    use eframe::egui::{FontData, FontDefinitions, FontFamily};
    let mut fonts = FontDefinitions::default();
    let windows = std::env::var("WINDIR").unwrap_or_else(|_| "C:/Windows".into());
    for (name, filename) in [("segoe", "segoeui.ttf"), ("cjk", "msyh.ttc")] {
        if name == "cjk" && language() != "zh" {
            continue;
        }
        if let Ok(data) = std::fs::read(std::path::Path::new(&windows).join("Fonts").join(filename))
        {
            fonts
                .font_data
                .insert(name.into(), FontData::from_owned(data).into());
            let family = fonts.families.get_mut(&FontFamily::Proportional).unwrap();
            if name == "segoe" {
                family.insert(0, name.into());
            } else {
                family.push(name.into());
            }
        }
    }
    ctx.set_fonts(fonts);
}
fn english(text: &str) -> &str {
    match text {
        "İmzalı güncelleme paketi uygula" => "Apply signed update package",
        "Uygulama yakalama" => "Application capture",
        "WGTun hizmeti, sing-box ve uygulama yakalama bileşeni gereklidir; tüm hizmetleri kaldır işlemiyle kaldırılır." => "WGTun service, sing-box and application capture are required; remove all services removes them together.",
        "OpenPrism'e hoş geldin" => "Welcome to OpenPrism",
        "WGTun hizmeti ve uygulamanın kurduğu tüm bileşenler kaldırılsın mı?" => "Remove the WGTun service and all components installed by the application?",
        "Kaldır" => "Remove",
        "Kurulu değil" => "Not installed",
        "Kural eylemi" => "Rule action",
        "Doğrudan / bağlantı dışında" => "Direct / bypass connection",
        "Etkin bağlantıyı kullan" => "Use active connection",
        "Yalnızca TCP test edilir; UDP ve genel erişim doğrulanmaz." => "Only TCP is tested; UDP and overall reachability are not verified.",
        "Başarılı" => "Success",
        "Başarısız" => "Failed",

        "Tüm hizmetleri kaldır" => "Remove all services",
        "Zapret desteği için Hizmetler ekranından hizmeti kur / onar. Kurulu hizmet eski veya Zapret dosyaları eksik." => "Install or repair the service from Services for Zapret support. The installed service is outdated or Zapret files are missing.",
        "Hizmeti güncelle" => "Update service",
        "Deneysel IPv4 modu. Seçilen trafik ayrı relay ve Zapret filtresinden geçer." => "Experimental IPv4 mode. Selected traffic uses a separate relay and Zapret filter.",
        "IPv6 seçili trafikte desteklenmez. İlk kullanımdan önce hizmeti kur veya onar." => "Selected IPv6 traffic is not supported. Install or repair the service before first use.",
        "Strateji testi" => "Strategy testing",
        "Motor çalışıyor; internet erişimi doğrulanmadı." => "Engine running; internet access has not been verified.",
        "Bağlantı kapatıldı." => "Disconnected.",
        "Kurulu hizmet ve motor dosyaları doğrulandı." => "Installed service and engine files verified.",
        "Bazı motor dosyaları doğrulanamadı; onarım gerekli." => "Some engine files could not be verified; repair required.",
        "WGTun hizmeti henüz kurulmadı. İlk kurulumdan devam et." => "WGTun service is not installed. Continue with initial setup.",
        "WGTun’u aç" => "Open WGTun", "Çıkış" => "Exit",
        "GoodCheck için Hizmetler ekranından hizmeti kur veya onar." => "Install or repair the service from Services to use GoodCheck.",
        "Hizmet hazırlanıyor" => "Preparing service", "Motor hazır" => "Engine ready", "Tarayıcılar" => "Browsers",
        "Hizmet işlemi başlatılamadı" => "Could not start the service operation",
        "Sistem tepsisi açılamadı; pencere açık tutuluyor" => "System tray unavailable; keeping the window open",
        "Çevrimdışı görünüm: işlem yapılmadı" => "Offline preview: no operation performed",
        "Cloudflare hesabı hazırlama işlemi başlatılamadı." => "Could not start preparing the Cloudflare account.",
        "Uygulama yolu bulunamadı" => "Application path not found",
        "Desteklenmeyen hizmet işlemi" => "Unsupported service operation",
        "Hizmet durumu alınamadı" => "Could not read the service status",
        "Hizmet girdisi gönderilemedi" => "Could not send service input",
        "Hizmet ilerlemesi açılamadı" => "Could not read service progress",
        "Hizmet işlemi başarısız" => "Service operation failed",
        "Hizmet işlemi zaman aşımına uğradı" => "Service operation timed out",
        "Hizmet yanıtı geçersiz" => "Invalid service response",
        "Hizmet çıktısı açılamadı" => "Could not read service output",
        "ByeDPI yapılandırması" => "ByeDPI configuration",
        "Strateji" => "Strategy", "Standart" => "Standard", "DNS · isteğe bağlı" => "DNS · optional", "Varsayılan DNS" => "Default DNS",
        "Ağında çalışan ByeDPI stratejilerini test et. Sonuçlar otomatik uygulanmaz." => "Test ByeDPI strategies on your network. Results are not applied automatically.",
        "Test edilecek alan adları" => "Hosts to test",
        "Her satıra bir alan adı yaz. En fazla 8 alan adı." => "Enter one hostname per line. Up to 8 hosts.",
        "GoodCheck taraması sürüyor…" => "GoodCheck scan in progress…", "Durdur" => "Stop", "GoodCheck ile tara" => "Scan with GoodCheck",
        "Tarama tamamlandı. Bir sonuç seçip Uygula düğmesine bas." => "Scan complete. Select a result and press Apply.",
        "Test edilen alan adlarında çalışan strateji bulunamadı." => "No working strategy found for the tested hosts.",
        "Tarama durduruldu." => "Scan stopped.", "Tarama zaman aşımına uğradı." => "Scan timed out.",
        "Taramadan önce bağlantıyı kes." => "Disconnect before scanning.", "GoodCheck taraması tamamlanamadı." => "GoodCheck scan failed.",
        "Uygula" => "Apply", "Profil" => "Profile", "Bilinmiyor" => "Unknown", "Kurulum gerekli" => "Setup required",
        "Hazır" => "Ready", "Başlatılıyor" => "Starting", "Durduruldu" => "Stopped", "Onarım gerekli" => "Repair required", "Çalışıyor" => "Running",
        "Hizmeti kur / onar" => "Install / repair service", "Hizmeti kaldır…" => "Remove service…", "Hizmeti kaldır" => "Remove service",
        "WGTun hizmeti ve yönettiği sing-box / ByeDPI dosyaları kaldırılsın mı?" => "Should WGTun and its managed sing-box / ByeDPI files be removed?",
        "Etkin bağlantı kesilecek. Yeniden kullanmak için kurulum gerekir." => "The active connection will be disconnected. Setup will be required to use it again.",
        "Çevrimdışı görünüm" => "Offline preview", "En yeni kayıtlar önce gösterilir." => "Newest entries are shown first.",
        "Temizle" => "Clear", "Yenile" => "Refresh", "Kayıtlarda ara" => "Search logs", "Ayrıntılı kayıt" => "Detailed logging",
        "Değişiklik bir sonraki bağlantıda uygulanır." => "Changes apply on the next connection.",
        "Kurulum tamamlandı. Bağlantı sayfasından devam edebilirsin." => "Setup complete. Continue from the Connection page.",
        "Bağlantıya geç" => "Go to Connection", "Motor kurulumu gerekli" => "Engine installation required", "● Hizmet hazır" => "Service ready", "● Hizmet hazır değil" => "Service not ready",
        "Ücretsiz WARP hesabı" => "Free WARP account", "WARP / WARP+ lisans anahtarı" => "WARP / WARP+ license key",
        "Mevcut wgcf hesabını içe aktar" => "Import existing wgcf account", "Lisans anahtarı" => "License key", "Anahtarı kullan" => "Use key", "Hesap dosyası seç" => "Choose account file",
        "Cloudflare WARP kullanım koşulları" => "Cloudflare WARP terms of service", "Cloudflare hesabı hazırlanıyor…" => "Preparing Cloudflare account…", "Tekrar dene" => "Retry", "Cloudflare profili hazır." => "Cloudflare profile ready.",
        "Uygulama yolu" => "Application path", "IPv4 adresi" => "IPv4 address", "IPv6 adresi" => "IPv6 address", "IPv4 ağı / CIDR" => "IPv4 network / CIDR", "IPv6 ağı / CIDR" => "IPv6 network / CIDR", "Kontrol ediliyor…" => "Validating…", "+  Kural ekle" => "+  Add rule", "Adım" => "Step", "seçili" => "selected",
        "Yönlendirme" => "Routing",
        "Yöntemi incele, kullanmak istediğin profili seç." => "Explore a method and select the profile you want to use.",
        "Diğer · Yakında" => "Other · Coming soon",
        "VPN tüneli kurmadan DPI tabanlı engellere karşı alternatif bir bağlantı yöntemi kullan." => "Use an alternative connection method for DPI based restrictions without a VPN tunnel.",
        "Trafiğini şifreli bir tünel üzerinden yönlendir. Cloudflare WARP kullanabilir veya kendi yapılandırmanı ekleyebilirsin." => "Route traffic through an encrypted tunnel. Use Cloudflare WARP or import your own configuration.",
        "YAPILANDIRMA" => "CONFIGURATION",
        "Standart ByeDPI" => "Standard ByeDPI",
        "Seçili uygulamaların trafiğinde DPI aşma yöntemini kullanır." => "Applies DPI bypass to traffic from selected applications.",
        "Özel WireGuard yapılandırması" => "Custom WireGuard configuration",
        "Kendi sunucuna ait .conf dosyasını içe aktar." => "Import a .conf file for your own server.",
        ".conf dosyası seç" => "Choose .conf file",
        "Cloudflare hesabı ve bağlantı ayarları otomatik hazırlanır." => "Cloudflare account and connection settings are prepared automatically.",
        "Hesap seçenekleri" => "Account options",
        "Uygulamak için Seç düğmesine bas." => "Press Select to apply.",
        "Seç" => "Select",
        "Motor çalışırken kural, metot ve profil değiştirilemez. Önce bağlantıyı kes." => "Rules, methods and profiles cannot be changed while the engine is running. Disconnect first.",
        "Kural ekle" => "Add rule",
        "Yönlendirilecek trafiği belirle." => "Choose which traffic to route.",
        "Kural adı · isteğe bağlı" => "Rule name · optional",
        "Örn. Ev ağı" => "e.g. Home network",
        "IP bazlı" => "IP based",
        "IP adresi" => "IP address",
        "Ağ / CIDR" => "Network / CIDR",
        "Genel" => "General",
        "Başlangıç" => "Startup",
        "WGTun’u kendine göre ayarla." => "Make WGTun your own.",
        "Uygulama dili" => "Application language",
        "Arayüzde kullanılacak dil." => "Language used in the interface.",
        "Sistem tepsisine küçült" => "Minimize to system tray",
        "Pencereyi kapattığında arka planda çalışmaya devam et." => "Keep running in the background when the window closes.",
        "Bilgisayarını açtığında WGTun da açılsın." => "Open WGTun when your computer starts.",
        "Otomatik bağlan" => "Connect automatically",
        "Windows açılışında son kullanılan profille bağlan." => "Connect with the last profile when Windows starts.",
        "WireGuard ve DPI yönlendirme, tek yerde." => "WireGuard and DPI routing in one place.",
        "Kaynak kodu ↗" => "Source code ↗",
        "Sorun bildir ↗" => "Report an issue ↗",
        "WGTun’a güç veren açık kaynak projelerine ve geliştiricilerine teşekkürler." => "Thanks to the open source projects and developers powering WGTun.",
        "Trafik yönlendirme" => "Traffic routing",
        "DPI aşma" => "DPI bypass",
        "WARP hesap ve profil araçları" => "WARP account and profile tools",
        "Kullanıcı arayüzü" => "User interface",
        "Yazılımsal çizim" => "Software rendering",
        "Değişiklikler otomatik kaydedilir" => "Changes are saved automatically",
        "Yalnızca bu IPv4 adresine giden trafik eşleştirilir." => "Matches traffic to this IPv4 address only.",
        "Yalnızca bu IPv6 adresine giden trafik eşleştirilir." => "Matches traffic to this IPv6 address only.",
        "Bu IPv4 ağındaki adreslere giden trafik eşleştirilir." => "Matches traffic to addresses in this IPv4 network.",
        "Bu IPv6 ağındaki adreslere giden trafik eşleştirilir." => "Matches traffic to addresses in this IPv6 network.",
        "Seçilen klasördeki ve alt klasörlerindeki uygulamalar eşleştirilir." => "Matches applications in the selected folder and its subfolders.",
        "İşlem adını düzenle veya uygulama klasörünü seç." => "Edit the process name or choose an application folder.",
        "Uygulamanın tam dosya yolunu düzenle veya klasör seç." => "Edit the executable path or choose a folder.",
        "+  Klasör ekle" => "+  Add folder",
        "Hizmetler" => "Services", "Loglar" => "Logs", "Sistem" => "System",
        "Uygulama" => "Application", "Yeni kural" => "New rule", "Kural adı" => "Rule name",
        "Çalışan ve yüklü uygulamalarda ara" => "Search running and installed applications",
        "Uygulama dosyası seç" => "Browse application", "Klasör seç" => "Browse folder",
        "Ad (isteğe bağlı)" => "Name (optional)", "Kural türü" => "Rule type",
        "Eşleştirilecek değer" => "Match value", "Uygulamalar ve kurallar" => "Applications and rules",
        "+  Uygulama veya kural ekle" => "+  Add application or rule",
        "Beklemede" => "Idle", "Kurulumu başlat" => "Start setup", "Seçilenler için en az bir etkin, doğrulanmış kural ekle." => "Add at least one enabled, validated rule.",
        "Ozel DNS (istege bagli)" => "Custom DNS (optional)", "Vazgec" => "Cancel",
        "Bağlantı" => "Connection", "Profiller" => "Profiles", "Ayarlar" => "Settings",
        "Servisler" => "Services", "Günlükler" => "Logs", "Kayıtlar" => "Logs",
        "Tercihler" => "Preferences", "Dil" => "Language", "Türkçe" => "Turkish", "İngilizce" => "English",
        "Hakkında" => "About", "Kaynak kodu" => "Source code", "Sorun bildir" => "Report an issue",
        "Katkılar" => "Credits", "Güncellemeleri kontrol et" => "Check for updates",
        "Mevcut sürüm" => "Current version", "Son sürüm" => "Latest version", "Sürümü aç" => "Open release",
        "Bağlan" => "Connect", "Bağlantıyı kes" => "Disconnect", "Bağlan / bağlantıyı kes" => "Connect / disconnect",
        "Senin ağın, senin kuralların" => "Your network, your rules", "Bağlantın kontrolünde." => "Your connection is under control.",
        "İşlem sürüyor…" => "Working…", "Motor çalışıyor." => "Engine is running.", "Bağlanmaya hazırsın." => "Ready to connect.",
        "Bağlantı motoru etkin" => "Connection engine active", "Bağlantı kapalı" => "Disconnected",
        "Seçili profil" => "Selected profile", "Değiştir" => "Change",
        "Tüm trafik" => "All traffic", "Yalnızca seçilenler" => "Selected only", "Seçilenler" => "Selected", "Seçilenler hariç" => "Exclude selected",
        "Özel WireGuard Profili" => "Custom WireGuard profile",  "Diğer yöntemler" => "Other methods",
        "Sağlayıcı" => "Provider", "Yapılandır" => "Configure", "Düzenle" => "Edit", "Kuralı düzenle" => "Edit rule",
        "Ekle" => "Add", "Sil" => "Delete", "Kaldır" => "Remove", "Kaydet" => "Save", "Vazgeç" => "Cancel", "İptal" => "Cancel",
        "Geri" => "Back", "Devam" => "Continue", "İleri" => "Next", "Bitir" => "Finish", "Kapat" => "Close",
        "İlk kurulum" => "Initial setup", "WGTun'a hoş geldin" => "Welcome to WGTun", "Bağlantı yöntemini seç" => "Choose a connection method",
        "WireGuard, varsayılan olarak Cloudflare WARP kullanır. ByeDPI alternatif bir yöntemdir." => "WireGuard uses Cloudflare WARP by default. ByeDPI is an alternative method.",
        "Hangi trafik yönlendirilsin?" => "Which traffic should be routed?", "Gerekli bileşenleri kur" => "Install required components",
        "Kurulum gerekli dosyaları otomatik indirir ve Windows bir kez yönetici izni isteyebilir." => "Setup downloads the required files automatically; Windows may request administrator permission once.",
        "Hizmet hazır" => "Service ready", "Hizmet kurulumu gerekli" => "Service installation required",
        "Henüz kural yok. Bir uygulama, klasör veya IP ekleyerek başla." => "No rules yet. Add an application, folder, or IP to get started.",
        "Devam etmek için en az bir kuralı aç." => "Enable at least one rule to continue.",
        "Kurallar" => "Rules",  "Kural seçimi" => "Rule selection", "Ad" => "Name", "Tür" => "Type", "Değer" => "Value",
        "İşlem adı" => "Process name", "Klasör yolu" => "Folder path", "IPv4 aralığı" => "IPv4 range", "IPv6 aralığı" => "IPv6 range",
        "Windows ile başlat" => "Start with Windows", "Tepsiye küçült" => "Minimize to tray",
        _ => text,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fallback_and_translation() {
        set_language("unknown");
        assert_eq!(language(), "tr");
        assert_eq!(tr("Bağlantı"), "Bağlantı");
        set_language("en");
        assert_eq!(tr("Bağlantı"), "Connection");
        assert_eq!(tr("unknown text"), "unknown text");
        set_language("ru");
        assert_eq!(tr("Bağlantı"), "Подключение");
        set_language("zh");
        assert_eq!(tr("Ayarlar"), "设置");
        let mut seen = std::collections::HashSet::new();
        for line in include_str!("translations.tsv").lines() {
            let columns: Vec<_> = line.split('\t').collect();
            assert_eq!(columns.len(), 3, "Invalid translation row: {line}");
            assert!(
                seen.insert(columns[0]),
                "Duplicate translation: {}",
                columns[0]
            );
            for (language, index) in [("ru", 1), ("zh", 2)] {
                set_language(language);
                assert!(!columns[index].trim().is_empty());
                assert_eq!(translate_english(columns[0]), columns[index]);
            }
        }
        set_language("tr");
    }
}
