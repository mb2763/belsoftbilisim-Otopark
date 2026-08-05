# KAPALI OTOPARK — PERFORMANS ANALİZ RAPORU

**Tarih:** 06.08.2026
**Soru:** "Projede genel bir yavaşlık var mı?"
**Cevap:** Var — ve tek bir kök nedeni var: **kamera karesi temizliği kapalı unutulmuş (`MaxFiles = 0`)**, üstüne UI iş parçacığı her 400 ms'de bu sınırsız büyüyen klasörü 4 kez baştan sona tarıyor. Program açık kaldıkça klasör büyür, her tarama uzar, arayüz kademeli olarak donar. Diğer bulgular ikincil ama aynı elden düzeltilmeli.

Bu rapor bir uygulama planıdır (önceki `PLAKA_SISTEMI_ANALIZ_RAPORU.md` gibi): Faz bölümleri sırayla koda uygulanacak. Tüm referanslar `D:\Projelerimmm\Parkomat Projeleri\Otopark` deposuna göredir.

---

## 1. KÖK NEDEN — sınırsız klasör × saniyede 10 tarama

### 1a. Temizlik kapalı: `CameraSnapshotService.cs:31`

```csharp
private const int MaxFiles = 0;      // "gelistirme suresi" notuyla birakilmis
...
if (MaxFiles <= 0) return;           // CleanupOldFiles HIC SILMIYOR
```

Kamera döngüsü her **500 ms'de bir** kareyi `D:\GESI\OTOPARK\EntryCapture` / `ExitCapture` klasörüne yazıyor (`CameraSnapshotService.cs:259-289`). Temizlik çalışmadığı için:

| Çalışma süresi | Dosya / kamera |
|---|---|
| 1 saat | 7.200 |
| 4 saat | 28.800 |
| 8 saat | 57.600 |
| 1 gün açık kalırsa | 172.800 |

Yeniden başlatmak **kurtarmaz** — dosyalar diskte kalır, program açılır açılmaz yine yavaştır. Ancak klasör elle boşaltılınca düzelir. ("Bazen hızlı bazen yavaş" şikayetinin klasik imzası.)

### 1b. UI iş parçacığı bu klasörü sürekli tarıyor: `PersonnelDashboardView.xaml.cs`

- `_uiTimer` **400 ms** (`:351`) → `LoadLatestImages()` (`:1113`) her tick'te **4 ayrı** `Directory.GetFiles("*.*") + FileInfo + OrderByDescending` taraması yapıyor (giriş büyük, giriş 2 küçük, çıkış büyük, çıkış 2 küçük — `GetLatestImageFile` `:1149`, `GetLatestImageFiles` `:1159`). Hepsi **UI thread'de**.
- `_detectTimer` **1500 ms** (`:359`) → `DetectFromFolderAsync` aynı taramayı 1-3 kez daha yapıyor.
- 30 sn'de bir tanı logu 2 `GetFiles` daha (`:1138-1140`).

### Ölçüm (sentetik, aynı işlem dizisi: listele + stat + sırala)

| Klasördeki dosya | Tek tarama | LoadLatestImages'ın 4 taraması |
|---|---|---|
| 1.000 | 28 ms | 112 ms |
| 5.000 | 159 ms | 636 ms |
| 20.000 | 1.078 ms | **4.3 sn** |
| 60.000 | 3.742 ms | **15 sn** |
| 120.000 | 6.705 ms | **27 sn** |

Tick aralığı 400 ms. Yani **~2 saat çalışma sonrası (20k dosya) UI thread tick başına 4+ sn işe gömülüyor** — arayüz fiilen donuyor; tıklamalar, plaka kartları, bariyer butonu hepsi bu kuyruğun arkasında bekliyor. 4-8 saatte durum 15-27 sn/tick'e gider. **"Genel yavaşlık" budur.**

### FAZ A — düzeltme (kök neden, ~1 saat)

1. `MaxFiles = 0` → **400** yap (kamera başına ~3,3 dakikalık tampon; UI yalnız son 3 kareyi gösteriyor, stabilizer penceresi 10 sn, DuplicateSuppressor 120 sn → 400 fazlasıyla yeterli).
2. `CleanupOldFiles` zaten her kayıtta çağrılıyor (`:289`) — sabit değişince çalışmaya başlar. n≈400'de maliyeti önemsiz.
3. **Birikmiş sahayı kurtarma:** uygulama açılışında (kamera döngüsü başlamadan, `Task.Run` içinde) her iki capture klasöründe `snap_*.jpg` sayısı > 2×MaxFiles ise eskileri sil. Sahada 100k+ dosya birikmiş olabilir; bu süpürme olmadan ilk `CleanupOldFiles` çağrısı tek seferde on binlerce silmeye çalışır (dakikalar sürer, kamera döngüsünü tıkar — `SaveFrame` içinden çağrıldığı için ilk kare gecikir).
4. Silme LastWriteTime sıralamasıyla değil **dosya adına göre** yapılabilir (`snap_yyyyMMdd_HHmmss_fff` adı zaten sıralı) → FileInfo/stat maliyeti tamamen kalkar: `GetFiles` + `Array.Sort(names)` yeter.

