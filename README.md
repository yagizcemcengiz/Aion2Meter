# Aion2Meter — Phase 1

Windows üzerinde AION 2 için ileride geliştirilebilecek bir DPS meter'ın **yalnızca pasif capture ve offline replay altyapısıdır**. Bu sürümde DPS hesaplama, AION 2 protokolü/packet decoding, combat parser veya overlay yoktur. Test için oyunu açmanız gerekmez.

## Gereksinimler

- Windows 11 x64.
- .NET 10 SDK; C# 14 ve WPF. `global.json` .NET 10 SDK ailesini seçer.
- Canlı capture için kullanıcı tarafından ayrıca kurulmuş [Npcap](https://npcap.com/). Installer, driver ve native binary dosyaları repository'ye dahil edilmez.
- SharpPcap **6.3.1**, PacketDotNet **1.4.8**, xUnit. NuGet sürümleri sabitlenir; `packages.lock.json` dosyaları bağımlılık çözümünü kaydeder.
- Npcap kurulumu yalnızca Administrator erişimine izin veriyorsa uygulamayı yönetici olarak çalıştırın.

## Solution

```text
Aion2Meter.sln
src/
  Aion2Meter.Core/       Ortak modeller, interface'ler, session adları, thread-safe sayaçlar
  Aion2Meter.Capture/    SharpPcap adaptör keşfi, pasif capture, pcap writer, metadata
  Aion2Meter.Replay/     Npcap gerektirmeyen offline pcap okuyucu ve console CLI
  Aion2Meter.App/        WPF diagnostic ekranı
tests/
  Aion2Meter.Tests/      Deterministic unit testler ve offline native writer testi
```

## Build ve test

Workspace kökünde PowerShell ile:

```powershell
.\build.ps1
```

Bu komut sırayla `dotnet restore`, `dotnet build`, `dotnet test` çalıştırır ve hata olduğunda durur. Ayrı adımlar:

```powershell
.\build.ps1 restore
.\build.ps1 build
.\build.ps1 test
```

Doğrudan dotnet CLI kullanmak için önce repository içindeki geliştirme ortamını yükleyin:

```powershell
. .\dev-env.ps1
dotnet restore
dotnet build
dotnet test
```

`dev-env.ps1`, SDK/NuGet cache ve geçici dosyalarını workspace içindeki, Git tarafından ignore edilen `.tools/` altında tutar. Bu değişkenler mevcut PowerShell oturumunda kalır. Repository dışına build dosyası yazılması gerekmez. PowerShell script çalıştırma politikanız izin vermiyorsa kendi kuruluş politikanıza uygun bir oturum kullanın.

Normal testler Npcap kullanmaz. `NpcapIntegration` kategorisindeki test yalnızca sentetik bir paketi **dosyaya** yazar, sonra offline okuyarak timestamp ve özgün uzunluğunu karşılaştırır. Hiçbir adaptörü açmaz ve ağa paket göndermez; Npcap yüklenemiyorsa graceful skip olur. Yalnızca unit testler:

```powershell
dotnet test --filter "Category!=NpcapIntegration"
```

## WPF diagnostic uygulaması

Yönetici PowerShell oturumunda workspace kökünden:

```powershell
. .\dev-env.ps1
dotnet run --project src/Aion2Meter.App
```

Build sonrasında `src/Aion2Meter.App/bin/Debug/net10.0-windows/Aion2Meter.App.exe` dosyasını **Run as administrator** ile de açabilirsiniz.

1. Açılışta Npcap durumu ve capture-capable yerel adaptörler yüklenir.
2. Aktif Ethernet/Wi-Fi adaptörünü seçin. Friendly name, description, interface identifier, IPv4/IPv6 adresleri, MAC ve status mümkün olduğunca gösterilir. Npcap loopback/sanal adaptörlerinde bazı alanlar bulunmayabilir.
3. Adaptör durumu değiştiyse **Refresh Adapters** kullanın; bu işlem capture sırasında kapalıdır.
4. **Start Capture** ile capture başlatın. Adaptör seçmeden başlatılamaz.
5. Saniyede yaklaşık üç kez güncellenen packets/sec, total packets/bytes ve TCP/UDP/Other sayaçlarını izleyin.
6. **Stop Capture** ile capture'ı durdurun. Bekleyen paketler işlenir, writer kapatılıp native dosya tamponları flush edilir. Pencereyi kapatmak da bu kapanış akışını bekler.

UI ham payload göstermez. Son 100 diagnostic mesaj saklanır; packet başına log/UI güncellemesi yapılmaz. UI thread'i Npcap keşfi, start/stop ve dosya yazımı için bloke edilmez.

Npcap yüklenemediğinde uygulama açık kalır, durum **Missing** olur ve şu hata gösterilir:

```text
Npcap is not installed or could not be loaded.
```

## Capture seçenekleri ve dosyalar

- Yalnızca seçilen **yerel** adaptörde `ip or ip6` BPF filtresi kullanılır. Oyun portları veya server IP'leri hard-code edilmez; ARP gibi IP dışı trafik kaydedilmez.
- Promiscuous mode **kapalıdır** (`DeviceModes.None`). Bu aşamada bilgisayarın kendi IP trafiğini yakalamak yeterli olduğundan diğer makinelerin Ethernet frame'lerini kabul etmesi istenmez. Monitor mode ve paket gönderimi seçenekleri kullanılmaz.
- 500 ms read timeout ve 262144 byte snapshot sınırı kullanılır. Writer link-layer türünü açılan adaptörden alır; Ethernet dışındaki Npcap adaptörlerinde yanlış bir Ethernet başlığı yazılmaz.
- Callback yalnızca timestamp/uzunluk bilgilerini alır, raw veriyi bir kez kopyalar ve 4096 paketle sınırlı kuyruğa koyar. Dosya yazımı ve PacketDotNet ile standart Ethernet/IP/TCP/UDP başlık metadata'sı çıkarma tek background consumer'da yapılır. Application payload parse edilmez.
- Kuyruk dolarsa callback beklemez; yeni paket düşürülür. **Queue dropped** sayacı ve diagnostic mesajı bunu gösterir. Bu sayaç Npcap/kernel tarafındaki kayıpları ölçmez. Yüksek trafikte capture eksiksiz olacağı garanti edilmez.
- UI sayaçları writer'a verilen paketleri ve **captured byte** toplamını sayar. Metadata okunamayan bozuk/truncated paketler ham haliyle dosyaya yazılır, `Other` ve **Metadata errors** sayaçlarına eklenir. Özgün frame uzunluğu ayrıca metadata'da korunur.
- Session başladığında solution kökünde `captures/` otomatik oluşturulur. Dosya adları **UTC** kullanır: `capture_2026-10-07_001530.pcap`. Aynı saniyede tekrar başlatılırsa `_001`, `_002` eklenir; var olan capture üzerine yazılmaz.
- Solution dışında dağıtılan uygulamada `captures/`, uygulamanın kendi dizini altında oluşturulur. Dizin yazılabilir olmalıdır.

Format **classic pcap 2.4**, microsecond timestamp'tır. [SharpPcap 6.3.1 CaptureFileWriterDevice](https://github.com/dotpcap/sharppcap/blob/v6.3.1/SharpPcap/LibPcap/CaptureFileWriterDevice.cs) libpcap dump writer'ını kullanır ve classic pcap yazar. Dosyalar Wireshark ile açılabilir. `Write(data, ref PcapHeader)` kullanılarak captured/original length ayrı tutulur; `Write(RawCapture)` özgün uzunluğu korumadığı için kullanılmaz. Writer dispose/close normal Stop ve uygulama kapanışında çağrılır. İşletim sisteminin zorla process sonlandırması veya elektrik kesintisi güvenli Stop ile aynı garantiye sahip değildir.

## Offline replay

```powershell
. .\dev-env.ps1
dotnet run --project src/Aion2Meter.Replay -- captures/capture_2026-10-07_001530.pcap
dotnet run --project src/Aion2Meter.Replay -- captures/capture_2026-10-07_001530.pcap --dump 20
```

Boşluk içeren dosya yollarını tırnak içine alın. `--dump` isteğe bağlıdır; 0–10000 arasında bir sayı kabul eder.

CLI total packets, captured total bytes, first/last timestamp (UTC), duration, TCP/UDP/Other ve metadata error sayılarını verir. `--dump 20` ilk 20 paketin yalnızca timestamp, captured/original length, Ethernet type, IP version, source/destination IP/port, transport protocol ve TCP flags bilgisini dosya sırasıyla gösterir.

Replay dosyayı yalnızca okumak için açar ve Npcap/native driver gerektirmez. Little/big endian, microsecond/nanosecond classic pcap desteklenir; pcapng desteklenmez ve açıklayıcı hata verir. Boş capture'da timestamp `n/a`, duration sıfırdır. İlk/son timestamp dosyada görülen en erken/en geç zamandır; zamanlar ters sırada olsa da duration negatif olmaz. Nanosecond girişler .NET timestamp hassasiyeti olan 100 ns'a yuvarlanmadan aşağı kesilir. Geçersiz/truncated dosyalarda summary başarı gibi gösterilmez ve exit code `1` döner; CLI kullanım hataları `2` döner.

**Replay yalnızca kaydedilmiş veriyi offline okuyucuya tekrar okutmak demektir. Hiçbir paket network'e geri gönderilmez.**

## Privacy ve kapsam sınırları

Capture dosyaları IP adresleri, endpoint'ler ve **ham paket payload'ları** içerebilir. Şifrelenmemiş trafikte kişisel bilgiler bulunabilir. Kendi sisteminizde ve izinli trafik üzerinde kullanın; capture dosyalarını paylaşırken içeriklerini değerlendirin. UI/loglarda payload gösterilmez ama dosyadaki payload silinmez veya anonimleştirilmez. `captures/`, `*.pcap`, `*.pcapng`, `bin/`, `obj/` ve IDE geçici dosyaları Git tarafından ignore edilir.

Proje oyun klasörünü/process'ini aramaz, oyun executable veya dosyalarını değiştirmez. Process attach, memory access/scanner, injection, API/DirectX hooking, anti-cheat bypass, packet modification/injection, network'e raw paket gönderimi, input/macro automation içermez. Phase 2 özellikleri uygulanmaz.

## Troubleshooting

| Durum | Kontrol |
| --- | --- |
| Npcap Missing | Npcap'i resmi kaynaktan ayrıca kurun/onarın, ardından uygulamayı yeniden açın. Build ve offline replay için Npcap gerekli değildir. |
| Adapter listesi boş | Ethernet/Wi-Fi bağlantısını ve Npcap kurulumunu kontrol edin, Refresh Adapters kullanın. |
| Capture açılmıyor / access denied | Npcap'in Administrator-only kurulumu için uygulamayı Administrator olarak çalıştırın. Capture dizinine yazma iznini kontrol edin. |
| Packet counter artmıyor | Aktif adaptörü seçtiğinizi doğrulayın. VPN kullanıyorsanız trafik sanal adaptörden geçebilir. Browser'da birkaç site açın. IP filtresi nedeniyle ARP sayılmaz. |
| Adaptör çıkarıldı / capture hata verdi | Diagnostic logu kontrol edin. Capture kapanır; Refresh Adapters ile yeniden keşfedip yeni session başlatın. |
| Queue dropped artıyor | Trafik hızını/disk yükünü kontrol edin. Capture eksik olabilir; bu Phase 1 sürümü full-rate kayıt garantisi sunmaz. |
| Replay dosyayı bulamıyor | Current File alanındaki tam yolu kullanın; göreli yol workspace köküne göre çözümlenir. |
| Replay format/truncation hatası | Stop Capture sonrasında okuyun. Classic `.pcap` kullanın; pcapng gerekiyorsa Wireshark ile classic pcap olarak dışarı aktarın. |
| Metadata errors artıyor | Ham veri korunur; bozuk/truncated veya desteklenmeyen link-layer paketlerde metadata sınırlı olabilir. Wireshark ile dosyayı kontrol edin. |
| Build/test NuGet hatası | .NET 10 SDK ve NuGet internet erişimini kontrol edin; `.\build.ps1 restore` çalıştırın. |

## Manuel test checklist

1. App'i Administrator olarak çalıştır.
2. Network adapter listesinin geldiğini doğrula.
3. Aktif Ethernet/Wi-Fi adapter'ını seç.
4. Start Capture bas.
5. Browser'da birkaç site aç.
6. Packet counter'ın arttığını doğrula.
7. Stop Capture bas.
8. captures klasöründe dosyanın oluştuğunu doğrula.
9. Replay tool ile capture dosyasını aç.
10. Replay statistics çıktısını doğrula.

Bu aşamada AION 2 açılması zorunlu değildir.
