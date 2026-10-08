# Aion2Meter

A lightweight real-time DPS meter for AION 2 on Windows.

`Windows x64` · `Beta` · `Local` · `No Injection`

Live DPS · Total Damage · Party Contribution · Automatic Class Detection

[English](#english) · [Türkçe](#turkce)

<!-- Add an approved, intentionally tracked public overlay screenshot here. -->

---

<a id="english"></a>

## English

[**Download Aion2 DPS Meter**](https://github.com/yagizcemcengiz/Aion2Meter/releases)

*The Windows ZIP has not been published yet; this link currently opens the Releases page.*

> **Beta:** protocol coverage is still being expanded. Some combat events may not yet be counted.

### Quick Start

> **IMPORTANT — Start before entering the server/world.**
> Open Aion2Meter and start the meter before entering your server so it can identify your character correctly.
> Already in-world? Open the meter → return to AION 2's **Server Selection** → enter your server again.
> You normally do **not** need to restart the entire game.

#### 1. Install Npcap

Aion2Meter uses Npcap to read AION 2 network traffic. Download the installer from the [official Npcap website](https://npcap.com/#download) and install it using the default options.

> **Npcap is required. Aion2Meter cannot read game traffic without it.**

#### 2. Get the Windows ZIP

**A beta Windows ZIP has not been published yet.** Check [GitHub Releases](https://github.com/yagizcemcengiz/Aion2Meter/releases) for availability; the repository is currently private.

Once published, download the latest Windows ZIP and extract it to a folder. The planned Windows x64 package will include its .NET runtime; you will not need to install .NET, the .NET SDK or Visual Studio separately. Npcap is still required.

#### 3. Start Aion2Meter

1. Run `Aion2Meter.App.exe` from the extracted folder.
2. If `Settings` opens, choose the Wi-Fi or Ethernet adapter used for your internet connection and click `Start meter` **before entering the server**. Your selection is remembered for next time.
3. Start AION 2, or return to **Server Selection** if it is already running.
4. Enter your server.
5. Attack a target. Supported damage should appear automatically.

**Started the meter while already in-world?** Return to **Server Selection** and enter your server again. A full game restart is normally unnecessary.

### What you'll see

| Label | Meaning |
| --- | --- |
| `TOTAL` | That character's total damage during the CURRENT encounter |
| `DPS` | Damage per second |
| `%` | Contribution to the party's counted damage |
| `YOU` | Your character |
| Class icon | Automatically detected for supported character profiles |

A distant party member may temporarily show **0 TOTAL · — DPS · 0%**. This is normal while their current character information is unavailable. The same row updates when sufficient current character and combat information arrives.

### Features

- Real-time CURRENT DPS, total damage and party contribution for solo and party play.
- Distant party member awareness, with supported join, leave, rejoin and disband handling.
- Active party membership retained across supported dungeon/scene transitions while character information refreshes.
- Automatic class detection and icons for validated profiles.
- Compact always-on-top overlay with resizing, opacity, scale and position/size lock.
- CURRENT reset, system tray controls, customizable global hotkeys and an approximate network RTT indicator (`≈ ms`, not exact in-game ping).

### Hotkeys

| Default shortcut | Action |
| --- | --- |
| `Ctrl + Shift + H` | Show / hide overlay |
| `Ctrl + Shift + R` | Reset CURRENT; the meter keeps running |

Change shortcuts in `Settings`. Hiding the overlay keeps the meter running. The system tray also offers show/hide, reset, `Settings` and exit.

### Troubleshooting

- **No data appears:** check that Npcap is installed and the correct Wi-Fi/Ethernet adapter is selected. To try another active adapter, use `Settings` → `Stop` → select the adapter → `Start meter`. If you started after entering the world, return to **Server Selection** and enter again.
- **Party member shows 0 / — DPS:** this can be normal while the member is far away or current character information is pending. The same row updates when that information and supported combat arrive.
- **Data unavailable:** use `Settings` → `Export diagnostics`, save the JSON and attach it when reporting the issue. Re-entering the server may restore operation temporarily. A full packet capture is not normally needed for a beta report.
- **Overlay is hidden:** press `Ctrl + Shift + H` or click the Aion2Meter system tray icon.
- **Reset CURRENT:** press `Ctrl + Shift + R` or use `Settings` → `Reset CURRENT`.

Diagnostic exports are bounded local JSON files for troubleshooting. They contain no full raw packet capture or full combat history and are not uploaded automatically. They may include character names and network endpoints; review them before sharing. The save dialog lets you choose the location.

### Known Limitations

- Beta with partial protocol coverage: some combat events may not yet be counted.
- Boss Meter, overall/dungeon-session totals and healing/HPS are not available yet.
- Some transitions may still require re-entering the server to recover data.

<small>Technical note: combat category `0x36` remains unsupported.</small>

### How it works

Aion2Meter passively reads AION 2 network traffic through Npcap and calculates combat statistics locally. It does not read game process memory, inject code, modify game files, send/inject game packets or automate gameplay.

---

<a id="turkce"></a>

## Türkçe

AION 2 için Windows üzerinde çalışan hafif, gerçek zamanlı DPS ölçer.

[**Download Aion2 DPS Meter**](https://github.com/yagizcemcengiz/Aion2Meter/releases)

*Windows ZIP henüz yayımlanmadı; bu bağlantı şimdilik Releases sayfasını açar.*

> **Beta:** protokol desteği geliştirilmeye devam ediyor. Bazı savaş olayları henüz sayılmayabilir.

### Hızlı Başlangıç

> **ÖNEMLİ — Server'a/oyun dünyasına girmeden ÖNCE başlat.**
> Karakterini doğru tanıyabilmesi için Aion2Meter'ı açıp meter'ı server'a girmeden başlat.
> Zaten oyundaysan: meter'ı aç → AION 2'de **Server Selection** ekranına dön → server'ına tekrar gir.
> Normalde oyunu tamamen kapatıp açman **gerekmez**.

#### 1. Npcap'i kur

Aion2Meter, AION 2'nin ağ trafiğini okuyabilmek için Npcap kullanır. Kurulum dosyasını [resmî Npcap sitesinden](https://npcap.com/#download) indir ve kur. Kurulumda varsayılan seçenekleri kullanabilirsin.

> **Npcap gereklidir. Npcap olmadan Aion2Meter oyun trafiğini okuyamaz.**

#### 2. Windows ZIP dosyasını indir

**Beta Windows ZIP dosyası henüz yayımlanmadı.** İndirme durumu için [GitHub Releases](https://github.com/yagizcemcengiz/Aion2Meter/releases) bölümünü kontrol et; repository şu anda private.

Yayımlandığında en güncel Windows ZIP dosyasını indir ve normal bir klasöre çıkar. Planlanan Windows x64 paketi .NET çalışma zamanını içerecek; ayrıca .NET, .NET SDK veya Visual Studio kurman gerekmeyecek. Npcap yine gereklidir.

#### 3. Aion2Meter'ı çalıştır

1. Çıkardığın klasördeki `Aion2Meter.App.exe` dosyasını aç.
2. `Settings` açılırsa internete bağlı olduğun Wi-Fi veya Ethernet adaptörünü seç ve **server'a girmeden önce** `Start meter` düğmesine bas. Seçimin sonraki açılış için hatırlanır.
3. AION 2'yi aç veya zaten çalışıyorsa **Server Selection** ekranına dön.
4. Server'ına gir.
5. Bir hedefe saldır. Desteklenen hasarın otomatik olarak görünmeye başlamalı.

**Aion2Meter'ı karakterin zaten oyundayken mi açtın?** **Server Selection** ekranına dönüp server'ına tekrar gir. Normalde oyunu tamamen yeniden başlatman gerekmez.

### Ekranda ne göreceksin?

| Etiket | Anlamı |
| --- | --- |
| `TOTAL` | İlgili karakterin mevcut savaşta (CURRENT) verdiği toplam hasar |
| `DPS` | Saniye başına hasar |
| `%` | Partinin sayılan toplam hasarındaki katkı |
| `YOU` | Senin karakterin |
| Sınıf ikonu | Desteklenen karakter profillerinde otomatik algılanır |

Uzaktaki bir parti üyesi geçici olarak **0 TOTAL · — DPS · 0%** görünebilir. Güncel karakter bilgisi henüz alınmadığında bu normaldir. Yeterli güncel karakter ve savaş bilgisi geldiğinde aynı satır otomatik güncellenir.

### Özellikler

- Solo ve parti için gerçek zamanlı CURRENT DPS, toplam hasar ve parti katkısı.
- Uzaktaki parti üyelerini takip; desteklenen katılma, ayrılma, yeniden katılma ve parti dağılma işlemleri.
- Desteklenen dungeon/sahne geçişlerinde karakter bilgisi yenilenirken aktif parti üyeliğinin korunması.
- Doğrulanmış profiller için otomatik sınıf algılama ve sınıf ikonları.
- Her zaman üstte duran kompakt overlay; boyut, saydamlık, ölçek ve konum/boyut kilidi.
- CURRENT sıfırlama, sistem tepsisi, değiştirilebilir global kısayollar ve yaklaşık ağ RTT göstergesi (`≈ ms`; oyunun kesin ping değeri değildir).

### Kısayollar

| Varsayılan kısayol | İşlem |
| --- | --- |
| `Ctrl + Shift + H` | Overlay'i göster / gizle |
| `Ctrl + Shift + R` | CURRENT'ı sıfırla; meter çalışmaya devam eder |

Kısayolları `Settings` üzerinden değiştirebilirsin. Overlay'i gizlemek meter'ı durdurmaz. Sistem tepsisinde göster/gizle, sıfırlama, `Settings` ve çıkış seçenekleri de bulunur.

### Sorun Giderme

- **Veri gelmiyor:** Npcap'in kurulu ve doğru Wi-Fi/Ethernet adaptörünün seçili olduğundan emin ol. Diğer aktif adaptörü denemek için `Settings` → `Stop` → adaptörü seç → `Start meter` yolunu kullan. Meter'ı karakter zaten oyundayken açtıysan **Server Selection** ekranına dönüp tekrar gir.
- **Parti üyesinde 0 / — DPS görünüyor:** üye uzaktaysa veya güncel karakter bilgisi bekleniyorsa bu normal olabilir. Bilgi ve desteklenen savaş olayları geldiğinde aynı satır güncellenir.
- **Data unavailable görünüyor:** `Settings` → `Export diagnostics` ile JSON'u kaydet ve hata bildirirken dosyayı ekle. Server'a tekrar girmek geçici olarak düzeltebilir. Beta hata bildirimi için normalde tam paket kaydı gerekmez.
- **Overlay kayboldu:** `Ctrl + Shift + H` tuşlarına bas veya Aion2Meter'ın sistem tepsisi ikonuna tıkla.
- **CURRENT sıfırlama:** `Ctrl + Shift + R` kullan veya `Settings` → `Reset CURRENT` düğmesine bas.

Diagnostic çıktıları hata ayıklama için oluşturulan, boyutu sınırlı yerel JSON dosyalarıdır. Tam ham paket kaydı veya tüm savaş geçmişini içermez ve otomatik olarak hiçbir yere yüklenmez. Karakter adları ve ağ adresleri içerebilir; paylaşmadan önce kontrol et. Kaydetme penceresinde konumu sen seçersin.

### Bilinen Sınırlamalar

- Beta ve kısmi protokol desteği: bazı savaş olayları henüz sayılmayabilir.
- Boss Meter, genel/dungeon oturumu toplamları ve iyileştirme/HPS henüz yok.
- Bazı geçişlerde verinin tekrar gelmesi için server'a yeniden giriş gerekebilir.

<small>Teknik not: `0x36` savaş kategorisi henüz desteklenmiyor.</small>

### Nasıl çalışır?

Aion2Meter, Npcap üzerinden AION 2 ağ trafiğini pasif olarak okuyup savaş istatistiklerini bilgisayarında hesaplar. Oyun belleğini okumaz, oyunun işlemine kod enjekte etmez, oyun dosyalarını değiştirmez, oyuna paket göndermez/enjekte etmez ve oynanışı otomatikleştirmez.

---

## Development

Development requires Windows and the .NET 10 SDK (`global.json` selects 10.0.100 with `latestFeature` roll-forward). These tools are for developers, not the planned beta ZIP installation.

From the repository root in PowerShell:

```powershell
. .\dev-env.ps1
dotnet restore Aion2Meter.sln --locked-mode
dotnet build Aion2Meter.sln --no-restore
dotnet test Aion2Meter.sln --no-build --no-restore
```

`build.ps1` also runs restore, build and test in sequence. Automated tests use synthetic data; no running game is required. The native offline writer integration tests need Npcap. To run the app from source: `dotnet run --project src/Aion2Meter.App`.

**Layout:** `Core` contains shared models; `Capture` handles passive capture; `Replay` contains protocol research and combat accounting; `Presentation` contains live sessions and view models; `App` provides WPF UI. Tests live under `tests/`.

**Technical documentation:**

- [Overlay controls and manual checks](docs/overlay-product-ui.md)
- [Party membership and class profiles](docs/live-party-profile.md)
- [Scene continuity and diagnostics](docs/scene-party-lifetime.md)
- [Live pipeline](docs/phase3w-live-pipeline.md), [CURRENT accounting](docs/phase3x-live-combat-meter.md), [checkpointing](docs/phase3y-live-stream-checkpoints.md) and [startup recovery](docs/phase3y1-live-startup-recovery.md)
- [Replay protocol](docs/phase3f-replay-protocol.md), [identity research](docs/phase3h-replay-identities.md), [action correlation](docs/phase3j-replay-action-correlation.md) and [neutral damage events](docs/phase3l-neutral-damage-events.md)
- [All research documentation](docs/)

Keep real captures, metadata, raw research output and capture-derived fixtures out of Git. Use ignored `captures/` or `.tools/tmp/` for local research artifacts.

## License

No project license has been declared yet. Third-party components retain their respective licenses; dependency details are documented in the [replay protocol notes](docs/phase3f-replay-protocol.md). Npcap is installed separately and is not bundled with Aion2Meter.
