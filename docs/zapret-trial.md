# WGTun Zapret deneme modu

Paket: `artifacts/WGTun-zapret-trial/WGTun.exe`.

1. Yeni paketi aç; Hizmetler ekranından **Kur / onar** işlemini tamamla. Kurulum sabit hash'lerle `winws.exe`, `WinDivert.dll`, `WinDivert64.sys` ve `cygwin1.dll` indirir. Mevcut profil ve kullanıcı kuralları korunur.
2. Profiller → **Zapret** → **Seç**. Sekmeye tıklamak modu kaydetmez.
3. Bağlantı ekranında **Yalnızca seçilenler** kapsamını ve yönlendirilecek uygulama/klasör/IP kurallarını seç; ardından bağlan.

## Nasıl çalışır?

Sing-box `active` çıkışını yerel SOCKS5 relay'e yollar. Relay'in dış TCP/UDP soketleri Windows süreç içi port rezervasyonlarından oluşturulur ve fiziksel IPv4 arayüzüne bağlanır. Winws filtresi fiziksel ifIdx + protokol + kaynak/hedef port havuzu ile yalnızca bu akışları eşleştirir. Normal `direct` çıkış fiziksel arayüze bağlanır ve relay havuzuna girmez.

Relay loopback üzerinde dinler, SOCKS CONNECT ve UDP ASSOCIATE destekler; UDP fragmentlerini kabul etmez. Bir UDP association yalnızca kendi gönderdiği hedeflerden yanıt alır. Havuz kapasitesi veya hedef bağlantısı başarısız olursa normal sokete/direct çıkışa geri düşmez.

TCP 80/443 için multisplit, UDP 443 için fake profili kullanılır. Diğer seçilmiş IPv4 hedefleri relay'den geçer ancak bu desync profilleriyle değiştirilmez. Global DNS istekleri sing-box DoH üzerinden doğrudan çıkar.

## Deneme sürümünün sınırları

- Relay bu sürümde IPv4 hedefleri destekler. Seçili IPv6 hedefleri relay tarafından reddedilir; sessiz direct fallback yoktur. Bu bir IPv6 DPI çözümü değildir.
- Fiziksel IPv4 çıkışı bulunamaz veya birden fazla olası arayüz arasında karar verilemezse bağlantı hata ile durur.
- Ağ adresi değişiminde mevcut fiziksel çıkış yeniden değerlendirilir; değiştiyse hizmetin recovery döngüsü oturumu yeniden kurar. Geçiş kesintisiz değildir ve henüz canlı Wi-Fi/hotspot geçiş testi yapılmamıştır.
- WinDivert başka bir uygulamayla uyumsuzsa başlangıç hatası döner. Başka uygulamaların WinDivert hizmetleri durdurulmaz/silinmez. WGTun kendi winws süreçlerini ve relay soketlerini kapatır; paylaşılan sürücü kaydı yerinde kalabilir.
- TCP, UDP relay ve config doğrulama testleri geçmiştir. UDP/QUIC DPI aşma başarısı ve uzun süreli kararlılık henüz bütün hedeflerde doğrulanmamıştır.

## Test kayıtları

- `artifacts/research/zapret/relay.json`: gerçek SOCKS5 üzerinden HTTPS ve UDP/DNS testi.
- `artifacts/config-checks/zapret.json`: sing-box tarafından kontrol edilen seçili uygulama konfigürasyonu.
- `tests/WGTun.Checks`: Zapret enum/JSON roundtrip, `active` / `direct` ayrımı, bind_interface, DoH ve hariç tutma kuralları.
- Önceki kaynak-port/WinDivert izolasyon kanıtı: `docs/zapret-prototype.md`.

Bu paketin hazırlanması sırasında yükseltilmiş relay+winws tekrar testinin Windows yönetici onayı iptal edildi. SOCKS5 üzerinden gerçek HTTPS ve UDP/DNS testi geçti; mevcut winws ile fiziksel izolasyon önceki prototipte doğrulandı. Yeni entegre modun hizmet üzerinden uçtan uca TUN testi henüz tamamlanmadı.
