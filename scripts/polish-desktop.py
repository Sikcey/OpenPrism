from pathlib import Path
p=Path('src/WGTun.Desktop/MainWindow.xaml.cs')
s=p.read_text(encoding='utf-8')
translations={
'Checking connection status…':'Durum kontrol ediliyor…','Open WGTun':'WGTun’u aç','Exit':'Çıkış',
'Working…':'İşlem sürüyor…','Connected.':'Motor çalışıyor.','Ready to connect.':'Bağlanmaya hazırsın.',
'DPI bypass':'DPI aşma','Change ?':'Değiştir ↗','DPI bypass · Selected profile':'DPI aşma · Seçili profil','WireGuard · Selected profile':'WireGuard · Seçili profil',
'Refresh status':'Durumu yenile','Traffic routing':'Trafik yönlendirme','All traffic':'Tüm trafik','Selected only':'Yalnızca seçilenler','Except selected':'Seçilenler hariç',
'+ Add application (.exe)':'+ Uygulama ekle','Applications (*.exe)|*.exe':'Uygulamalar (*.exe)|*.exe','Custom WireGuard':'Özel WireGuard',
'Custom WireGuard profile':'Özel WireGuard Profili','Quick setup with your WARP account':'WARP hesabınla hızlı kurulum','Your own server or VPN provider':'Kendi sunucun veya VPN sağlayıcın',
'Connection method without a VPN tunnel':'VPN tüneli kurmadan bağlantı yöntemi','WireGuard configuration':'WireGuard yapılandırması','Choose the .conf file from your provider.':'',
'+ Import WireGuard .conf':'+ WireGuard .conf ekle','WireGuard configuration (*.conf)|*.conf':'WireGuard yapılandırması (*.conf)|*.conf',
'Cloudflare account':'Cloudflare hesabı','Free WARP account':'Ücretsiz WARP hesabı','WARP / WARP+ license key':'WARP / WARP+ lisans anahtarı','Import existing wgcf account':'Mevcut wgcf hesabını içe aktar',
'Apply license':'Anahtarı kullan','Choose account .toml':'Hesap dosyası seç','wgcf account (*.toml)|*.toml':'wgcf hesabı (*.toml)|*.toml',
'GoodbyeDPI is a candidate only. Application-scoped DPI routing is unavailable.':'Uygulama bazlı DPI aşma henüz kullanılamıyor.',
'Configure':'Yapılandır','Configure ':'Yapılandır: ','Custom parameters':'Özel parametreler','Save':'Kaydet','Cancel':'Vazgeç',
'Rules can be saved, but application-scoped routing is currently unavailable.':'Özel kurallar henüz trafiğe uygulanmıyor. Bağlanmak için kural listesini boşaltmalısın.',
'+ Add rule':'+ Kural ekle','Your rules':'Kuralların','No rules yet.':'Henüz kural yok.','Edit ':'Düzenle: ',
'All applications':'Tüm uygulamalar','All destinations':'Tüm hedefler','Direct':'Doğrudan','Active connection':'Aktif bağlantı',
'New rule':'Yeni kural','Edit rule':'Kuralı düzenle','Rule name':'Kural adı','Application (.exe path, optional)':'Uygulama (.exe yolu, isteğe bağlı)',
'Destination IP / CIDR (optional)':'Hedef IP / CIDR (isteğe bağlı)','Protocol':'Protokol','Action':'Yönlendirme','Enter a rule name.':'Bir kural adı gir.',
'Delete':'Sil','Delete this rule?':'Bu kural silinsin mi?','Minimize to tray on close':'Kapatınca sistem tepsisine küçült','Connect on startup':'Açılışta bağlan',
'The installed tunnel is left running when the application closes.':'Bağlantıyı kesmek için Bağlantı ekranını kullan.',
'YOUR CONNECTION SOURCES':'BAĞLANTI KAYNAKLARIN','FINE TUNING':'İNCE AYAR','Custom rules':'Özel kurallar','MAKE IT YOURS':'TERCİHLERİN','YOUR NETWORK, YOUR RULES':'SENİN AĞIN, SENİN KURALLARIN','Your connection, in control.':'Bağlantın kontrolünde.'}
for en,tr in translations.items():s=s.replace('"'+en+'"','"'+tr+'"')
s=s.replace('("Connection", "?"), ("Profiles", "?"), ("Rules", "?"), ("Settings", "?")', '("Connection", "◉"), ("Profiles", "▤"), ("Rules", "⑂"), ("Settings", "⚙")')
s=s.replace('Btn(icon + "   " + name,', 'Btn(icon + "   " + (name switch { "Connection" => "Bağlantı", "Profiles" => "Profiller", "Rules" => "Kurallar", _ => "Ayarlar" }),')
s=s.replace('Heading.Text) = currentPage switch { "Profiles" => ("BAĞLANTI KAYNAKLARIN", "Profiles"),', 'Heading.Text) = currentPage switch { "Profiles" => ("BAĞLANTI KAYNAKLARIN", "Profiller"),')
s=s.replace('"Settings" => ("TERCİHLERİN", "Settings")','"Settings" => ("TERCİHLERİN", "Ayarlar")')
s=s.replace('var power = Btn("?",','var power = Btn("\\uE7E8",').replace('power.FontSize = 38;', 'power.FontFamily = new FontFamily("Segoe MDL2 Assets"); power.FontSize = 30;')
s=s.replace('var edit = Btn("?", (_, _) => EditRule(rule));','var edit = Btn("\\uE70F", (_, _) => EditRule(rule)); edit.FontFamily = new FontFamily("Segoe MDL2 Assets");')
s=s.replace('Page.Children.Add(Text("Application-scoped routing and custom rules are unavailable until a routing backend is installed.", 12, true)); ', '')
s=s.replace('if (settings.Scope != TrafficScope.All)\n', 'if (settings.Scope != TrafficScope.All)\n')
# Keep declared checked consent separate from registration state and do not add a create-account button.
a=s.index('        var consent = new CheckBox');b=s.index('        child.Children.Add(Text("Cloudflare hesabı"',a)
s=s[:a]+'''        if (string.IsNullOrWhiteSpace(accountMessage) || !accountReady)
        {
            var consent = new CheckBox { Content = "Cloudflare WARP kullanım koşullarını kabul ediyorum.", IsChecked = false, IsEnabled = !busy };
            child.Children.Add(consent);
            var terms = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Kullanım koşulları")) { NavigateUri = new Uri("https://www.cloudflare.com/application/terms/") };
            terms.RequestNavigate += (_, e) => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
            var termsText = Text("", 12, true); termsText.Inlines.Add(terms); child.Children.Add(termsText);
            consent.Checked += async (_, _) => {
                if (busy) return;
                busy = true; consent.IsEnabled = false;
                try { var result = await backend.EnsureWarpAccountAsync(true); accountReady = result.Success; accountMessage = result.Message; Notice.Text = result.Message; }
                catch (Exception ex) { accountMessage = "WARP hazırlanamadı: " + ex.Message; }
                finally { busy = false; Render(); }
            };
        }
'''+s[b:]
s=s.replace('private string accountMessage = "";', 'private string accountMessage = "";\n    private bool accountReady;')
a=s.index('    private async Task EnsureAccount()');b=s.index('    private async Task SaveAsync()',a)
s=s[:a]+'''    private async Task EnsureAccount()
    {
        try { var result = await backend.EnsureWarpAccountAsync(); accountReady = result.Success; accountMessage = result.Success ? "" : result.Message; }
        catch (Exception ex) { accountMessage = "WARP hazırlanamadı: " + ex.Message; }
        if (currentPage == "Profiles") Render();
    }
'''+s[b:]
# Empty result text still means ready; don't reopen consent on rerender.
s=s.replace('if (string.IsNullOrWhiteSpace(accountMessage) || !accountReady)', 'if (!accountReady)')
s=s.replace('else DpiEngines(child);','else DpiEngines(child);')
p.write_text(s,encoding='utf-8')
