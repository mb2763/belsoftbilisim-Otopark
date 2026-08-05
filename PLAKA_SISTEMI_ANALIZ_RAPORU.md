# PLAKA TANIMA SİSTEMİ — MODEL ANALİZİ ve %100 YOL HARİTASI

**Tarih:** 06.08.2026
**Veri tabanı:** 05.08.2026 günü kaydedilen **1388 gerçek kamera karesi / 51 araç geçişi** (Hunat kapalı otopark, giriş+çıkış kamerası)
**Amaç:** Bu rapor bir uygulama planıdır — "Faz" bölümlerindeki maddeler sırayla koda uygulanacaktır. Tüm dosya/satır referansları `D:\Projelerimmm\Parkomat Projeleri\Otopark` deposuna göredir.

---

## 1. YÖNETİCİ ÖZETİ

**Soru: %100'e yakın kendi plaka sistemimiz olabilir mi?**
**Cevap: Evet — ve büyük kısmı zaten elimizde.** Sistem şu an tamamen yerel (bulut zorunlu değil), açık kaynak modellerle çalışıyor ve ölçülen durum şu:

| Ölçüt | Bugün | Yapılacaklarla |
|---|---|---|
| Araç yakalama (giriş kaçmaması) | %98 (50/51) | ~%100 (kamera açısı + güven kapısı) |
| Plaka tam görünüyorsa doğruluk | **%100 (41/41)** | %100 korunur |
| Genel harf-harf doğruluk | %90 (46/51) | ~%99 otomatik + kalan %1 personel onayı = **%100 sistem** |
| Kare başına süre (CPU) | 121 ms | değişmez (yeterli) |

**En kritik iki bulgu:**
1. Veri setindeki **her hata, istisnasız, plakanın kare kenarında yarım kalmasından** (kamera açısı). Plaka tam görünüyorsa algoritma 41/41 hatasız.
2. C# kodunda **çifte-softmax hatası** var: model zaten olasılık döndürüyor, kod tekrar softmax uygulayıp mükemmel okumaya bile 0.07 skor veriyor. Bu düzeltilirse model **hangi okumadan emin olmadığını kendisi söylüyor** — ölçtük: doğru okumaların tamamı ≥0.99, hatalıların tamamı ≤0.70. Yani sistem "yanılıyorum" demeyi öğrenmeye değil, sadece sesinin duyulmasına muhtaç.

---

## 2. MEVCUT MİMARİ

```
Kamera (MJPEG, 500 ms'de bir kare)
  └─> CameraSnapshotService: kareyi diske yazar
       └─> PersonnelDashboardView: FileSystemWatcher + timer, kareyi işler
            └─> LocalPlateRecognizer.RecognizeInternal
                 1) DetectPlateRegions  : ONNX YOLO > Haar > (sezgisel, ONNX varken kapalı)
                 2) En fazla 2 bölge    : her bölge 3 görüntü varyantı ile ONNX OCR'a
                 3) Tesseract           : SADECE ONNX geçerli sonuç veremezse (yedek)
                 4) Aday birleştirme    : TR-öncelikli sıralama (eleme değil), oylama
                 5) Skor                : +0.55 takviye, ONNX kutusuysa 0.90'a zorla
            └─> PlateStabilizer.Push   : skor ≥0.90 → tek karede kabul; değilse 2 kare/10 sn
            └─> Bulut PlateRecognizer  : SADECE "kutu var ama okunamadı" karesinde (kota koruması)
```

Dosyalar: `Otopark.Client/Helpers/LocalPlateRecognizer.cs`, `OnnxPlateOcr.cs`, `OnnxPlateDetector.cs`, `HaarPlateDetector.cs`, `PlateRecognitionHelpers.cs` (PlateStabilizer + PlateRules + bulut istemci), `Views/PersonnelDashboardView.xaml.cs` (akış + oto-onay).

---

## 3. MODEL ANALİZİ

### 3.1 Dedektör — `plate_detector.onnx` (YOLO11n, plaka fine-tune)

