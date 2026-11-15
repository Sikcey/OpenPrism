# Zapret entegrasyonu: prompt incelemesi ve düzeltilmiş tasarım

Durum: mimari inceleme. Bu belge winws modunun uygulanmış veya üretimde doğrulanmış olduğu anlamına gelmez.

İlk Windows prototipinin sonuçları ve DLL düzeltmesi: [zapret-prototype.md](zapret-prototype.md).

## Önerilen dosya / modül yapısı

| Modül | Sorumluluk |
| --- | --- |
| `Models.cs` | Mevcut enum değerlerini değiştirmeden Zapret modu ve sabit strateji kimlikleri |
| `WindowsEgressResolver.cs` | IP Helper üzerinden gerçek çıkış rotası, interface LUID/ifIdx, adaptör adı ve adres ailesi |
| `NetworkRebindCoordinator.cs` | NetworkChange olaylarını birleştirme, bağlantı nesli takibi, iptal ve seri yeniden bağlama |
| `ZapretEgressRelay.cs` | Sing-box tarafından seçilmiş TCP/UDP akışları için ayrı yerel çıkış yolu |
| `WindowsPortLease.cs` | Relay akışlarını diğer uygulamaların akışlarından ayıran Windows soket/port rezervasyonu; prototipte doğrulanmalı |
| `ZapretFilterBuilder.cs` | Fiziksel arayüz + relay sahipliği + protokol/port + impostor/loopback dışlama |
| `ZapretStrategies.cs` | Sabit, doğrulanmış winws argümanları; kullanıcıdan komut satırı kabul etmez |
| `WinDivertOwnership.cs` | Mevcut sürücü/hizmet sahipliği ve uyumluluk denetimi; başkasının sürücüsünü silmez |
| `ZapretSession.cs` | Relay, winws ve sing-box süreçlerinin Job Object içinde başlatılması ve kapatılması |
| `DependencyDownloads.cs` / `SingBoxDependencies.cs` | Sabit upstream sürümü, arşiv hash'i ve sürücü güven doğrulaması |
| `SingBoxConfiguration.cs` / `RuleCompiler.cs` | Mevcut kural semantiğini koruyan active/direct çıkışları |
| `BrokerControl.cs` / `BrokerService.cs` | Kalıcı yetkili hizmet, tek aktif mod, yeniden başlatma ve IPC |
| `profile_page.rs` / dil katalogları | Zapret önizlemesi, açık Seç işlemi, motor çalışırken kilit |

## Prompttaki temel eksik

Fiziksel arayüz filtresi uygulama ayrımı değildir. Aynı Ethernet veya Wi-Fi kartından çıkan seçilmiş ve seçilmemiş trafik aynı `ifIdx` değerini kullanabilir. Sing-box üzerinde iki ayrı `direct` tag oluşturmak da fiziksel paketlere bu tag'i taşımaz.

Bu nedenle şu yapı strict split tunneling sağlamaz:

```
seçili uygulama     -> sing-box -> direct A -> aynı fiziksel NIC -> winws
seçilmemiş uygulama -> sing-box -> direct B -> aynı fiziksel NIC -> winws
```

WinDivert NETWORK katmanında `ifIdx` bulunur; aynı katmanda `processId` filtre alanı yoktur. `processId` FLOW/SOCKET katmanlarında bulunur ve orada alınan süreç kimliği bir NETWORK filtresine doğrudan eklenemez. Üstelik TUN'dan sonra yeniden açılan soketlerin sahibi orijinal uygulama yerine sing-box olur.

Doğru akış için işaretlenebilir, ayrı bir çıkış yolu gerekir:

```
uygulamalar -> sing-box TUN / kural seçimi
               | active -> yerel SOCKS5 relay -> sahipliği ayrılmış soketler -> fiziksel NIC -> winws
               | direct -----------------------------------------------> fiziksel NIC
```

Yerel relay ByeDPI benzeri bir active outbound sunar; winws kendi başına SOCKS sunucusu olarak varsayılmaz. Relay'in soket sahipliğini fiziksel paketlerde güvenilir biçimde ayırmadan Zapret split modu etkinleştirilmez.

