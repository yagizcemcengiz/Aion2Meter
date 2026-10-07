# Aion2Meter — Phase 3A

Windows üzerinde AION 2 için ileride geliştirilebilecek bir DPS meter'ın **pasif capture, process endpoint discovery ve offline protocol research altyapısıdır**. Phase 3A, seçilen TCP bağlantısında zaman çizelgesi, sınırlı ham byte incelemesi, sequence karşılaştırması ve stream ordering sağlar. Bu sürümde DPS hesaplama, AION 2 application protocol decoding, combat parser veya overlay yoktur. Otomatik testler için oyunu açmanız gerekmez.

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
  Aion2Meter.Replay/     Npcap gerektirmeyen offline flow analizi, protocol research ve TCP stream araçları
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

## Phase 3A — offline protocol research

Research komutu sadece mevcut **classic PCAP** dosyasını okur; Npcap/oyun/network erişimi gerekmez. PCAPNG desteklenmez. `--local` ve `--remote` sayısal IP:port olmalıdır; IPv6 için `[2001:db8::1]:12345` kullanın. Yalnızca bu exact TCP dört-tuple'ın iki yönü seçilir. Aşağıdaki örnek IP'ler dokümantasyon adresleridir; kendi capture endpoint'lerinizi kullanın.

Workspace kökünde PowerShell örnekleri (satır devamı için backtick):

```powershell
$capture = "captures/session-a.pcap"
$endpoints = @("--local", "192.0.2.1:12345", "--remote", "198.51.100.2:443")

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --timeline --from 11.5 --to 14.5

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --payload --frame-lengths 57,61,92,59 --max-payload-bytes 64

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --timeline --frame-length 57 --direction out

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --payload --payload-length 3 --max-packets 100

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --sequence 57,61,92 --sequence-window-ms 20 --direction out

dotnet run --project src/Aion2Meter.Replay -- research compare `
  "captures/idle.pcap" "captures/session-a.pcap" "captures/session-b.pcap" "captures/session-c.pcap" `
  @endpoints --sequence 57,61,92 --sequence-window-ms 20 --direction out

dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --streams --direction out --stream-offset 0 --stream-bytes 64

# Genel ham hex pattern araması; chunk sınırını geçebilir, gap'i geçemez.
dotnet run --project src/Aion2Meter.Replay -- research $capture @endpoints `
  --streams --stream-pattern AABBCC --from 11.5 --to 14.5