**Kabul ölçütü:** 8 saat kesintisiz çalışmada klasör dosya sayısı ~400'de sabit; `LoadLatestImages` süresi sabit (<5 ms); UI donması yok.

---

## 2. FAZ B — LoadLatestImages: 4 tarama → 1 tarama, UI thread dışına (~1 saat)

`PersonnelDashboardView.xaml.cs:1113-1144`

Aynı klasör aynı tick'te iki kez taranıyor (büyük görsel için 1, küçükler için 1); giriş+çıkış = 4. Ayrıca sıralama tüm klasörü `FileInfo`'ya çevirip `LastWriteTimeUtc`'ye göre yapılıyor.

1. Klasör başına **tek** tarama: en yeni 3 dosyayı bir geçişte seç (tam sıralama yerine "top-3" seçimi ya da ad-sıralı son 3 — adlar zaten kronolojik).
2. Taramayı `Task.Run` içine al; yalnız sonuç atamalarını (`vm.EntryCameraImagePath = ...`) Dispatcher'a döndür. `_uiTimer.Tick` yeniden-giriş koruması olarak `_uiBusy` bayrağı ekle (detect timer'daki `_tickBusy` deseni `:363-370`'te hazır).
3. 30 sn'lik tanı logu dosya sayısını ayrı `GetFiles` ile değil, aynı taramanın sonucundan alsın.
4. `DetectFromFolderAsync`'in `GetLatestImageFile` çağrıları da (\:brk`:392-404` içinde 1-3 tarama) FAZ A sonrası ucuzlar; B'deki tek-tarama yardımcusunu paylaşması yeterli.

**Kabul ölçütü:** tick başına klasör taraması ≤2 (giriş+çıkış), UI thread'de tarama 0.

---

## 3. FAZ C — Görsel decode maliyeti: tam çözünürlük → hedef genişlik (~30 dk)

`Converters/PathToImageConverter.cs:44-58`

Şu an: dosya belleğe okunuyor → `BitmapFrame.Create(..., BitmapCacheOption.OnLoad)` ile **tam çözünürlükte decode** (2560×1440 kare ≈ 14 MB piksel tamponu, ~10-25 ms) → sonra `TransformedBitmap` ile küçültme. Bu, binding üzerinden **UI thread'de**, kamera görüntüsü her değiştiğinde (500 ms'de bir × 6 Image kontrolü) çalışıyor → sürekli CPU + LOH/GC baskısı.

Düzeltme: `BitmapImage` + **`DecodePixelWidth = decodeWidth`** kullan — JPEG decoder doğrudan küçük boyutta açar (5-10× daha az iş ve bellek). Dosyadaki yorum "BitmapImage + StreamSource 'key null' bug'ı" nedeniyle `BitmapFrame`'e geçildiğini söylüyor; bu yüzden:

1. Önce `BitmapImage` + `StreamSource=MemoryStream` + `DecodePixelWidth` + `CacheOption=OnLoad` + `Freeze` dene (bug tekrar ederse görülecek — log zaten throttle'lı).
2. Bug tekrarlarsa geri çekilme planı: `BitmapFrame` yolu kalsın ama **converter'a küçük bir ön-bellek** ekle (`path → frozen bitmap`, son 8 kayıt): aynı dosya ikinci kez decode edilmez (aynı tick'te büyük + küçük görsel aynı dosyayı iki kez decode ediyor).

**Kabul ölçütü:** kamera akışı açıkken işlemcide görünür düşüş; çalışma kümesinde (Task Manager) büyüme durması.

---

## 4. FAZ D — VehicleFrames sınırsız büyüyor (~30 dk)