Windows port rezervasyonları olası bir izolasyon mekanizmasıdır. TCP/UDP, IPv4/IPv6, kaynak portların zaman içinde yeniden kullanılması ve kapatılan akışların gecikmiş paketleri birlikte doğrulanmalıdır. Sadece sabit bir port aralığı seçmek veya çakışan portta tekrar denemek yeterli değildir. Gerekiyorsa özel WFP sınıflandırma bileşeni değerlendirilir; bu ek sürücü ve imzalama maliyeti açıkça belirtilir.

## Arayüz ve filtre

İncelenen upstream winws kaynağında `--wf-iface=<ifIdx>[.<subIfIdx>]` bulunuyor. Ayrıca `--wf-raw` tam WinDivert filtresi kabul ediyor. Seçilecek release bu özellikler açısından sabitlenip doğrulanmalı; master kaynağını görmek release uyumluluğunu kanıtlamaz.

`--wf-l3=ipv4 --wf-tcp=80,443 --wf-udp=443` yalnızca protokol ve port kapsamını tanımlar. IPv6 ve bu portlar dışındaki uygulama trafiği ayrıca ele alınmalıdır. IPv4 desteklenip IPv6'nın da korunduğu iddia edilmez.

Tam raw filtre kullanılacaksa fiziksel arayüz, relay akış sahipliği, iki yönlü kaynak/hedef port eşleşmesi ve `!impostor` / `!loopback` koşulları aynı filtrede üretilir. Arayüz filtresi tek başına bütün paket döngüsü olasılıklarının çözüldüğü anlamına gelmez.

## Sing-box konfigürasyonu

`direct` outbound gerçek adaptör adına `bind_interface` ile bağlanır. Windows `ifIdx` sayısı doğrudan `bind_interface` değerine yazılmaz. Arayüz bilgisi Windows API'larından aynı rota anlık görüntüsü içinde alınır.

Strict split modunda `active`, relay'in loopback SOCKS5 adresidir. Kullanıcı kuralları mevcut `active` / `direct` mantığını korur:

| Kapsam | Eşleşen trafik | Diğer trafik |
| --- | --- | --- |
| Tüm trafik | active | active |
| Yalnızca seçilenler | active | direct |
| Seçilenler hariç | direct | active |

Doğrudan çıkması istenen uygulamaların da TUN içinde işlenip direct üzerinden çıkması mevcut Windows mimarisiyle tutarlıdır. Bunların mutlaka TUN'u fiziksel olarak atlaması isteniyorsa ayrıca işletim sistemi seviyesinde yönlendirme gerekir. Sing-box `bypass` eylemi Windows için genel bir TUN atlatma çözümü olarak varsayılmaz. Linux `routing_mark` Windows'a taşınmaz.

DoH/bootstrap fiziksel çıkışa bağlanır; çözümleyiciye erişmek için çözümleyicinin kendisine ihtiyaç duyulmaz. Windows ortak DNS istemcisinden gelen sorgularda uygulama kimliği her zaman korunmadığından uygulama bazlı DNS izolasyonu vaadi verilmez.

## Hizmet / sürücü yaşam döngüsü

Mevcut WGTunBroker kullanılır. Kurulum/güncelleme yönetici onayı gerektirebilir; normal bağlanma işlemleri yetkili hizmet üzerinden yürür. Winws, kullanıcı tarafından yazılabilir dizinden SYSTEM yetkisiyle başlatılmaz.

Genel `sc stop/delete WinDivert` uygulanmaz. Başka VPN, firewall veya DPI aracının kullandığı sürücü bozulabilir. Uyumlu mevcut sürücü kullanılabiliyorsa sahipliği devralmadan kullanılır. Uyumsuz veya sahipliği belirsiz sürücü tespit edilirse işlem açık hata ile durur. WGTun kendi süreçlerini ve WinDivert handle'larını kapatır; paylaşılan sürücünün kaydı yalnızca WGTun'un oluşturduğu kanıtlandığında ve güvenli kaldırma koşulları sağlandığında kaldırılır.

