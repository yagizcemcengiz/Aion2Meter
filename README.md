# Aion2Meter — Phase 2

Windows üzerinde AION 2 için ileride geliştirilebilecek bir DPS meter'ın **pasif capture, process endpoint discovery ve offline flow analiz altyapısıdır**. Bu sürümde DPS hesaplama, AION 2 application protocol decoding, combat parser veya overlay yoktur. Otomatik testler için oyunu açmanız gerekmez.

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
  Aion2Meter.Core/       Endpoint/filter/session modelleri, metadata JSON, flow ve packet-size sayaçları
  Aion2Meter.Capture/    Windows IP Helper discovery, SharpPcap pasif capture ve pcap writer
  Aion2Meter.Replay/     Npcap gerektirmeyen offline okuyucu, flow analizi ve capture karşılaştırması
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

- **All Traffic** modunda seçilen **yerel** adaptörde Phase 1'in `ip or ip6` BPF filtresi kullanılır. **Selected Process Traffic** modunda aşağıda açıklanan endpoint filtresi kullanılır. Oyun portları veya server IP'leri hard-code edilmez; ARP gibi IP dışı trafik kaydedilmez.
- Promiscuous mode **kapalıdır** (`DeviceModes.None`). Bu aşamada bilgisayarın kendi IP trafiğini yakalamak yeterli olduğundan diğer makinelerin Ethernet frame'lerini kabul etmesi istenmez. Monitor mode ve paket gönderimi seçenekleri kullanılmaz.
- 500 ms read timeout ve 262144 byte snapshot sınırı kullanılır. Writer link-layer türünü açılan adaptörden alır; Ethernet dışındaki Npcap adaptörlerinde yanlış bir Ethernet başlığı yazılmaz.
- Callback yalnızca timestamp/uzunluk bilgilerini alır, raw veriyi bir kez kopyalar ve 4096 paketle sınırlı kuyruğa koyar. Dosya yazımı ve PacketDotNet ile standart Ethernet/IP/TCP/UDP başlık metadata'sı çıkarma tek background consumer'da yapılır. Application payload parse edilmez.
- Kuyruk dolarsa callback beklemez; yeni paket düşürülür. **Queue dropped** sayacı ve diagnostic mesajı bunu gösterir. Bu sayaç Npcap/kernel tarafındaki kayıpları ölçmez. Yüksek trafikte capture eksiksiz olacağı garanti edilmez.
- UI sayaçları writer'a verilen paketleri ve **captured byte** toplamını sayar. Metadata okunamayan bozuk/truncated paketler ham haliyle dosyaya yazılır, `Other` ve **Metadata errors** sayaçlarına eklenir. Özgün frame uzunluğu ayrıca metadata'da korunur.
- Session başladığında solution kökünde `captures/` otomatik oluşturulur. Dosya adları **UTC** ve sanitize edilmiş Session Label kullanır: `2026-10-07_001530_aion-idle.pcap`. Aynı saniyede/label ile tekrar başlatılırsa `_001`, `_002` eklenir; var olan capture veya metadata üzerine yeni session yazılmaz. Eski Phase 1 dosyaları replay ile okunmaya devam eder.
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

Process selection **memory reading değildir**. PID yalnızca Windows network endpoint ownership bilgisini eşlemek için kullanılır; standart process adı okunur. Process adı erişilemezse veya process refresh sırasında kapanırsa PID `Unavailable / exited` adıyla gösterilebilir. Game memory, executable path/modules ve oyun kurulum dizinleri okunmaz. Game adı aranmaz veya hard-code edilmez; seçim kullanıcıya aittir. Proje oyun executable veya dosyalarını değiştirmez. Process attach, memory access/scanner, injection, API/DirectX hooking, anti-cheat interaction/bypass, packet modification/injection, network'e raw paket gönderimi, input/macro automation içermez. Phase 3 uygulanmaz.

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

Bu temel capture testi için AION 2 açılması zorunlu değildir.

## Process / Traffic Discovery

1. **Refresh Processes** ile TCP/UDP endpoint sahibi process'leri listeleyin. Dropdown `ProcessName (PID N)` biçimindedir; hiçbir process otomatik seçilmez.
2. Process'i elle seçin. Tablo Protocol, Local Endpoint, Remote Endpoint, State ve IP Version alanlarını gösterir.
3. **Refresh Connections** seçili PID'yi koruyarak Windows tablolarını yeniden okur. PID artık endpoint sahibi değilse seçim temizlenir. İki refresh düğmesi de tüm owner tablolarından tutarlı bir görünüm oluşturur; farklı tabloların OS snapshot zamanları küçük farklılıklar gösterebilir.
4. Capture Mode olarak **All Traffic** veya **Selected Process Traffic** seçin. Session Label ve isteğe bağlı User Notes girin; bunlar capture başladıktan sonra kilitlenir.
5. Selected Process capture başlatılırken endpoint'ler background thread'de tekrar okunur. Boş/invalid PID, boş endpoint listesi, filtre oluşturma veya Npcap derleme hatasında capture başlamaz; UI/log açıklayıcı hata verir.