| Özellik | Değer |
|---|---|
| Kaynak | morsetechlab/yolov11-license-plate-detection (YOLO11**n**) |
| Boyut | 10.5 MB |
| Girdi | `[batch, 3, H, W]` float — **dinamik boyut** (biz 640×640 veriyoruz) |
| Çıktı | `[batch, 5, N]` = cx, cy, w, h, skor |
| Süre (CPU) | ~77 ms/kare |
| Eşik | 0.65 güven + 0.45 NMS |

**Ölçülen davranış (1388 kare):**
- 1209 boş koridor karesinde **0 sahte kutu** (hayalet üretmiyor — tek-kare kabulünün güvenlik temeli bu)
- Eşik 0.65→0.20'ye düşürülüp tekrar tarandı: sadece 1 ek okuma çıktı, o da çöp (OCR 0.05). **Eşik araç kaçırmıyor.**
- Kutu hataları yalnızca plaka fiziksel olarak karenin dışına taştığında.

**Not:** Girdi dinamik olduğundan istenirse 960×960 ile uzak/küçük plakalar için ikinci geçiş yapılabilir; mevcut sahnede gerek yok (plakalar büyük).

### 3.2 OCR — `plate_ocr.onnx` (fast-plate-ocr `cct_s_v2_global`)

| Özellik | Değer |
|---|---|
| Kaynak | ankandrew/fast-plate-ocr, CCT (Compact Convolutional Transformer), **Apache-2.0** |
| Boyut | 5.3 MB |
| Girdi | `[N, 64, 128, 3]` **uint8 RGB** (NHWC) |
| Çıktı 1 `plate` | `[N, 10, 37]` — 10 slot × 37 sınıf (0-9, A-Z, `_` dolgu). **DİKKAT: satırlar zaten softmax'lanmış olasılık** (satır toplamı = 1.0) |
| Çıktı 2 `region` | `[N, 66]` — 66 ülke/bölge sınıflandırması. **Şu an hiç kullanılmıyor** |
| Süre (CPU) | ~5 ms/çağrı |

**Ölçülen davranış:** temiz kırpıkta karakter doğruluğu fiilen %100 (41/41 geçiş hatasız; yanlışlar yalnız kırpık görüntüde). Yabancı plakaları da (FR/NL/AT/DE/BE) aynı doğrulukla okuyor — "global" model.

**`region` çıktısı bizim veride tutarlı** (belgelenmiş liste yok, gözlemsel): TR→60, Almanya→23, Belçika→9, Fransa→21, Avusturya→5, Hollanda→43. Kırpık çöp okumada 65 verdi. Ücretsiz bir ülke sınıflandırıcısını kullanmıyoruz.

### 3.3 Yedekler

- **Tesseract 5.2** (`C:\Otopark\tessdata`): yalnız ONNX geçerli sonuç veremeyince; ~100 ms/çağrı. 06.08 düzeltmesiyle **opsiyonel** (yokluğu artık sistemi öldürmüyor).
- **Haar cascade**: ONNX dedektör dosyası yoksa bölge bulucu yedeği.
- **Bulut PlateRecognizer**: yalnız "kutu var, okunamadı" karelerinde (1388 karede 4 kare). Anahtar opsiyonel.

---

## 4. ÖLÇÜLEN PERFORMANS (araç bazlı, 51 geçiş)

| Sonuç | Adet | Not |
|---|---|---|
| En az bir karede okunan | 50/51 (%98) | tek kaçan: 4 karesinde de plaka kenarda kırpık |
| Harf-harf doğru | 46/51 (%90) | |
| **Plaka kare içinde tamsa** | **41/41 (%100)** | |
| Plaka kenarda kırpıksa | 5/10 doğru (%50) | hataların TAMAMI bu sınıfta |

Kanıt metrikleri: doğru okunanlarda plakanın alt kenara ortalama uzaklığı **434 px**; sorunluların medyanı **0 px** (kenara yapışık). Sorunlu 5 geçişin **hiçbirinde** plakası tam görünen alternatif kare yok → bu hata sınıfında yazılımın alabileceği pay **sıfır**, çözüm fiziksel (kamera açısı).

---

## 5. KRİTİK YAZILIM BULGUSU — ÇİFTE SOFTMAX