dotnet run --project src/Aion2Meter.Replay -- research --help
```

PowerShell array splatting `@endpoints` ortak endpoint seçeneklerini aktarır. Bash `\` satır devamı kullanmayın. Default komut bağlantı/filter sayaçlarını gösterir; byte dump için açıkça `--payload` veya `--streams` gerekir. `--direction out|in|both` yön seçer; default `both`.

### Timeline, payload ve filtreler

- `--timeline`: orijinal dosyada **1-based packet index**, UTC timestamp, relative seconds, yön, captured frame length, IP/TCP header'lardan hesaplanan declared payload length, available payload bytes, sequence/acknowledgment ve TCP flags. Sıra timestamp, eşitlikte orijinal index'tir. `ACK-only`, control/no-payload, payload ve `TRUNCATED` ayrıdır. ACK-only, payload'sız ve yalnız ACK flag'i olan segmenttir.
- Relative time için aynı basename `.json` içindeki `StartedUtc` kullanılır. Metadata yok/bozuksa tüm PCAP'in en erken timestamp'i kullanılır ve origin çıktıda belirtilir; bu durumda zamanlar gerçek session başlangıcıyla birebir olmayabilir. `--from`/`--to` inclusive relative seconds'tir, decimal ayırıcı `.` olmalıdır. Metadata başlangıcından önce timestamp bulunan packet negatif relative time gösterebilir.
- `--frame-length N` veya `--frame-lengths CSV` **captured frame length** filtreler. `--payload-length N` declared TCP application-byte uzunluğunu filtreler. Header/options/VLAN/padding payload değildir. Truncated capture'da declared ve available ayrı gösterilir; eksik byte'lar üretilmez. Frame-length filtresi tek başına da timeline satırlarını açar.
- `--payload` tek başına yalnız declared payload taşıyan paketleri listeler; `--timeline --payload` birlikte ACK/control satırlarını da tutar, dump'ı yalnız payload için gösterir. Hex ve printable ASCII default **64 byte/packet**, `--max-payload-bytes 1..4096` ile artırılabilir. Default en çok **200 satır/match**, `--max-packets 1..10000` ile değişir; eksiltilen satır sayısı belirtilir.
- Ethernet/VLAN, raw IP, NULL/loopback ve Linux cooked SLL/SLL2 ile IPv4/IPv6 TCP okunur. IPv6 standart extension header'ları geçilir. IP fragment reassembly, IPv6 jumbogram ve başka link type'lar desteklenmez; unsupported/bozuk header sayaçları **tüm dosya** içindir. Connection'a atanamayan paketler selected stream'e katılmaz.

### Sequence ve byte karşılaştırması

`--sequence CSV`, her yönün timestamp sırasındaki **ardışık frame-length** dizisini arar. Ters yöndeki paketler araya girebilir, aynı yöndeki başka bir frame (ACK-only dahil) eşleşmeyi keser. Overlapping matches desteklenir. `--sequence-window-ms` ilk-son packet arasındaki **toplam süreyi** sınırlar; default 20ms, sınır inclusive. Pattern generic'tir; herhangi bir oyun/skill'e atanmaz.

Arama time/direction filtresinden sonra yapılır. Length filtreleriyle `--sequence` birlikte reddedilir; aksi takdirde aradaki paketleri silip yapay adjacency üretmek mümkün olurdu. Her match başlangıç UTC/relative time, original packet indices, direction, captured lengths, payload lengths ve total elapsed time gösterir.

`research compare` 2–16 dosya ve `--sequence` ister. Her capture'daki **tüm match'ler**, aynı yön ve sequence pozisyonu için sample olarak karşılaştırılır; ilk eşleşme seçilip diğerleri atılmaz. Match olmayan dosya açıkça count=0 gösterir. Sample length'leri, common prefix/suffix ve tamamen aynı/değişen offset aralıkları raporlanır. Byte offset'ler **0-based payload-relative**, aralık sonları inclusive'dir. Common suffix sample sonuna göredir; prefix ile çakışmaz. Eşit payload'larda tüm uzunluk prefix'tir, suffix=0. Daha kısa sample'daki eksik byte `--` ve farklı sayılır. Truncated sample bulunan pozisyonda karşılaştırma yapılmaz.

Byte diff default ilk 64 offset ve ilk 16 sample sütununu gösterir; `--max-payload-bytes` offset/range limitini değiştirir. Summary tüm sample/byte'ları içerir; gösterilmeyen satır/sütun/range belirtilir. Bir dosyada birden çok match olabildiği için sample başlıkları `filename#match-number` içerir. Değişen byte'lara field/gameplay anlamı atanmaz.

### Offline TCP stream yaklaşımı ve sınırları

Her yön bağımsızdır. Payload sequence için SYN'in tükettiği sequence slot dikkate alınır ([TCP RFC 9293](https://www.rfc-editor.org/rfc/rfc9293.html)). Sequence numarası sıralaması 32-bit wrap'ı destekler, gözlenen sequence span **2 GiB'den küçük** olmalıdır; belirsiz span reddedilir. ACK-only/control packets byte eklemez. Out-of-order segment'ler sequence konumuna yerleşir. Aynı byte'ın retransmission/duplicate kopyası eklenmez; partial overlap'ın yeni kısmı tutulur. Çelişen overlap'ta **en erken timestamp, eşitlikte en küçük dosya index'i** kazanır; çelişen offset aralıkları raporlanır. Bu deterministik araştırma politikası, uzak TCP alıcısının aynı byte'ları seçtiğini kanıtlamaz.

Stream offset 0 en düşük **gözlenen payload sequence**'dir; midstream capture'da bağlantının gerçek başlangıcı değildir. `SYNobserved` yalnız seçilen zaman penceresindeki SYN'i bildirir. Missing/truncated sequence bölgeleri gap olarak kalır; iki tarafındaki bytes birbirine yapıştırılmaz. `--stream-offset`/`--stream-bytes` belirli offset penceresini (default 0/64, en çok 4096 bytes) gösterir. Her range, kaynak packet index ve yaklaşık capture timestamp taşır; bu application event zamanı değildir. Stream, length presentation filtresinden bağımsız, time/direction penceresinde oluşur. Bu nedenle yalnız frame filtresinin seçtiği paketleri birleştirmez.

`--stream-pattern HEX`, 1–64 byte literal'ini bitişik byte stream'de arar; chunk sınırını geçebilir, gap'te state sıfırlanır. Overlapping occurrences dahildir. Bu işlem message framing veya sayı/field decoding yapmaz. Byte entropy ve printable ASCII oranı yalnız descriptive ölçümlerdir; encrypted/compressed olduklarını veya plaintext field anlamlarını kanıtlamaz.