Keşif native Windows [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable) ve [GetExtendedUdpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable) OWNER_PID tablolarını kullanır; IPv4/IPv6 TCP bağlantıları, listener'lar ve UDP bindings desteklenir. Netstat/PowerShell çıktısı parse edilmez. Process name access denied/exited hataları listeyi çökertmez; bir table/family erişilemezse diğer tablolar listelenir ve warning gösterilir.

**Npcap PID bazlı filtreleme yapmaz.** Selected Process Traffic, seçili PID'nin başlangıç endpoint'lerinden **IP + port + protocol üzerinden BPF** üretir:

- Bağlı TCP için local/remote IP ve port çifti, iki yönlü olarak eşleştirilir.
- UDP owner tablolarında remote peer bulunmaz; TCP listener'lar gibi local IP/port iki yönde eşleştirilir.
- `0.0.0.0` / `::` wildcard bindings, seçili adaptörün aynı IP ailesindeki mevcut adresleriyle sınırlandırılır. Adaptör adresi bulunamazsa güvenli filtre üretilemez ve capture başlamaz.
- Filtre diagnostic log ve session JSON içinde kaydedilir. Sayısal IP literal'leri kullanılır; process adı veya kullanıcı label'ı BPF içine konmaz. IPv6 scope ID filtre literal'ine eklenmez.
- Endpoint paylaşımı/reuse ve PID reuse nedeniyle **false positive mümkündür**. UDP/listener filtreleri remote peer'i sınırlandıramaz. Bu mekanizma kesin process izolasyonu sağlamaz.
- Filtre sabit bir başlangıç snapshot'ıdır. Sonradan açılan bağlantılar/IP değişiklikleri veya yeni portlar kaçabilir; yeni session için refresh edip capture'ı yeniden başlatın. Port header'ı olmayan IP fragment'leri de endpoint filtresine uymayabilir. IPv6 extension header'larında Npcap BPF/metadata desteği sınırlı olabilir.
- 256 endpoint veya 32768 karakter sınırı aşılırsa filtre daraltılarak/sessizce eksiltilerek kullanılmaz; capture açıklayıcı hatayla reddedilir. Endpoint enumeration packet callback içinde yapılmaz.

## Session label ve JSON metadata

```text
captures/
  2026-10-07_001530_aion-idle.pcap
  2026-10-07_001530_aion-idle.json
```

Label ASCII harf/rakam, `-` ve `_` ile 80 karaktere sanitize edilir; diğer karakterler `-` olur, boş label `session` olur. Timestamp prefix'i Windows reserved filename sorunlarını önler. JSON, capture ile **aynı basename** kullanır.

JSON alanları: `SessionId`, `SessionLabel`, `StartedUtc`, `EndedUtc`, `Duration`, `SelectedAdapter`, `CaptureMode`, `SelectedPid`, `SelectedProcessName`, `ConnectionsAtStart`, `BpfFilter`, `TotalPackets`, `TotalBytes`, `TcpPackets`, `UdpPackets`, `OtherPackets`, `UserNotes`. Ayrıca `QueueDroppedPackets`, `MetadataErrors` ve `Status` kaydedilir. All Traffic metadata'sında seçili PID/process boş ve connections listesi boştur.

Başlangıçta `Running`, Stop/uygulama kapanışında tamamlanmış `Completed` veya capture hatasında `Faulted` metadata yazılır. Stop kuyruğu boşaltıp writer'ı kapattıktan sonra sayaçlar kaydedilir. JSON geçici sibling dosyadan atomik olarak değiştirilir. Process zorla sonlandırılırsa başlangıç metadata'sı `Running`, `EndedUtc=null` ve sayaçlar başlangıç değerleriyle kalabilir; bu tamamlanmış session anlamına gelmez. Metadata yazım hatası diagnostic/UI hatasıdır.

Packet payload JSON'a veya loglara yazılmaz. User Notes yalnızca yerel JSON'a yazılır, diagnostic log'a basılmaz; buraya credentials/cookies gibi gizli bilgiler girmeyin. PCAP ham payload içerir ve **hassas network bilgileri barındırabilir**. JSON da PID/IP/endpoint ve manuel notlar içerir. `captures/`, `*.pcap`, `*.pcapng` ve oluşturulan timestamp/label adındaki JSON'lar Git tarafından ignore edilir. Metadata'yı başka yere taşıyacaksanız basename'i koruyun veya `.capture.json` uzantısını kullanın; capture/metadata'yı repository'ye eklemeyin.

## Offline flows, packet sizes ve comparison