`OnnxPlateOcr.cs` başlık yorumu "Output: logits" diyor — **bu model için yanlış**. `DecodeGreedy` (satır ~196-218) zaten olasılık olan çıktıya tekrar softmax uyguluyor. Matematiksel sonuç: model %100 eminken bile skor `exp(0)/(exp(0)+36·exp(-1)) ≈ 0.07`. Tüm güven zinciri bu bozuk ölçümü telafi etmek için kurulmuş (`LocalPlateRecognizer.cs` ~286-316: +0.55 takviye, +0.05 onnx, +0.03 uzlaşma, ONNX kutusunda 0.90'a zorlama).

**Ham olasılıklar kullanılırsa ne oluyor — 51 geçişte ölçüldü:**

| Grup | min karakter olasılığı |
|---|---|
| 46 doğru okuma (TR + yabancı hepsi) | **≥ 0.99** |
| 5 sorunlu okuma (kırpık plaka) | **0.23 – 0.70** |

Aradaki boşluk (0.70 ↔ 0.99) devasa: **min-karakter ≥ 0.90 eşiği bu veri setinde kusursuz ayrım sağlıyor** — tüm doğrular geçer, tüm hatalılar yakalanır. Yani:

- Hatalı okuma otomatik kabul edilmez → "DOĞRULA" işaretiyle personele düşer (fotoğrafıyla).
- Kabul edilen her okuma doğrudur → **kabul edilenlerde %100**.

Bu tek düzeltme, sistemin karakterini değiştirir: "bazen yanılan sistem" → "yanıldığını bilen sistem".

---

## 6. %100'E YOL HARİTASI

### FAZ 0 — Kamera açısı (fiziksel, yazılım değil) — **en yüksek getiri**
Kamerayı bir tık yukarı eğ / geri al: araç bariyere yanaştığında plaka karenin içinde kalsın (mevcut doğru okumalardaki gibi alt kenardan ~400 px içeride). Çıkış kamerasında da üst kenar kırpması var (16:06:52 vakası).
**Beklenen:** hata sınıfının tamamen yok olması → harf-harf ~%100.
**Doğrulama:** açı değişiminden sonraki gün `VehicleFrames` üzerinde aynı analiz; kenara değen kutu oranı %20'den ~%0'a inmeli.

### FAZ 1 — Çifte softmax düzeltmesi + güven kapısı (kod; ~yarım gün)
1. `OnnxPlateOcr.DecodeGreedy`: softmax'ı kaldır, `plate` çıktısını doğrudan olasılık olarak kullan. Dönüşe **min-karakter olasılığı** da eklensin: `(Plate, Score, MinCharProb)`.
2. `LocalPlateRecognizer`: skor takviyelerini (+0.55/0.90 zorlaması) ham güvenle değiştir:
   - `bolgeKaynagi=="onnx" && minCharProb >= 0.90` → skor 0.90+ (tek-kare kabul sürer)
   - `minCharProb < 0.90` → skor tavanı 0.89 (stabilizer 2-kare doğrulaması ister) **ve** sonuca `Supheli=true` işareti
3. **Kenar teması kuralı:** dedektör kutusu kare kenarına ≤6 px yaklaşıyorsa okuma ne olursa olsun `Supheli=true` (ölçüm: kenarlı kutularda doğruluk %50'ye düşüyor).
4. Tesseract'ı devreye sokan koşulu güncelle: "geçerli TR yok" yerine "minCharProb < 0.90" (yabancı plakada gereksiz Tesseract çağrısını da bitirir).

### FAZ 2 — Şüpheli okuma akışı (UI; ~yarım gün)
- `Supheli=true` okuma **oto-onaya girmez**; panelde plaka + fotoğraf + "DOĞRULA" butonu; personel tek tıkla onaylar/düzeltir (CorrectPlateWindow zaten var, elle giriş serbestleştirildi).
- Bariyer politikası mevcut tasarıma uyar: personel onayı → giriş kaydı + bariyer.
- Böylece kaçan araç kalmaz: emin → otomatik, emin değil → insanlı, hiç okunamadı → yine insanlı (kutu bulunduğu an kare zaten `VehicleFrames`'e düşüyor).

### FAZ 3 — `region` çıktısını kullan (kod; ~2 saat)
- Ülke logla (raporlama + "yabancı plaka" istatistiği).
- TR karakter düzeltme haritalarını (`LetterToDigit` vb.) **yalnız region=60 iken** uygula — yabancı plakayı TR kalıbına zorlama riskini sıfırlar.

### FAZ 4 — Kendi verimizle fine-tune (isteğe bağlı; sahada %99+ görülürse gerekmez)
Veri toplama altyapısı **zaten çalışıyor**: `VehicleFrames` + personel onay/revizyonları = etiketli veri. 4-6 haftada ~2-5k onaylı plaka birikir.
- **OCR:** fast-plate-ocr eğitim kodu açık (Apache-2.0). `cct_s_v2_global`'i kendi kırpıklarımızla fine-tune; özel augmentasyon: **kenar kırpma simülasyonu**, parlama, açı. Tek GPU'da saatler (RTX 3060+ yeter, bulutta ~20-50$).
- **Dedektör:** gerek görülürse YOLO11n'i kendi karelerimizle fine-tune. **Lisans uyarısı:** YOLO11 ağırlıkları AGPL-3.0 — ticari dağıtımda Ultralytics lisansı gerekir ya da Apache-2.0 alternatif mimariye geçilir (örn. D-FINE/RF-DETR; çıktı formatı farklı, `OnnxPlateDetector`'a ayrı çözümleme dalı gerekir). OCR tarafında böyle bir sorun yok.

### FAZ 5 — İzleme (kod; ~2 saat)
Günlük özet log/rapor: geçiş sayısı, otomatik kabul, şüpheli, okunamayan, ülke dağılımı, kenar-teması oranı (kamera açısı bozulursa erken uyarı).

---

## 7. "KENDİ SİSTEMİMİZ" DEĞERLENDİRMESİ

| Boyut | Durum |
|---|---|
| Bulut bağımlılığı | Yok (bulut yalnız nadir yedek; anahtar opsiyonel) |
| Çalışma maliyeti | 0 TL/okuma; CPU yeterli (121 ms), GPU gerekmez |
| Lisans | OCR Apache-2.0 (ticari serbest); dedektör AGPL-3.0 (Faz 4'te not edildi); Tesseract Apache-2.0; OpenCV Apache-2.0 |
| Veri sahipliği | Tüm kareler ve etiketler bizde — fine-tune için hazır |
| Gerçekçi tavan | Otomatik: ~%99 (kir/hasar/örtülme her sistemde kalır). **Otomatik + şüpheli-onay akışı: %100** — hedefin doğru tanımı bu |

---

## 8. OPUS İÇİN UYGULAMA SIRASI

1. **Faz 1** kod değişiklikleri (`OnnxPlateOcr.cs` DecodeGreedy; `LocalPlateRecognizer.cs` skor bloğu ~286-316; kenar-teması kuralı `DetectPlateRegions` dönüşüne kutu koordinatı taşımayı gerektirir).
2. **Faz 2** UI (`PersonnelDashboardViewModel` + `PersonnelDashboardView` oto-onay koşuluna `!Supheli`; şüpheli listesi kartı).
3. **Faz 3** region (OnnxPlateOcr `results` içinde 2. çıktıyı da oku; `LocalPlateRecognizer`'da TR haritalarını koşullandır).
4. **Faz 5** günlük özet.
5. Test: `scratchpad` içindeki `arac_bazli.py` / `once_sonra.py` / `pad_tarama.py` betikleri aynı 1388 kare üzerinde regresyon ölçümü için kullanılabilir (çifte-softmax'lı `oku()` fonksiyonlarını ham olasılığa çevirmeyi unutma — aksi halde skorlar yine 0.07 görünür).
6. Kabul ölçütleri: (a) 46 doğru geçişin hepsi otomatik kabul, (b) 5 sorunlu geçişin hepsi şüpheli/insanlı, (c) boş koridorda sahte kabul 0, (d) kare süresi <300 ms.

**Faz 0 (kamera açısı) yazılım dışı — sahada yapılacak; yazılım fazları onu beklemek zorunda değil.**