Birden fazla farklı SYN sequence origin'i aynı dört-tuple içinde görülürse stream birleştirme reddedilir; tek connection epoch için zaman penceresi seçin. Capture SYN'i içermiyorsa tuple reuse kesin ayırt edilemez. Araç full TCP stack değildir: connection lifecycle/ACK/SACK doğrulaması, IP fragment birleştirme, eksik byte kurtarma ve application message framing yapmaz.

Her dosyada en çok 250000 seçilen packet / 64 MiB payload, compare toplamında 500000 packet / 128 MiB payload, 100000 sequence sample, stream yönü başına 100000 ham overlap conflict range ve 1000000 stream-pattern occurrence kabul edilir. Sınır aşımı açık hatadır; analysis sessizce kesilmez. Küçük capture/time window kullanın (dosya payload okuma limiti time window'dan önce uygulanır).

### Privacy ve araştırma sınırları

Research çıktısı **yalnız console'a** yazılır, dosya oluşturmaz. Credentials/auth lexical işaretleri ve JWT benzeri diziler için tüm packet payload'ları ve bitişik stream (display window dışı dahil) kontrol edilir. Saptanan yönde payload preview, stream hex/pattern ve ilgili byte comparison bastırılır; credential/token çıkarılmaz. Stream güvenilir şekilde kontrol edilemiyorsa byte çıktısı konservatif olarak bastırılır. Bu bir heuristic'tir; bilinmeyen binary formatlarda gizli verinin yokluğunu garanti etmez. Phase 3B block komutu da filtrelerden önce tüm seçilen yönü kontrol eder; sensitive yönde extraction/comparison/numeric output üretmez.

`captures/`, PCAP/PCAPNG ve session metadata ignore edilmeye devam eder. CLI çıktısını elle yönlendirirseniz `captures/` veya ignored `.tools/tmp/` altında tutun; **payload dump'larını Git'e eklemeyin**. Repo'ya gerçek capture, capture-derived fixtures veya ham research output eklenmez. Synthetic test fixtures çalışma sırasında geçici ignored build dizininde oluşur ve temizlenir.

Kavramlar ayrı tutulur: **Network Packet → TCP Byte Stream → Application Message → Combat Event → Skill Cast**. Application framing henüz doğrulanmamıştır; bu oklar 1:1 eşleme anlamına gelmez. Bir cast tek packet, bir packet tek combat event veya Earth's Retribution iki hit varsayımı yoktur. Üç controlled session'da kullanıcı skill'i bir kez kullandığını bildirmiştir; diğer gözlenen combat sayıları passive/proc/periodic/equipment vb. etkiler olabilir. Ekran gözlemleri decoded network fact değildir. Yaklaşık capture+10s manuel cast bilgisi frame-accurate timestamp değildir. Phase 3A sayısal ground truth araması yapmaz. Phase 3B'de yalnız açık `--hypothesis-value` ile doğrudan unsigned integer representations aranabilir; transform/endian brute force veya field ataması yapılmaz.

Phase 3A yalnız offline evidence toplar. Network replay/injection/modification, game input automation, memory/DLL/process/API/DirectX hooks, anti-cheat etkileşimi, TLS interception, decryption/key discovery ve production combat parser/DPS engine içermez.

## Phase 3B — candidate block research

`research blocks` 1–16 classic PCAP'te seçilen TCP bağlantısını offline inceler. Default yön `in`; `--direction out|in|both` ile değişir. Production combat parser, DPS engine veya overlay içermez. Binary eşleşmeler gameplay field mapping değildir.

```powershell
$endpoints = @("--local", "192.0.2.1:12345", "--remote", "198.51.100.2:443")

dotnet run --project src/Aion2Meter.Replay -- research blocks "captures/session-a.pcap" @endpoints `
  --from 11 --to 17 --byte-offsets

# Generic body-prefix filtresi prefix'in sonrasından başlar; hiçbir signature skill'e atanmaz.
dotnet run --project src/Aion2Meter.Replay -- research blocks `
  "captures/session-a.pcap" "captures/session-b.pcap" @endpoints `
  --block-length 35 --body-prefix AABB --hypothesis-value 337 --hypothesis-value 6

dotnet run --project src/Aion2Meter.Replay -- research blocks "captures/session-a.pcap" @endpoints `
  --sequence 57,61,92 --action-window-seconds 3 --summary --json

dotnet run --project src/Aion2Meter.Replay -- research blocks --help
```

Framing **UNVALIDATED hypothesis** olarak her çıktıda belirtilir: canonical unsigned base-128 little-seven-bit prefix değeri `V`, prefix byte sayısı `N` ise toplam candidate block uzunluğu `V + N - 4`. Bu formül yalnız araştırma içindir. Stream'in ilk gözlenen byte'ı ve her contiguous run başlangıcı varsayılan candidate boundary'dir; midstream başlangıç doğrulanmaz. Prefix transport chunk sınırını geçebilir, bir block birden çok chunk'tan gelebilir ve tek packet birden çok block taşıyabilir. TCP gap geçilmez. Malformed/noncanonical/overflow prefix, eksik body, aşırı uzunluk veya conflicting overlap bulunduğunda o run durur; byte atlayarak resynchronization aranmaz. Sonraki ayrı run kendi açık sınır varsayımıyla değerlendirilir. Covered/available bytes, contiguous runs, unparsed offset/bytes/reason, gaps/conflicts ve duplicate/overlap sayaçları gösterilir. Tam coverage tek başına framing'in doğru olduğunu kanıtlamaz.

Full connection stream önce reassemble/extract edilir; `--from`/`--to`, `--block-length` ve `--body-prefix` **sonra** adaylara uygulanır. Böylece time filtresi yeni bir application boundary icat etmez; stream offset daima full capture'daki en düşük gözlenen payload sequence'e göredir. Block timestamp ilk byte'ı taşıyan chunk'ın capture zamanıdır; oyun/input/event zamanı değildir. `CompletionUtc` en geç contributing chunk zamanını ayrıca gösterir. Timestamp sırası sequence sırasından farklı olabilir. Birden çok SYN origin'i içeren dosya block komutunda reddedilir; bu komut connection epoch seçmek için time filtresiyle stream'i kesmez.

Her block yön başına 1-based extraction index, UTC/relative seconds, original source/completion packet index, stream offset, length, full hex ve parsed prefix value/byte count taşır. `--byte-offsets` 0-based **block-relative** offset tablosunu açar (en çok 4096 satır/block; omitted count belirtilir). Index filtreleme sonrası yeniden numaralanmaz. `--max-blocks` default 200, üst sınır 10000; family count/signature/comparison ve numeric totals bütün seçilen block'ları içerir. `--summary` block detail satırlarını kaldırır. `--json` aynı evidence'ı machine-readable console JSON olarak verir. Suppressed ve omitted alanları yok/eksik evidence ile gerçek sıfır sonucunu ayırır.

Gruplama key'i yön + toplam uzunluk + prefix'ten sonraki ilk iki raw byte'tır. Bu **heuristic structural grouping**dir; aynı key ilişkili gameplay event demek değildir. Başka sabit body prefix'leri `--body-prefix` ile daraltılabilir. Her family için observed fixed/variable offset aralıkları, common prefix/suffix, değişken byte'lar için `??` signature, tüm offset'lerin same/different tablosu ve ayrı offset 32 satırları üretilir. Karşılaştırma ilk 16 sample sütununu, en çok 4096 offset satırını gösterir; aggregate signature/ranges tüm sample ve byte'ları kapsar. Offset 32 satırları `--max-blocks` ile sınırlanır, eksiltilenler ayrıca sayılır. Singleton'daki sabitlik çapraz sample validation değildir; yeni sample gelince signature değişebilir. Aynı sample kümesinde input sırası signature'ı değiştirmez.

`--hypothesis-value` decimal `uint32` aralığındadır ve 32 kez verilebilir. Değer sığıyorsa uint8, uint16 LE/BE, her zaman uint32 LE/BE representation aranır; block sınırı geçilmez. Her doğrudan eşleşme yalnız **candidate numeric match** olarak value/representation/offset/hex ile gösterilir. Eşleşmeyen değer **not directly represented in selected candidate blocks** olarak raporlanır; yokluk transform/encryption iddiası değildir. Başka block'larda veya rastgele offset'lerde sayısal tesadüf bulunması ground truth validation değildir. Unreadable visual değerler hypothesis girdisi yapılmamalıdır.

Caller-supplied `--sequence` outbound frame dizisi capture genelinde aranır; action-group listesi yalnız seçilen time penceresindeki outbound matches'i kullanır. `--sequence-window-ms` default 20 ms; `--action-window-seconds` default 3 s. Her match sonrası seçilmiş inbound adayların delta'ları ve same-family repetition count'u gösterilir. Default tüm inbound trafik adayları kapsanır; araştırılan body prefix'i seçmek daha dar bir association verir. Cue ve manuel input timestamp'i aynı kabul edilmez; bir block bir damage event, bir cast iki block/hit varsayımı yapılmaz. FRONT/CRITICAL için yalnız differing offsets incelenir, flag anlamı atanmaz.

Phase 3A okuma limitlerine ek olarak extraction yön başına 64 MiB, candidate length 1 MiB, toplam 250000 block ile sınırlıdır. Comparison output 4096 family / 100000 row sınırındadır; aşımda filtre daraltılması istenir, sessiz truncation yapılmaz. JSON offset range alanlarında `End` exclusive'dir; text `ByteRange` gösterimi inclusive son offset kullanır. Framing/gap/conflict veya suppression nedeniyle evidence eksikse numeric yokluk kesin sonuç olarak yazılmaz. Deterministic testler yalnız synthetic bytes/PCAP kullanır: chunk-spanning/coalesced extraction, prefix width, malformed/gap/conflict handling, grouping, signature stability, tüm differing offsets, direct integer representations, CLI filtering/output limits, action repetition ve full-direction privacy kontrolü.

## Controlled test için görsel cue

Phase 3F replay-only protocol komutları, bounded varint/framing/LZ4, raw candidate alanları ve bağımlılık lisansı [replay protocol belgesinde](docs/phase3f-replay-protocol.md) açıklanır. `research decode <capture.pcap> --json` tüm raw/unknown kayıtları ve container provenance'ını verir; `research containers <capture.pcap> --json --summary` container coverage özetini verir. Companion metadata tek game bağlantısını seçer; aksi halde iki endpoint açıkça verilmelidir. Production DPS, isim/skill çözümleme, flag booleans veya live combat accounting eklenmez.

Phase 3H [replay identity correlation belgesinde](docs/phase3h-replay-identities.md) açıklanır: `research identities`, `research id-graph` ve `research skills` capture-scoped isim gözlemlerini, ayrı context label'larını, neutral related-ID adaylarını ve tam raw skill kodlarını gösterir. `--json` raw/provenance ve ayrı preceding/retrospective lookup sonuçlarını korur. İsim bilinmiyorsa unknown, birden fazla isim varsa conflict döner; ownership, class/party, skill display name veya live DPS üretilmez.

Capture zaten Running iken **Start 10s Test Countdown** düğmesine basın. Diagnostic app 10 saniye geri sayıp **CUE NOW** gösterir. Kullanıcı oyundaki aksiyonu elle yapar. Cue görsel olduğu için diagnostic pencere görünür olmalıdır; beep ve game input/hook yoktur.

Countdown monotonic Stopwatch ile 10.000 saniye deadline hedefler; Windows/UI scheduler nedeniyle **tam 10.000s görünürlük garantisi yoktur**. Metadata'daki `TestMarkers` listesine `MarkerType="UserActionCue"`, `MarkerId`, `ScheduledUtc`, gözlenen `TimestampUtc`, session başlangıcına göre `RelativeSeconds` ve monotonic `SchedulingDelayMilliseconds` yazılır. Timestamp, UI cue state'inin hazırlandığı zamandır; ekranın fiziksel refresh anı veya kullanıcının reaction/cast anı değildir. Wall-clock değişikliği UTC/relative time'ı etkileyebilir; scheduler delay monotonic ölçülür.

Stop, pencere kapanışı veya capture fault countdown'u iptal eder. Marker yalnız aynı running SessionId'ye, Stop ile aynı lifecycle lock altında atomik metadata update olarak kaydedilir; son metadata yazımı başarılı marker'ı korur. Save hatası UI/log'da açıkça belirtilir. Session başına 1000 marker sınırı vardır. Eski JSON'larda `TestMarkers` eksikse boş liste olarak okunur; mevcut capture/metadata dosyaları değiştirilmez.

Synthetic testler timeline/ACK/payload/options/padding/IPv6/link headers, time/frame filters, sequence/timing, prefix/suffix/diff, retransmission/out-of-order/overlap/gap/wrap, stream pattern, bounded CLI/privacy ve cue metadata round-trip/backward compatibility'yi doğrular. Countdown'un canlı UI görünürlüğü ve manuel reaction korelasyonu ayrıca kullanıcı tarafından test edilmelidir.