```powershell
dotnet run --project src/Aion2Meter.Replay -- captures/2026-10-07_001530_aion-idle.pcap --flows
dotnet run --project src/Aion2Meter.Replay -- captures/2026-10-07_001530_aion-idle.pcap --top-flows 20
dotnet run --project src/Aion2Meter.Replay -- captures/2026-10-07_001530_aion-idle.pcap --flows --top-flows 20 --sizes
dotnet run --project src/Aion2Meter.Replay -- compare "captures/idle.pcap" "captures/single-hit.pcap"
```

`--flows` yönlü flow'ları Protocol, Source IP/Port, Destination IP/Port, Packets, captured Bytes, First/Last Seen UTC ve Duration ile gösterir. Ters yön ayrı flow'dur; IP protocol number da key'e dahil edilir. IP metadata'sı olmayan/bozuk paketler toplamda sayılır, flow'a atanmaz; `ungrouped packets` sayacı gösterilir. Portları bilinmeyen fragment'ler null portlarla ayrı bir key'e gruplanır; port/peer tahmin edilmez.

`--top-flows N` byte sayısına göre azalan sıralar, eşitlikte packet count/key kullanır; 1–10000 kabul eder ve flows'u otomatik açar. `--sizes` her gösterilen flow için packet count, minimum/maximum/average **captured length** ve en sık beş boyutu frekanslarıyla gösterir; flows'u otomatik açar. Boyutlar header dahil yakalanan frame uzunluğudur, payload uzunluğu değildir. `--dump`, `--flows`, `--top-flows` ve `--sizes` birlikte kullanılabilir. Hiçbir flag verilmezse Phase 1 summary davranışı korunur ve flow state bellekte tutulmaz. Flow analizi flow başına sayaç/boyut histogramı saklar; çok fazla benzersiz endpoint içeren uzun dosyalarda bellek kullanımı artabilir.

`compare A B` yalnız A/B'deki ve ortak yönlü flow'ları, flow başına packet/byte A/B değerlerini ve farklarını gösterir. Tüm farklar **B - A** yönündedir; capture sürelerine normalize edilmez. Ephemeral port değişimi flow'u değiştirebilir. Metadata parse error'ları warning üretir; PCAP parse error'ları işlemi başarısız yapar.

Yeni/kaybolan **remote endpoint** ayrımı için her iki capture yanında aynı basename `.json` dosyası gerekir. Yerel taraf, metadata'daki seçili adaptörün IP'leriyle tanımlanır; IPv6 scope ID normalize edilir. Metadata yoksa/bozuksa iki yöndeki observed endpoint farkları gösterilir ve local/remote yönünün bilinmediği açıkça yazılır. Adaptörün iki tarafını da yerel gösteren loopback gibi flow'lar remote sayılmaz. Sonuçlar standart network header istatistiğidir; application payload yorumlanmaz, skill/damage/crit çıkarılmaz ve network'e replay yapılmaz.

## Phase 2 manuel test checklist

- **TEST A:** App'i Administrator olarak çalıştırın. **Refresh Processes** ile Chrome/Discord/Steam gibi network process'lerinin listelendiğini doğrulayın.
- **TEST B:** AION 2'yi açıp karakterle oyuna girin. **Refresh Processes** yapın; oyuna ait görünen network process'ini elle seçip Connections tablosunun dolduğunu doğrulayın.
- **TEST C:** Aktif adaptörü seçin. **Selected Process Traffic**, Session Label `aion-idle` ile 30 saniye skill kullanmadan capture alın ve durdurun. PCAP/JSON çiftini doğrulayın.
- **TEST D:** Session Label `aion-single-earth-retribution` ile capture başlatın. Başka saldırı kullanmadan **yalnızca bir kez Earth's Retribution** kullanın. Ekrandaki damage değerini, varsa Crit / Perfect bilgisini elle not edin; 5–10 saniye sonra durdurun. Otomatik input veya payload decode yoktur.
- **TEST E:** Her iki PCAP için `--flows --top-flows 20 --sizes` çalıştırın. JSON'daki label/PID/filter/counters/notları kontrol edin.
- **TEST F:** `dotnet run --project src/Aion2Meter.Replay -- compare "<aion-idle.pcap>" "<aion-single-earth-retribution.pcap>"` çalıştırın. İki JSON'u PCAP'lerin yanında tutun; trafik farklarını inceleyin, payload decode etmeyin.

Unit suite native Windows table byte layout'larını, IPv4/IPv6 TCP/UDP mapping, process name access-denied/exited fallback, filter sınırlarını, session sanitization/JSON, flow aggregation/sorting/sizes ve comparison/CLI davranışını sentetik verilerle doğrular. Yeni unit testler Npcap veya gerçek AION 2 gerektirmez. Gerçek adaptör/game session davranışı manuel checklist ile ayrıca doğrulanmalıdır.