Süreçler suspended başlatılır, Job Object'e atanır, ardından devam ettirilir. Ön kontrol veya başlangıç hatası bütün yeni oturumu kapatır; kısmi başarı kaydedilmez. Bağlan, bağlantıyı kes, mod değişimi, GoodCheck taraması ve ağ yeniden bağlamaları aynı oturum koordinasyonuna tabidir.

## Ağ değişimi

NetworkChange bir ipucudur; olayın gelmesi internet erişiminin hazır olduğu anlamına gelmez. Fiziksel adaptör durumu ve gerçek rotalar yeniden sorgulanır. Birden fazla Ethernet/Wi-Fi adaptörü ve IPv4/IPv6 için farklı çıkışlar ele alınır. Sanal adaptörleri yalnızca adlarına bakarak elemek yeterli değildir.

Olaylar birleştirilir; iptal edilebilir tek bir yeniden bağlama çalışır. Yeni rota kullanılabilir olmadan çalışan oturum yıkılmaz. Yeni bağlantılar güncel arayüze bağlanır. Eski IP adresine bağlı mevcut TCP oturumlarının Wi-Fi/hotspot geçişinde sıfır kesintiyle taşınması garanti edilmez. Hedef: kullanıcı müdahalesiz toparlanma ve kısa kontrollü kesinti.

## Kabul kontrolleri

- Seçilen uygulama üzerinde winws etkisi gözlenirken eşzamanlı seçilmeyen uygulamanın fiziksel akışları değiştirilmez.
- Aynı hedefe aynı anda giden seçilmiş/seçilmemiş uygulamalar, TCP ve UDP, IPv4 ve IPv6 ayrı ayrı sınanır.
- Kaynak port yeniden kullanımı, relay kapanması ve gecikmiş cevaplar izolasyonu delmez.
- Fiziksel NIC ve TUN üzerinde paket yakalanarak tekrar yakalama döngüsü dışlanır.
- Önceden çalışan başka WinDivert uygulaması durdurulmaz veya silinmez.
- Ağ değişimi, uyku/uyanma, hızlı olay dizisi, driver yükleme hatası, child crash ve işlem iptali denenir.
- İkinci normal bağlantı UAC açmaz; kayıtlı mod yalnızca başarılı açık seçimle değişir.
- Başarısız başlangıç sonrası süreçler, handle'lar, geçici ayarlar ve relay soketleri temizlenir.

## Düzeltilmiş uygulama talimatı

Mevcut sing-box tabanlı WGTun'a, uygulama/IP bazlı ayrımı fiziksel çıkışta da koruyan bir Zapret modu ekle. Önce seçili ve seçili olmayan akışları winws NETWORK filtresinin ayırabildiğini kanıtlayan küçük bir Windows prototipi oluştur. Fiziksel ifIdx ve iki direct tag'in tek başına izolasyon sağlamadığını varsay. Relay / Windows soket rezervasyonu veya WFP tasarımının güvenilirliğini ölçmeden modu arayüzde kullanılabilir yapma.

Sonra yukarıdaki modülleri mevcut kalıcı yetkili hizmete entegre et. Başkalarına ait WinDivert hizmetlerini silme. Varsayılan fiziksel rotayı otomatik çöz, doğrulanmış sürüm ve filtreleri kullan, seçili modun yanı sıra DNS ve IPv6 davranışını açıkça tanımla. Ağ geçişinde sıfır paket kaybı yerine otomatik toparlanma hedefle. Test edilmemiş kernel/driver ve canlı ağ davranışlarına production-ready etiketi koyma.

## Kaynaklar

- Zapret winws parametreleri ve filtre üretimi: https://github.com/bol-van/zapret/blob/master/nfq/nfqws.c
- Windows bundle: https://github.com/bol-van/zapret-win-bundle
- WinDivert filtre alanlarının katman kapsamı: https://reqrypt.org/windivert-doc.html#filter_language
- Kullanılan sing-box sürümünün dial alanları: https://github.com/SagerNet/sing-box/blob/v1.14.4/docs/configuration/shared/dial.md
- Aynı sürümün route eylemleri: https://github.com/SagerNet/sing-box/blob/v1.14.4/docs/configuration/route/rule_action.md