`LocalPlateRecognizer.cs:799` (`TrySaveVehicleFrame`): kutu bulunan **her kare** `C:\Otopark\VehicleFrames\<yyyy-MM-dd>\` altına kopyalanıyor. Ölçüm: **tek günde 1.933 dosya / 425 MB** → ayda ~13 GB. Disk dolunca her şey yavaşlar; ayrıca şüpheli-onay akışının kanıt fotoğrafları da bu diskte.

Düzeltme: gün-klasörü yapısı zaten var — açılışta + günde bir kez **14 günden eski gün-klasörlerini sil** (`Task.Run`, `PlakaIstatistik.Bitir()` gibi sessiz). 14 gün, personel revizyon/itiraz penceresi için yeterli; sabit yap ki istenirse değiştirilebilsin.

**Kabul ölçütü:** `VehicleFrames` altında en fazla 14 gün klasörü.

---

## 5. Küçük bulgular (aynı elde düzeltilebilir, tek başına yavaşlık yapmaz)

| Yer | Bulgu | Öneri |
|---|---|---|
| `PersonnelDashboardViewModel.cs:1651` (Logout) | Her çıkışta `new HttpClient` + 6. kopya gömülü URL | `App`'teki paylaşılan client'ı ver; URL tekilleştirme zaten bilinen konu |
| `CameraSnapshotService.cs` MJPEG | Kare **her zaman** diske yazılıp UI dosyadan okuyor | İleride: son kareyi bellekte tut (`byte[]`), UI oradan bağlansın — FAZ A/B sonrası gerek kalmayabilir |
| `log.txt` | Şu an 0,2 MB — sorun değil; ama sınırsız append | 10 MB üstünde `log_eski.txt`'ye döndür (tek `File.Move`) |

## 6. Yanlış alarm — Opus vakit harcamasın

- `App.xaml.cs:44 .Wait()` yalnız `--download-models` argümanıyla çalışıyor; normal açılışta yok.
- `LoginViewModel` / `PersonnelDashboardViewModel` içindeki `result.Result`, `response?.Result` ifadeleri **`IDataResult<T>.Result` özelliğidir**, `Task.Result` değil — bloklama yok.
- `EnsureModelsAsync` modeller diskte varken anında dönüyor.
- OCR boru hattı zaten optimize (kare başına ort. 83 ms, ölçüldü) — dokunma.

---

## 7. UYGULAMA SIRASI (Opus için)

1. **FAZ A** — `MaxFiles=400` + açılış süpürmesi + ada-göre silme (`CameraSnapshotService.cs`)
2. **FAZ B** — tek tarama + `Task.Run` (`PersonnelDashboardView.xaml.cs: LoadLatestImages, GetLatestImageFile(s), DetectFromFolderAsync`)
3. **FAZ C** — `DecodePixelWidth` (`PathToImageConverter.cs`) — 'key null' geri çekilme planıyla
4. **FAZ D** — VehicleFrames 14 gün saklama (`LocalPlateRecognizer.cs` veya `PlakaIstatistik` yanına ayrı yardımcı)
5. Bölüm 5'teki küçükler (isteğe bağlı, aynı commit'te olabilir)
6. Derle → publish → `D:\OtoparkTest\1_Uygulama` tazele → commit/push

**Regresyon:** değişiklik plaka tanıma mantığına dokunmuyor; yine de `scratchpad\regresyon.py` bir kez koşturulup (a)-(d) ölçütlerinin bozulmadığı teyit edilmeli.

**Sahada doğrulama:** bir tam gün sonra `D:\GESI\OTOPARK\EntryCapture` dosya sayısı ~400 sabit mi + `C:\Otopark\plaka_ozet.log` günlük satırı normal mi.

---

## 8. UYGULAMA DURUMU (06.08.2026 — tamamlandı)

Faz A, B, C, D ve bölüm 5'teki küçük bulgular uygulandı.

### Teşhis sahada doğrulandı

Son gerçek oturumun logu (`C:\Otopark\log.txt`, 05.08 16:32) teşhisi birebir onayladı:

```
UI: Entry=D:\GESI\OTOPARK\Entry\2026\08\05\ (17886 dosya) | Exit=...\ (18963 dosya)
```

~18.000 dosya — ölçümde tick başına ~12 saniyeye denk gelen aralık. "Genel yavaşlık" bu.

### Raporun bir varsayımı yanlıştı — düzeltildi

Rapor capture klasörünü `D:\GESI\OTOPARK\EntryCapture` (tek klasör) sanıyordu. Gerçekte **tarihe göre bölünüyor**: `D:\GESI\OTOPARK\Entry\yyyy\MM\dd\` (`PersonnelDashboardView.xaml.cs:47-48`).

Bunun iki sonucu var:
1. Birikim **gün içinde** oluyor (gece 00:00'da yeni klasör) — yine de 12 saatte ~86.000 dosyaya çıkıyor, sorun aynen geçerli.
2. **Rapor bunu kaçırmıştı:** eski gün klasörleri hiç silinmiyor. Diskte Nisan 2026'dan beri klasörler duruyordu. Faz D bu yüzden genişletildi.

### Uygulanan değişiklikler

| Faz | Dosya | Değişiklik |
|---|---|---|
| A | `Services/CameraSnapshotService.cs` | `MaxFiles = 0` → **400**. `CleanupOldFiles` artık `FileInfo`/stat kullanmıyor, dosya **adına** göre siliyor (`snap_yyyyMMdd_HHmmss_fff` zaten kronolojik). Yeni `TemizleAsync()` — açılış süpürmesi. |
| A | `Views/PersonnelDashboardView.xaml.cs` | `TemizleAsync` kamera döngüsü **başlamadan** çağrılıyor (fire-and-forget), yoksa ilk `CleanupOldFiles` on binlerce dosyayı `SaveFrame` içinden silmeye çalışıp döngüyü tıkardı. |
| B | `Views/PersonnelDashboardView.xaml.cs` | `LoadLatestImages` tamamen `Task.Run` içinde; sadece atamalar `Dispatcher`'da. `_uiBusy` yeniden-giriş koruması. Klasör başına **tek** tarama (4 → 2). Yeni `SonGorseller()` tek geçişte en yeni n dosyayı seçiyor, **stat çağrısı yok**. Tanı logunun sayısı aynı taramadan geliyor (ekstra `GetFiles` kalktı). |
| B | aynı dosya | `DetectFromFolderAsync` da aynı yardımcıyı kullanıyor; `GetLatestImageFile`/`GetLatestImageFiles` kaldırıldı, yerine `GetLatestImagePath`. |
| C | `Converters/PathToImageConverter.cs` | `BitmapImage` + **`DecodePixelWidth`** — JPEG doğrudan hedef genişlikte açılıyor, tam boyutlu ara tampon (2560×1440 ≈ 14 MB) hiç oluşmuyor. 'key null' bug'ı tekrarlarsa `catch` ile eski `BitmapFrame` yoluna dönülüyor (loglanıyor). |
| D | `Helpers/DiskBakim.cs` (yeni) | 14 günden eski **gün klasörlerini** siliyor: `Entry\yyyy\MM\dd`, `Exit\yyyy\MM\dd` ve `VehicleFrames\yyyy-MM-dd` (iki yapı da destekleniyor). Boşalan yıl/ay klasörlerini topluyor. Log 10 MB'ı aşarsa `log_eski.txt`'ye döndürüyor. Tarih **klasör adından** çözülüyor (dosya damgasına güvenilmiyor). |
| 5 | `ViewModel/PersonnelDashboardViewModel.cs` + `Api/Services/ZoneApiService.cs` | Logout'taki `new HttpClient` kaldırıldı; mevcut `_zoneApi.Http` yeniden kullanılıyor (soket birikmesi + gömülü URL'nin 6. kopyası gitti). |

### Ölçüm — önce/sonra (aynı işlem dizisi)

| Klasördeki dosya | ESKİ (4 tarama, stat'lı) | YENİ (2 tarama, stat'sız) | Kazanç |
|---|---|---|---|
| **400** ← Faz A sonrası kalıcı durum | **103 ms** | **2 ms** | **44×** |
| 1.000 | 324 ms | 11 ms | 29× |
| 5.000 | 2.202 ms | 88 ms | 25× |
| 20.000 | 12.303 ms | 356 ms | 35× |
| 60.000 | 18.427 ms | 261 ms | 71× |

Faz A klasörü 400'de sabitlediği için pratikte hep ilk satır geçerli: **tick başına 103 ms → 2 ms**, üstelik artık UI thread'inde değil.

### Duman testi (yapılan / yapılmayan)

Yapıldı: uygulama publish edilip çalıştırıldı — açılışta çökme yok, 140 MB RAM, `Responding=True`, log'da istisna yok.

**Yapılmadı (dürüstlük notu):** bu makinede kameralara erişim olmadığı ve giriş yapılmadığı için `LoadLatestImages`, `DiskBakim` ve yeni decode yolu **canlı koşturulamadı**. Kod yolları derleniyor ve mantıkları ayrı ayrı ölçüldü, ama sahadaki ilk çalıştırmada log kontrol edilmeli:
- `Acilis temizligi: ... -> N dosya silindi` satırları
- `Disk bakimi: silindi ...` satırları
- `PathToImage: hizli decode basarisiz` satırı **görülmemeli** (görülürse Faz C geri çekilmeye düşmüş demektir; çalışmaya devam eder ama kazanç olmaz)

### Sahada doğrulama (1 gün sonra)

- `D:\GESI\OTOPARK\Entry\<bugün>` dosya sayısı **~400'de sabit** mi
- `D:\GESI\OTOPARK\Entry` altında 14 günden eski klasör kalmamış olmalı
- Arayüzde saatler geçtikçe yavaşlama **olmamalı** (asıl kabul ölçütü)
