using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WGTun.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using FontFamily = System.Windows.Media.FontFamily;

namespace WGTun.Desktop;
public partial class MainWindow : Window
{
    private readonly IAppBackend backend = new AppBackend();
    private UserSettings settings = new();
    private EngineStatus status = new(false, "Durum kontrol ediliyor…");
    private readonly System.Threading.SemaphoreSlim saveGate = new(1, 1);
    private System.Windows.Forms.NotifyIcon? tray;
    private bool exiting, busy;
    private string currentPage = "Connection";
    private string accountMessage = "";
    private bool accountReady;
    private bool connectAfterConsent;
    private IReadOnlyList<DpiPreset> presets = [];
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(160, 171, 178));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(32, 175, 146));
    private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(23, 59, 52));

    public MainWindow()
    {
        InitializeComponent();
        AppBackend.ConnectionProgress += message => Dispatcher.Invoke(() => Notice.Text = message);
        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, e) => { if (!exiting && settings.CloseToTray && tray != null) { e.Cancel = true; Hide(); } else tray?.Dispose(); };
    }
    private async Task InitializeAsync()
    {
        try
        {
            settings = await backend.LoadAsync();
            settings.Applications ??= []; settings.Rules ??= []; settings.Dpi ??= new();
            var migrated = settings.RuleSchemaVersion >= 1;
            foreach (var app in backend.DetectApplications())
                if (!settings.Applications.Any(a => string.Equals(a.Path, app.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.Applications.Add(app);
                    if (migrated && !settings.Rules.Any(r => r.Type == RuleType.ProcessPath && string.Equals(r.Value, app.Path, StringComparison.OrdinalIgnoreCase)))
                        settings.Rules.Add(new RoutingRule { Name = app.Name, Type = RuleType.ProcessPath, Value = app.Path, Enabled = app.Selected });
                }
            RulePolicy.Migrate(settings);
            presets = backend.GetDpiPresets();
            await SaveAsync();
            await RefreshStatus();
            SetupTray(); Render();
            accountReady = AppBackend.HasPreparedWarpProfile;
            if (settings.AutoConnect && !status.Connected) { var resumed = await backend.ResumeLastProfileAsync(); Notice.Text = resumed.Message; await RefreshStatus(); Render(); }

        }
        catch (Exception ex) { Notice.Text = "Startup failed: " + ex.Message; Render(); }
    }
    private void SetupTray()
    {
        tray = new System.Windows.Forms.NotifyIcon { Text = "WGTun", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("WGTun’u aç", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Çıkış", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async Task EnsureAccount()
    {
        try { var result = await backend.EnsureWarpAccountAsync(); accountReady = result.Success; accountMessage = result.Success ? "" : result.Message; }
        catch (Exception ex) { accountMessage = "WARP hazırlanamadı: " + ex.Message; }
        if (currentPage == "Profiles") Render();
    }
    private async Task SaveAsync()
    {
        await saveGate.WaitAsync();
        try { await backend.SaveAsync(settings); }
        catch (Exception ex) { Notice.Text = "Could not save settings: " + ex.Message; }
        finally { saveGate.Release(); }
    }
    private async Task RefreshStatus()
    {
        try { status = await backend.GetStatusAsync(); }
        catch (Exception ex) { status = new(false, "Status unavailable: " + ex.Message); }
    }
    private async Task ToggleConnection()
    {
        if (busy) return; busy = true; Render();
        try
        {
            await SaveAsync();
            if (!status.Connected && settings.Profile == ProfileKind.Cloudflare && !AppBackend.HasPreparedWarpProfile) {
                if (!AppBackend.HasWarpAccount) { connectAfterConsent = true; currentPage = "Profiles"; Notice.Text = "Bağlanmak için Cloudflare koşullarını bir kez kabul et."; return; }
                Notice.Text = "Mevcut WARP hesabından profil hazırlanıyor…";
                var prepared = await backend.EnsureWarpAccountAsync();
                if (!prepared.Success) { Notice.Text = prepared.Message; return; }
                accountReady = true;
            }
            var result = status.Connected ? await backend.DisconnectAsync() : await backend.ConnectAsync(settings);
            await RefreshStatus(); Notice.Text = result.Message;
        }
        catch (Exception ex) { await RefreshStatus(); Notice.Text = "Connection operation failed: " + ex.Message; }
        finally { busy = false; Render(); }
    }
    private static TextBlock Text(string value, double size = 13, bool muted = false, bool bold = false) => new() { Text = value, FontSize = size, Foreground = muted ? Muted : Brushes.WhiteSmoke, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Stack() => new();
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    private static Border Card(UIElement child) => new() { Child = child, Background = new SolidColorBrush(Color.FromRgb(27, 31, 35)), BorderBrush = new SolidColorBrush(Color.FromRgb(48, 55, 60)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(0, 5, 0, 8) };
    private static Button Btn(string label, RoutedEventHandler action, bool selected = false) { var b = new Button { Content = label, Background = selected ? Soft : new SolidColorBrush(Color.FromRgb(37,43,47)), Margin = new Thickness(0, 3, 8, 3) }; b.Click += action; return b; }
    private void Go(string page) { currentPage = page; Notice.Text = ""; Render(); }
    private void Render()
    {
        Navigation.Children.Clear();
        foreach (var (name, icon) in new[] { ("Connection", "◉"), ("Profiles", "▤"), ("Settings", "⚙") })
            Navigation.Children.Add(Btn(icon + "   " + (name switch { "Connection" => "Bağlantı", "Profiles" => "Profiller", _ => "Ayarlar" }), (_, _) => Go(name), currentPage == name));
        Page.Children.Clear();
        (Eyebrow.Text, Heading.Text) = currentPage switch { "Profiles" => ("BAĞLANTI KAYNAKLARIN", "Profiller"), "Settings" => ("TERCİHLERİN", "Ayarlar"), _ => ("SENİN AĞIN, SENİN KURALLARIN", "Bağlantın kontrolünde.") };
        switch (currentPage) { case "Profiles": Profiles(); break; case "Settings": Settings(); break; default: Connection(); break; }
    }
    private void Connection()
    {
        var methods = Row();
        methods.Children.Add(Btn("WireGuard", async (_, _) => { if (settings.Profile == ProfileKind.Dpi) settings.Profile = ProfileKind.Cloudflare; await SaveAsync(); Render(); }, settings.Profile != ProfileKind.Dpi));
        methods.Children.Add(Btn("DPI aşma", async (_, _) => { settings.Profile = ProfileKind.Dpi; await SaveAsync(); Render(); }, settings.Profile == ProfileKind.Dpi)); Page.Children.Add(methods);
        var hero = Stack(); hero.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        var power = Btn("\uE7E8", async (_, _) => await ToggleConnection(), true); power.FontFamily = new FontFamily("Segoe MDL2 Assets"); power.FontSize = 30; power.Width = 90; power.Height = 90; power.HorizontalAlignment = System.Windows.HorizontalAlignment.Center; power.IsEnabled = !busy; hero.Children.Add(power);
        var title = Text(busy ? "İşlem sürüyor…" : status.Connected ? "Motor çalışıyor." : "Bağlanmaya hazırsın.", 22, bold: true); title.HorizontalAlignment = System.Windows.HorizontalAlignment.Center; title.Margin = new Thickness(0,12,0,5); hero.Children.Add(title);
        var detail = Text(status.Message, 12, true); detail.HorizontalAlignment = System.Windows.HorizontalAlignment.Center; hero.Children.Add(detail);
        var bar = new DockPanel { Margin = new Thickness(0,22,0,0) }; var change = Btn("Değiştir ↗", (_, _) => Go("Profiles")); DockPanel.SetDock(change, Dock.Right); bar.Children.Add(change); var labels = Stack(); labels.Children.Add(Text(ProfileName(), 14, bold:true)); labels.Children.Add(Text(settings.Profile == ProfileKind.Dpi ? "DPI aşma · Seçili profil" : "WireGuard · Seçili profil", 12, true)); bar.Children.Add(labels); hero.Children.Add(bar); Page.Children.Add(Card(hero));
        var refresh = Btn("Durumu yenile", async (_, _) => { await RefreshStatus(); Render(); }); Page.Children.Add(refresh);
        Page.Children.Add(Text("Trafik yönlendirme", 16, bold:true)); var scopes = Row();
        foreach (var (scope, label) in new[] { (TrafficScope.All, "Tüm trafik"), (TrafficScope.SelectedOnly, "Yalnızca seçilenler"), (TrafficScope.ExceptSelected, "Seçilenler hariç") }) scopes.Children.Add(Btn(label, async (_, _) => { settings.Scope = scope; await SaveAsync(); Render(); }, settings.Scope == scope)); Page.Children.Add(scopes);
        if (status.Connected) Page.Children.Add(Text("Değişiklikler yeniden bağlandığında uygulanır.", 12, true));
        if (settings.Scope != TrafficScope.All)
        {
            if (settings.Rules.Count == 0) Page.Children.Add(Text("Henüz kural yok.", 12, true));
            foreach (var rule in settings.Rules.OrderBy(r => string.IsNullOrWhiteSpace(r.Name) ? r.Value : r.Name))
            {
                var row = new DockPanel();
                var edit = Btn("\uE70F", (_, _) => EditRule(rule)); edit.FontFamily = new FontFamily("Segoe MDL2 Assets"); edit.ToolTip = "Kuralı düzenle"; DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
                var info = Stack();
                var check = new CheckBox { Content = string.IsNullOrWhiteSpace(rule.Name) ? rule.Value : rule.Name, IsChecked = rule.Enabled, ToolTip = rule.Value };
                check.Checked += async (_, _) => { rule.Enabled = true; await SaveAsync(); }; check.Unchecked += async (_, _) => { rule.Enabled = false; await SaveAsync(); };
                info.Children.Add(check); info.Children.Add(Text($"{RulePolicy.TypeName(rule.Type)} · {rule.Value}", 12, true)); row.Children.Add(info); Page.Children.Add(Card(row));
            }
            Page.Children.Add(Btn("+ Kural ekle", (_, _) => EditRule(null)));
        }
    }
    private string ProfileName() => settings.Profile switch { ProfileKind.Cloudflare => "Cloudflare WARP", ProfileKind.CustomWireGuard => string.IsNullOrWhiteSpace(settings.WireGuardProfileName) ? "Özel WireGuard" : settings.WireGuardProfileName, _ => settings.Dpi.Engine };
    private void Profiles()
    {
        foreach (var (kind, label, description) in new[] { (ProfileKind.Cloudflare, "Cloudflare WireGuard", "WARP hesabınla hızlı kurulum"), (ProfileKind.CustomWireGuard, "Özel WireGuard Profili", "Kendi sunucun veya VPN sağlayıcın"), (ProfileKind.Dpi, "DPI aşma", "VPN tüneli kurmadan bağlantı yöntemi") })
        {
            var box = Stack(); var radio = new RadioButton { Content = label, GroupName = "profile", IsChecked = settings.Profile == kind, FontSize = 15, FontWeight = FontWeights.SemiBold };
            radio.Checked += async (_, _) => { settings.Profile = kind; await SaveAsync(); Render(); }; box.Children.Add(radio); box.Children.Add(Text(description, 12, true));
            if (settings.Profile == kind)
            {
                var child = Stack(); child.Margin = new Thickness(24,18,0,0); box.Children.Add(child);
                if (kind == ProfileKind.Cloudflare) Cloudflare(child);
                else if (kind == ProfileKind.CustomWireGuard)
                {
                    child.Children.Add(Text("WireGuard yapılandırması", 13, bold:true)); child.Children.Add(Text("", 12, true));
                    child.Children.Add(Btn("+ WireGuard .conf ekle", async (_, _) => { var d = new OpenFileDialog { Filter = "WireGuard yapılandırması (*.conf)|*.conf" }; if (d.ShowDialog(this) != true) return; try { var result = await backend.ImportWireGuardAsync(d.FileName); Notice.Text = result.Message; if (result.Success) { settings.WireGuardProfileName = Path.GetFileName(d.FileName); await SaveAsync(); Render(); } } catch (Exception ex) { Notice.Text = "Import failed: " + ex.Message; } }));
                    child.Children.Add(Text(settings.WireGuardProfileName, 12, true));
                }
                else DpiEngines(child);
            }
            Page.Children.Add(Card(box));
        }
    }
    private void Cloudflare(StackPanel child)
    {
        if (!accountReady)
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
                if (accountReady && connectAfterConsent) { connectAfterConsent = false; currentPage = "Connection"; await ToggleConnection(); }
            };
        }
        child.Children.Add(Text("Cloudflare hesabı", 13, bold:true)); var choice = new ComboBox { ItemsSource = new[] { "Ücretsiz WARP hesabı", "WARP / WARP+ lisans anahtarı", "Mevcut wgcf hesabını içe aktar" }, SelectedIndex = 0 }; child.Children.Add(choice);
        var fields = Stack(); child.Children.Add(fields); var resultText = Text(accountMessage, 12, true); child.Children.Add(resultText);
        choice.SelectionChanged += (_, _) =>
        {
            fields.Children.Clear();
            if (choice.SelectedIndex == 1)
            {
                var password = new PasswordBox(); fields.Children.Add(password);
                fields.Children.Add(Btn("Anahtarı kullan", async (_, _) => { try { var result = await backend.ApplyWarpLicenseAsync(password.Password); accountMessage = result.Message; resultText.Text = result.Message; if (result.Success) password.Clear(); } catch (Exception ex) { resultText.Text = "License failed: " + ex.Message; } }));
            }
            else if (choice.SelectedIndex == 2) fields.Children.Add(Btn("Hesap dosyası seç", async (_, _) => { var d = new OpenFileDialog { Filter = "wgcf hesabı (*.toml)|*.toml" }; if (d.ShowDialog(this) != true) return; try { var result = await backend.ImportWarpAccountAsync(d.FileName); accountMessage = result.Message; resultText.Text = result.Message; } catch (Exception ex) { resultText.Text = "Import failed: " + ex.Message; } }));
        };
    }
    private void DpiEngines(StackPanel child)
    {
        child.Children.Add(Text("Uygulama bazlı DPI aşma henüz kullanılamıyor.", 12, true));
        var engines = new List<string> { "byedpi" };
        foreach (var engine in engines)
        {
            var row = new DockPanel(); var config = Btn("Yapılandır", (_, _) => ConfigureDpi(engine)); DockPanel.SetDock(config, Dock.Right); row.Children.Add(config);
            var radio = new RadioButton { Content = "ByeDPI", GroupName = "engine", IsChecked = string.Equals(settings.Dpi.Engine, engine, StringComparison.OrdinalIgnoreCase) };
            radio.Checked += async (_, _) => { settings.Dpi.Engine = engine; settings.Dpi.PresetId = presets.FirstOrDefault(p => p.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase))?.Id ?? ""; await SaveAsync(); }; row.Children.Add(radio); child.Children.Add(row);
        }
    }
    private Window Dialog(string title, StackPanel content)
    {
        var window = new Window { Title = title, Owner = this, Width = 500, SizeToContent = SizeToContent.Height, MaxHeight = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(27,31,35)), Foreground = Brushes.WhiteSmoke, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        content.Margin = new Thickness(24); content.Children.Insert(0, Text(title, 20, bold:true)); return window;
    }
    private static void Field(StackPanel panel, string label, UIElement control) { var t = Text(label, 12, true); t.Margin = new Thickness(0,12,0,5); panel.Children.Add(t); panel.Children.Add(control); }
    private void ConfigureDpi(string engine)
    {
        var panel = Stack(); var dialog = Dialog("Yapılandır: " + engine, panel);
        var available = presets.Where(p => p.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)).ToList();
        var options = available.Select(p => p.Name).Concat(new[] { "Özel parametreler" }).ToList(); var strategy = new ComboBox { ItemsSource = options };
        var index = available.FindIndex(p => p.Id == settings.Dpi.PresetId && settings.Dpi.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)); strategy.SelectedIndex = index >= 0 ? index : options.Count - 1;
        var args = new TextBox { Text = settings.Dpi.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase) ? settings.Dpi.CustomArguments : "", AcceptsReturn = true, MinHeight = 65, TextWrapping = TextWrapping.Wrap };
        var dns = new TextBox { Text = settings.Dpi.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase) ? settings.Dpi.Dns : "" };
        Field(panel, "Strateji", strategy);
        var presetNotes = Text("", 12, true);
        panel.Children.Add(presetNotes);
        void UpdateStrategy()
        {
            var isPreset = strategy.SelectedIndex >= 0 && strategy.SelectedIndex < available.Count;
            args.IsEnabled = !isPreset;
            presetNotes.Text = isPreset ? available[strategy.SelectedIndex].Notes : "Özel parametreler motorla uyumlu olmalıdır.";
        }
        strategy.SelectionChanged += (_, _) => UpdateStrategy();
        UpdateStrategy();
        Field(panel, "Gelişmiş parametreler", args); Field(panel, "DNS sunucusu", dns);
        var actions = Row(); actions.Margin = new Thickness(0,18,0,0); actions.Children.Add(Btn("Kaydet", async (_, _) => { settings.Dpi.Engine = engine; settings.Dpi.PresetId = strategy.SelectedIndex >= 0 && strategy.SelectedIndex < available.Count ? available[strategy.SelectedIndex].Id : ""; settings.Dpi.CustomArguments = args.Text; settings.Dpi.Dns = dns.Text.Trim(); await SaveAsync(); dialog.Close(); Render(); })); actions.Children.Add(Btn("Vazgeç", (_, _) => dialog.Close())); panel.Children.Add(actions); dialog.ShowDialog();
    }
    private void EditRule(RoutingRule? existing)
    {
        var panel = Stack(); var dialog = Dialog(existing == null ? "Yeni kural" : "Kuralı düzenle", panel);
        var types = new[] { RuleType.IPv4Address, RuleType.IPv6Address, RuleType.IPv4Range, RuleType.IPv6Range, RuleType.ProcessName, RuleType.ProcessPath, RuleType.FolderPath };
        var name = new TextBox { Text = existing?.Name ?? "" };
        var type = new ComboBox { ItemsSource = types.Select(t => RulePolicy.TypeName(t)).ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(types, existing?.Type ?? RuleType.IPv4Address)) };
        var value = new TextBox { Text = existing?.Value ?? "" };
        Field(panel, "Ad (isteğe bağlı)", name); Field(panel, "Tür", type); Field(panel, "Değer", value);
        var browse = Btn("Gözat", (_, _) => {
            if (types[type.SelectedIndex] == RuleType.ProcessPath) { var file = new OpenFileDialog { Filter = "Uygulamalar (*.exe)|*.exe", CheckFileExists = true }; if (file.ShowDialog(dialog) == true) value.Text = file.FileName; }
            else if (types[type.SelectedIndex] == RuleType.FolderPath) { var folder = new Microsoft.Win32.OpenFolderDialog(); if (folder.ShowDialog(dialog) == true) value.Text = folder.FolderName; }
        });
        panel.Children.Add(browse); var hint = Text("", 12, true); panel.Children.Add(hint);
        void UpdateType() {
            var selected = types[type.SelectedIndex]; browse.Visibility = selected is RuleType.ProcessPath or RuleType.FolderPath ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = selected switch { RuleType.IPv4Address => "Örnek: 192.0.2.1", RuleType.IPv6Address => "Örnek: 2001:db8::1", RuleType.IPv4Range => "Örnek: 192.0.2.0/24", RuleType.IPv6Range => "Örnek: 2001:db8::/32", RuleType.ProcessName => "Örnek: Discord.exe", RuleType.ProcessPath => "Mevcut bir .exe dosyası seç.", _ => "Mevcut bir klasör seç." };
        }
        type.SelectionChanged += (_, _) => UpdateType(); UpdateType();
        var actions = Row(); actions.Margin = new Thickness(0,18,0,0);
        actions.Children.Add(Btn("Kaydet", async (_, _) => {
            var candidate = new RoutingRule { Type = types[type.SelectedIndex], Value = value.Text.Trim() };
            var error = RulePolicy.Validate(candidate); if (error != null) { MessageBox.Show(dialog, error, "WGTun"); return; }
            var rule = existing ?? new RoutingRule(); rule.Type = candidate.Type; rule.Value = candidate.Value; rule.Name = string.IsNullOrWhiteSpace(name.Text) ? candidate.Value : name.Text.Trim();
            if (existing == null) settings.Rules.Add(rule); await SaveAsync(); dialog.Close(); Render();
        }));
        if (existing != null) actions.Children.Add(Btn("Sil", async (_, _) => { if (MessageBox.Show(dialog, "Bu kural silinsin mi?", "WGTun", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; settings.Rules.Remove(existing); await SaveAsync(); dialog.Close(); Render(); }));
        actions.Children.Add(Btn("Vazgeç", (_, _) => dialog.Close())); panel.Children.Add(actions); dialog.ShowDialog();
    }
    private void Settings()
    {
        var box = Stack();
        using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        var startup = new CheckBox { Content = "Windows ile ba?lat", IsChecked = runKey?.GetValue("WGTun") != null, IsEnabled = runKey != null };
        startup.Checked += (_, _) => { try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); key?.SetValue("WGTun", "\"" + Environment.ProcessPath + "\""); } catch (Exception ex) { Notice.Text = ex.Message; } };
        startup.Unchecked += (_, _) => { try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); key?.DeleteValue("WGTun", false); } catch (Exception ex) { Notice.Text = ex.Message; } };
        box.Children.Add(startup);
        var auto = new CheckBox { Content = "A??l??ta son profille ba?lan", IsChecked = settings.AutoConnect };
        auto.Checked += async (_, _) => { settings.AutoConnect = true; await SaveAsync(); await backend.SetStartupAsync(true); };
        auto.Unchecked += async (_, _) => { settings.AutoConnect = false; await SaveAsync(); await backend.SetStartupAsync(false); };
        box.Children.Add(auto);
        var trayCheck = new CheckBox { Content = "Kapatınca sistem tepsisine küçült", IsChecked = settings.CloseToTray, IsEnabled = tray != null }; box.Children.Add(trayCheck); box.Children.Add(Text("Bağlantıyı kesmek için Bağlantı ekranını kullan.", 12, true));
        trayCheck.Checked += async (_, _) => { settings.CloseToTray = true; await SaveAsync(); }; trayCheck.Unchecked += async (_, _) => { settings.CloseToTray = false; await SaveAsync(); };
        box.Children.Add(Text("VPN yalnızca Bağlan düğmesiyle başlatılır. Açılışta hesap oluşturulmaz veya yönetici izni istenmez.", 12, true)); Page.Children.Add(Card(box));
    }
}
