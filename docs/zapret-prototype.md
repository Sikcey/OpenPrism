# Zapret Windows prototipi — 2026-11-15

Prototip uygulamanın aktif mod listesine eklenmedi. Amaç, sing-box sonrasındaki çıkış akışlarını fiziksel kart üzerinde ayırmanın uygulanabilirliğini sınamak.

## Sonuçlar

- Windows geçici port rezervasyonu: IPv4/IPv6 × TCP/UDP geçti. Her havuzda 12 seçili soket açıldı; 32 portun tamamına rezervasyonsuz bind engellendi; protokol/adres ailesi başına 200 normal otomatik bind havuz dışında kaldı.
- Gerçek fiziksel çıkış: Ethernet, ifIdx 8. Seçili ve normal soketler aynı 1.1.1.1 hedefinde TCP/TLS/HTTPS ve UDP/DNS yanıtı aldı.
- Pinned upstream WinDivertHelperCompileFilter / WinDivertHelperEvalFilter ile 48 filtre senaryosu geçti: iki yön, iki protokol, iki adres ailesi; normal akış, farklı arayüz, impostor, loopback ve farklı uzak port dışlandı.
- Salt okunur canlı yakalama: gözlenen paket sayıları seçili TCP / normal TCP / seçili UDP / normal UDP için 17 / 17 / 2 / 2; sahiplik filtresi 17 / 0 / 2 / 0 paket eşleştirdi. NO_INSTALL kullanıldı; sürücü kurulmadı.
- Canlı winws TCP multisplit testi: ilk çalışmada seçili HTTPS geçti, normal HTTPS zaman aşımına uğradı; UDP isteklerinin ikisi de geçti. Sahiplik filtresi normal akışları yine eşleştirmedi. Nedenin winws olduğu veya olmadığı bu tek ölçümle kanıtlanmadı.
- Tekrarda HTTPS ve UDP isteklerinin tamamı geçti. Gözlenen sayılar 19 / 15 / 2 / 2; sahiplik filtresi 19 / 0 / 2 / 0 paket eşleştirdi. Test sonunda yalnızca prototipin winws child süreci kapatıldı. Başka uygulamaya ait mevcut WinDivert hizmetinin yolu ve durumu değişmedi.

Bu sonuçlar akış izolasyonunun temelini destekler. Tam SOCKS5 relay, UDP/QUIC stratejileri, sing-box entegrasyonu, fiziksel IPv6, ağ değiştirme ve uzun süreli kararlılık henüz doğrulanmadı. Erişilebilir bir test hedefindeki HTTPS başarısı, erişimi engellenmiş bütün hedeflerde DPI aşmayı kanıtlamaz.

## DLL hatası

İlk indirilen winws dosyaları arasında `cygwin1.dll` yoktu. PE import kontrolüyle bağımlılık bulundu, aynı sabit upstream commit'inden eklendi. Sonrasında `winws --version` 0 ile çıktı ve canlı winws testi çalıştı. `scripts/check-zapret-payload.py` sürücü yüklemeden doğrudan PE bağımlılıklarını kontrol eder. WinwsProbeSession bütün dört dosyanın hash'ini başlatmadan önce doğrular.

Kaynak: https://github.com/bol-van/zapret-win-bundle/tree/6b4375fa04f6bd7017eec379e16e462d3ea8df66/zapret-winws

| Dosya | SHA-256 |
| --- | --- |
| winws.exe | 2da71e80878dc270ac83f5893ecbb841f9752a57f1da8ff9325636b4346bc632 |
| WinDivert.dll | 16abd6a029e65557c6a309bea7b13bf81fff4e193567582e1cddbf6719f323e0 |
| WinDivert64.sys | 8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2 |
| cygwin1.dll | 103104a52e5293ce418944725df19e2bf81ad9269b9a120d71d39028e821499b |

## Kod ve kanıtlar

- `src/WGTun.Core/Experimental/WindowsPortLease.cs`: süreç içi geçici TCP/UDP havuzu.
- `src/WGTun.Core/Experimental/ZapretFilterBuilder.cs`: aile/protokol/arayüz/havuz bazlı iki yönlü filtre.
- `src/WGTun.Core/Experimental/WinwsProbeSession.cs`: sabit payload hash'leri ve Job Object içinde kısa süreli winws testi.
- `tests/WGTun.ZapretProbe/`: rezervasyon, gerçek çıkış, native filtre değerlendirmesi ve salt okunur paket gözlemi.
- `artifacts/research/zapret/port-isolation.json`
- `artifacts/research/zapret/physical-traffic.json`
- `artifacts/research/zapret/filter-isolation.json`
- `artifacts/research/zapret/capture-elevated.json`
- `artifacts/research/zapret/winws-live.json`: ilk kısmi başarısızlık.
- `artifacts/research/zapret/winws-live-repeat.json`: başarılı tekrar.
