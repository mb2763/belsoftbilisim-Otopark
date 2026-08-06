using OpenCvSharp;
using Otopark.Client.Helpers.Plate;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;
using OcvRect = OpenCvSharp.Rect;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// Multi-engine lokal plaka tanima pipeline'i (tamamen acik kaynak):
    /// 1) ONNX YOLOv8 -> plaka bolgesi tespiti (varsa)
    /// 2) Haar cascade -> plaka bolgesi tespiti (yedek)
    /// 3) ONNX OCR (FastPlateOCR) -> karakter tanima (en yuksek dogruluk)
    /// 4) Tesseract OCR -> karakter tanima (yedek)
    /// 5) PlateFormatLibrary (70 ulke / 660 format) -> sonuc dogrulama + skor
    ///
    /// Birden fazla motor calistirilir, sonuclar oy/skor ile birlestirilir.
    /// </summary>
    public sealed class LocalPlateRecognizer : IDisposable
    {
        private static readonly string TessDataPath = @"C:\Otopark\tessdata";

        // Karakter karisikligi haritalari (OCR sik yanilir)
        private static readonly Dictionary<char, char> LetterToDigit = new()
        {
            ['O'] = '0', ['Q'] = '0', ['D'] = '0',
            ['I'] = '1', ['L'] = '1', ['T'] = '1',
            ['Z'] = '2',
            ['S'] = '5',
            ['G'] = '6',
            ['B'] = '8',
        };

        /// <summary>
        /// Bir karede OCR'a verilecek EN FAZLA plaka bolgesi sayisi.
        /// Dedektor bolgeleri guven sirasina gore dondurur; 5 yerine 2 almak
        /// dogrulugu pratikte etkilemez ama kare basina isi 2.5 kat azaltir.
        /// </summary>
        private const int MaxRegions = 2;

        /// <summary>
        /// OTOMATIK ONAY GUVEN ESIGI - en zayif karakterin model olasiligi bunun altindaysa
        /// okuma SUPHELI sayilir ve personel dogrulamasina duser.
        ///
        /// 0.90 degeri 51 gercek arac gecisi uzerinde olculerek secildi:
        ///     46 DOGRU okumanin hepsi  : MinCharProb >= 0.99
        ///      5 HATALI okumanin hepsi : MinCharProb 0.23 - 0.70
        /// Iki grup arasinda 0.70 ile 0.99 arasi bos bir bant var; 0.90 tam ortasinda,
        /// yani esik kucuk kaymalara karsi dayanikli.
        /// </summary>
        public const double GuvenEsigi = 0.90;

        private TesseractEngine? _engine;
        private readonly OnnxPlateDetector _onnxDetector;
        private readonly OnnxPlateOcr _onnxOcr;
        private readonly HaarPlateDetector _haarDetector;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _initError;

        public LocalPlateRecognizer()
        {
            // Native DLL eksikse hicbir OpenCv tipini olusturma -> finalizer crash'i onle.
            if (!IsOpenCvAvailable())
            {
                AppLog("Lokal motor: OpenCvSharp native DLL yok, devre disi.");
                _initError = true;
                _onnxDetector = null!;
                _onnxOcr = null!;
                _haarDetector = null!;
                return;
            }

            _onnxDetector = new OnnxPlateDetector();
            _onnxOcr = new OnnxPlateOcr();
            _haarDetector = new HaarPlateDetector();
            InitTesseract();

            // _initError = "hicbir motor yok, tanima imkansiz" demektir.
            // ONEMLI: Tesseract'in EKSIK OLMASI bu duruma sokMAZ. Tesseract yalnizca
            // ONNX OCR gecerli bir sonuc uretemediginde devreye giren YEDEKTIR
            // (bkz. satir ~200: "if (_engine != null && !GecerliTrVar())").
            // Eskiden InitTesseract eksik eng.traineddata icin _initError=true yapiyordu;
            // RecognizeInternal da ilk satirda _initError'a bakip cikiyordu. Sonuc:
            // tessdata yoksa ONNX dedektor + ONNX OCR calisir durumdayken bile plaka
            // tanima TAMAMEN ve SESSIZCE oluyordu. tessdata yayin paketinde GELMEDIGI
            // (C:\Otopark\tessdata'da durur) icin temiz bir kurulumda sistem hic calismazdi.
            bool bolgeBulucuVar = _onnxDetector.IsAvailable || _haarDetector.IsAvailable;
            bool okuyucuVar = _onnxOcr.IsAvailable || _engine != null;
            if (!bolgeBulucuVar || !okuyucuVar)
            {
                _initError = true;
                AppLog($"Lokal motor DEVRE DISI: bolge bulucu={bolgeBulucuVar} okuyucu={okuyucuVar}. " +
                       $"C:\\Otopark\\models icindeki plate_detector.onnx / plate_ocr.onnx dosyalarini kontrol edin.");
            }

            AppLog($"Lokal motor: ONNX-detector={_onnxDetector.IsAvailable} ONNX-OCR={_onnxOcr.IsAvailable} " +
                   $"Haar={_haarDetector.IsAvailable} Tesseract={_engine != null} (Tesseract opsiyoneldir) " +
                   $"KULLANILABILIR={!_initError}");
        }

        private static bool IsOpenCvAvailable()
        {
            try { using var m = new Mat(1, 1, MatType.CV_8UC1); return !m.Empty(); }
            catch { return false; }
        }

        private void InitTesseract()
        {
            try
            {
                var trainedFile = Path.Combine(TessDataPath, "eng.traineddata");
                if (!File.Exists(trainedFile))
                {
                    // OPSIYONEL: Tesseract sadece yedek okuyucudur. Yoksa ONNX OCR ile devam edilir.
                    // Burada _initError SET EDILMEZ (bkz. yapicidaki aciklama).
                    AppLog($"Tesseract veri dosyasi yok: {trainedFile} - yedek OCR devre disi, ONNX ile devam ediliyor.");
                    return;
                }

                _engine = new TesseractEngine(TessDataPath, "eng", EngineMode.LstmOnly);
                _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                _engine.SetVariable("load_system_dawg", "0");
                _engine.SetVariable("load_freq_dawg", "0");
                _engine.SetVariable("classify_bln_numeric_mode", "0");
            }
            catch (Exception ex)
            {
                // Tesseract opsiyonel - basarisiz olsa da ONNX ile devam.
                AppLog($"Tesseract baslatilamadi: {ex.Message} - yedek OCR devre disi, ONNX ile devam ediliyor.");
                _engine = null;
            }
        }

        public Task<PlateRecognitionResult?> RecognizeAsync(string imagePath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return RecognizeInternal(imagePath, out _);
            }, ct);
        }

        /// <summary>
        /// RecognizeAsync ile ayni isi yapar, EK OLARAK karede plaka kutusu bulunup
        /// bulunmadigini da bildirir.
        /// KULLANIM AMACI: bulut API kotasini korumak. Bos koridor karesinde (kutu yok)
        /// buluta gitmenin anlami yoktur; sadece "kutu var ama okunamadi" durumunda
        /// buluta basvurulmalidir. Olculdu: 1388 karenin 1209'unda (%87) kutu yok.
        /// </summary>
        public Task<PlateReadOutcome> RecognizeDetailedAsync(string imagePath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sonuc = RecognizeInternal(imagePath, out bool kutuBulundu);
                return new PlateReadOutcome { KutuBulundu = kutuBulundu, Sonuc = sonuc };
            }, ct);
        }

        private PlateRecognitionResult? RecognizeInternal(string imagePath, out bool kutuBulundu)
        {
            kutuBulundu = false;
            if (_initError || !File.Exists(imagePath)) return null;

            Mat? src = null;
            try
            {
                src = Cv2.ImRead(imagePath);
                if (src.Empty()) return null;

                // 1) Plaka bolgelerini bul (ONNX > Haar > heuristic)
                var regions = DetectPlateRegions(src, out string bolgeKaynagi);
                if (regions.Count == 0) return null;

                // Buradan sonrasi = karede plaka kutusu VAR (okunabilse de okunamasa da)
                kutuBulundu = true;

                // Detection bulundu = arac var. Orijinal frame'i ayri klasore kopyala
                // (gelistirme/debug icin; plaka tanindiktan sonra dosya adi guncellenir).
                string? vehicleFramePath = TrySaveVehicleFrame(imagePath);

                // 2) Bolgeleri OCR'a ver. ONCE ONNX OCR (hizli), Tesseract YALNIZCA yedek.
                //
                // PERFORMANS NOTU (olculdu): eskiden her kare icin
                //     5 bolge x 5 varyant  = 25 ONNX OCR   (hizli, ~5 ms)
                //     5 bolge x 3 varyant x 2 PSM = 30 TESSERACT (yavas, ~100 ms+)
                //   ve skor < 0.80 ise TUM bu is ROI kirpma ile IKINCI KEZ yapiliyordu.
                // Sonuc: kare basina ~4 sn. Kamera 0.5 sn'de kare uretirken 10 karenin 9'u
                // atlaniyor, gecen aracin tek karesi isleniyor ve plaka kaciriliyordu.
                //
                // DUZELTME:
                //   - Bolge sayisi 5 -> 2 (dedektor zaten guven sirasina gore veriyor)
                //   - Tesseract SADECE ONNX gecerli bir TR plakasi bulamadiysa calisir
                //     (kod yorumunda da zaten "Tesseract yedek" yaziyordu)
                var allCandidates = new List<Candidate>();
                bool kenardaOkundu = false;   // kazanan okumanin geldigi bolge kare kenarina degiyor mu

                // ONNX OCR yeterince EMIN bir okuma uretti mi? (Tesseract'i tetikleme kosulu)
                // ESKIDEN: "gecerli TR plakasi var mi" -> yabanci plakada Tesseract bosuna
                // calisiyordu (kare basina ~30 cagri, ~100 ms+). Artik olcut GUVEN:
                // model emin olduysa yedege gerek yok, emin degilse yedek devreye girsin.
                bool EminOkumaVar() => allCandidates.Any(c => c.MinCharProb >= GuvenEsigi);

                foreach (var pr in regions.Take(MaxRegions))
                {
                    var region = pr.Region;
                    using (region)
                    {
                        // ONNX OCR'a coklu varyant ver (orijinal + kontrast artirilmis + sharpened).
                        // Boylece tek yanlis okuma yerine en cok consensus alan plaka kazanir.
                        if (_onnxOcr != null && _onnxOcr.IsAvailable)
                        {
                            foreach (var variant in OcrVariants(region))
                            {
                                using (variant)
                                {
                                    var r = _onnxOcr.Recognize(variant);
                                    var normalized = NormalizePlate(r.Plate);
                                    if (normalized.Length >= 5)
                                    {
                                        allCandidates.Add(new Candidate(
                                            normalized, r.Score, "onnx", r.MinCharProb, r.RegionId, pr.Kenarda));
                                    }
                                }
                            }
                        }

                        // Tesseract YEDEK: yalnizca ONNX OCR EMIN bir okuma uretemediyse.
                        //
                        // EK KOSUL (06.08.2026): bolge GUVENILIR bir dedektorden gelmis olmali.
                        // Tesseract cagrisi pahalidir (2 varyant x 2 PSM x bolge = ~30 cagri,
                        // ~700 ms). Haar/sezgisel bir bolge zaten supheli oldugundan oraya bu
                        // maliyeti harcamak kare suresini 3-5 saniyeye cikariyor ve kamera
                        // 500 ms'de kare urettigi icin GERCEK ARACLAR KACIYOR.
                        // ONNX yoksa (model dosyasi eksik) Tesseract tek okuyucudur - o zaman calisir.
                        bool tesseractUygun = bolgeKaynagi == "onnx" || !_onnxDetector.IsAvailable;
                        if (_engine != null && tesseractUygun && !EminOkumaVar())
                        {
                            foreach (var preprocessed in PreprocessVariants(region))
                            {
                                using (preprocessed)
                                {
                                    foreach (var psm in new[] { PageSegMode.SingleLine, PageSegMode.RawLine })
                                    {
                                        var c = TryTesseract(preprocessed, psm);
                                        if (c != null)
                                        {
                                            var tc = c.Value with { Kenarda = pr.Kenarda };
                                            allCandidates.Add(tc);

                                            // FAZ 3: TR karakter duzeltmesi YALNIZCA plaka TR bolgesi
                                            // olarak siniflandirildiysa uygulanir. Aksi halde yabanci
                                            // plaka zorla TR kalibina sokuluyordu (orn. NO MF 38).
                                            // Tesseract bolge bilgisi vermez; o yuzden ONNX'in ayni
                                            // kare icin verdigi bolge kararina bakilir. Hic ONNX
                                            // okumasi yoksa (Tesseract tek basina) TR varsayilir.
                                            bool trVarsay = allCandidates
                                                .Where(x => x.Source == "onnx" && x.RegionId >= 0)
                                                .Select(x => x.RegionId == PlateOcrResult.TrRegionId)
                                                .DefaultIfEmpty(true)
                                                .First();

                                            if (trVarsay)
                                            {
                                                var corrected = CorrectPlateFormat(c.Value.Plate);
                                                if (corrected != c.Value.Plate && corrected.Length >= 5)
                                                {
                                                    allCandidates.Add(new Candidate(
                                                        corrected, c.Value.Score, "tess-corrected",
                                                        c.Value.MinCharProb, -1, pr.Kenarda));
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (allCandidates.Count == 0) return null;

                // 3) ADAY SECIMI — TR TERCIH EDILIR AMA SART DEGIL.
                //
                // ONCEDEN yalnizca TR formatina uyan okumalar kabul ediliyor, digerleri
                // ATILIYORDU. Bu, YABANCI PLAKALI ve formati tam tutmayan araclarin
                // girisinin TAMAMEN KACMASINA yol aciyordu.
                //
                // OLCUM (250 gercek kare): dedektor bos koridor karelerinde SIFIR kutu
                // uretti; kutu buldugu 6 karenin 6'sinda da plaka okundu — ama 6'si da
                // TR formatinda olmadigi icin (DA587AP, H776XL, W73706E gibi yabanci
                // plakalar) mevcut kod hepsini atiyordu.
                //
                // Kural artik: "kutu varsa gercek plaka vardir" -> okumayi KABUL ET.
                // Yanlis bir harf olmasi, aracin tamamen kacmasindan iyidir; personel
                // web/masaustu Plaka Revizyon ekranindan duzeltebilir.
                bool TrMi(string plaka)
                {
                    var m = PlateFormatLibrary.Match(plaka);
                    return m != null && m.CountryCode == "TR";
                }

                if (allCandidates.Count == 0) return null;

                // En iyi plaka secimi:
                // 1) TR formatina uyanlar ONCE (yerel trafik cogunlukta, dogruluk yuksek)
                // 2) En uzun plaka (eksik karakter atlamadan tam okuma) - 38AIH552 > 38AH552
                // 3) En sik tekrar eden (consensus)
                // 4) En yuksek skorlu
                var topGroup = allCandidates
                    .GroupBy(c => c.Plate)
                    .OrderByDescending(g => TrMi(g.Key))
                    .ThenByDescending(g => g.Key.Length)
                    .ThenByDescending(g => g.Count())
                    .ThenByDescending(g => g.Max(c => c.Score))
                    .First();

                var winnerPlate = topGroup.Key;
                var winnerBest = topGroup.OrderByDescending(c => c.MinCharProb).First();
                var winnerSource = winnerBest.Source;
                kenardaOkundu = topGroup.Any(c => c.Kenarda);

                // ---- GUVEN ----
                // ARTIK HAM MODEL GUVENI KULLANILIYOR (06.08.2026).
                //
                // Eskiden OnnxPlateOcr ciktiya IKINCI KEZ softmax uyguluyordu; model %100
                // eminken bile skor ~0.07 geliyordu. Bu bozuk olcumu telafi etmek icin burada
                // +0.55 takviye, +0.05 onnx, +0.03 uzlasma ve 0.90'a zorlama yapiliyordu.
                // Softmax hatasi duzeltildi (bkz. OnnxPlateOcr.DecodeGreedy), dolayisiyla
                // tum bu yapay takviyeler KALDIRILDI.
                //
                // Olculen ayrisma (51 arac gecisi, 1388 kare):
                //     46 DOGRU okumanin hepsi  : MinCharProb >= 0.99
                //      5 HATALI okumanin hepsi : MinCharProb 0.23 - 0.70
                // Bu yuzden esik 0.90: tum dogrular gecer, tum hatalilar yakalanir.
                double minChar = winnerBest.MinCharProb;
                double winnerFinal = minChar;

                // ---- SUPHELI KARARI ----
                // Uc kosuldan biri bozuksa okuma SUPHELI'dir:
                //   1) Bolge guvenilir dedektorden (ONNX) gelmiyor  -> hayalet riski
                //   2) Model karakterlerin birinden emin degil       -> yanlis harf riski
                //   3) Plaka kutusu kare kenarina degiyor            -> plaka yarim kalmis
                // Olcum: kenara degen kutularda dogruluk %100'den %50'ye dusuyor.
                bool guvenilirKaynak = bolgeKaynagi == "onnx";
                bool supheli = !guvenilirKaynak || minChar < GuvenEsigi || kenardaOkundu;

                // ---- TEK-KARE KABUL ----
                // KRITIK TASARIM KARARI: supheli okuma da TEK KAREDE yukari verilir.
                //
                // Rapor "supheli ise skoru 0.89'a cek, stabilizer 2 kare dogrulamasi istesin"
                // diyordu. Bu, olculen veride 51 gecisin 34'u TEK KARELI oldugu icin o araclarin
                // TAMAMEN KACMASINA yol acardi (ornek: 16:09:39 gecisinin tek karesi var).
                // Kullanicinin birinci sarti "hicbir aracin girisi kacmasin"; ikinci sarti
                // "yanlis okuma olabilir". O yuzden okuma her zaman yukari verilir, supheli
                // olanlar OTOMATIK ONAYA girmez (bkz. PersonnelDashboardView) - personel onaylar.
                //
                // Ayrica olculdu: sorunlu 5 gecisin HICBIRINDE plakasi tam gorunen alternatif
                // kare yoktu; yani 2-kare beklemenin kazandiracagi bir sey de yoktu.
                if (winnerFinal < 0.90) winnerFinal = 0.90;

                Candidate? best = new Candidate(
                    winnerPlate, winnerFinal, winnerSource, minChar, winnerBest.RegionId, kenardaOkundu);

                // Plaka tanindi -> kopyalanan vehicle frame dosyasinin adina plakayi ekle
                if (vehicleFramePath != null)
                    TryRenameVehicleFrame(vehicleFramePath, winnerPlate);

                if (supheli)
                {
                    AppLog($"SUPHELI okuma: '{winnerPlate}' minKarakter={minChar:F2} " +
                           $"kaynak={bolgeKaynagi} kenarda={kenardaOkundu} bolge={winnerBest.RegionId} " +
                           $"-> otomatik onay YOK, personel dogrulamasi gerekiyor");
                }

                PlakaIstatistik.Kaydet(supheli, kenardaOkundu, winnerBest.RegionId);

                return new PlateRecognitionResult(
                    best.Value.Plate, best.Value.Score, supheli, minChar, kenardaOkundu, winnerBest.RegionId);
            }
            catch (Exception ex)
            {
                AppLog($"Lokal OCR hata: {ex.Message}");
                return null;
            }
            finally
            {
                src?.Dispose();
            }
        }

        // ===== ADAY BOLGELER =====

        /// <param name="kaynak">
        /// Bolgeleri hangi dedektorun urettigi: "onnx" (guvenilir), "haar", "heuristic", "yok".
        /// Skorlamada tek-kare kabul hakki YALNIZCA "onnx" kaynagina verilir; bu bilgi
        /// alan yerine out parametresi ile tasinir cunku LocalPlateRecognizer ornegi
        /// giris/cikis akislarinca PAYLASILIR (alan kullanmak yaris yaratirdi).
        /// </param>
        private List<PlateRegion> DetectPlateRegions(Mat src, out string kaynak)
        {
            var regions = new List<PlateRegion>();
            kaynak = "yok";

            bool onnxVar = _onnxDetector != null && _onnxDetector.IsAvailable;

            // 1) ONNX YOLOv8 (en hassas, en guvenilir)
            if (onnxVar)
            {
                var boxes = _onnxDetector!.Detect(src);
                foreach (var box in boxes.Take(5))
                    regions.Add(new PlateRegion(ExpandAndCrop(src, box, 8), KenardaMi(box, src)));
                if (regions.Count > 0) { kaynak = "onnx"; return regions; }
            }

            // 2) HAYALET OKUMA KAPISI  *** KONUMU KRITIK ***
            //    Guvenilir dedektor (ONNX) CALISIYOR ve "bu karede plaka YOK" diyorsa,
            //    BASKA HICBIR dedektore dusulmez. Kare bostur, nokta.
            //
            //    06.08.2026 SAHA HATASI: bu kapi ESKIDEN Haar blogundan SONRA duruyordu.
            //    Sonuc: ONNX dogru sekilde "plaka yok" dedigi her bos karede Haar devreye
            //    giriyor ve GORUNTUDEKI TARIH DAMGASINI plaka saniyordu. Log'dan gercek
            //    ornekler (hepsi kaynak=haar):
            //        '08Q62026'  <- "08-06-2026"
            //        '2926TH11'  <- "2026 Thu 11"
            //        'D62026', '0G2026', 'QG2026I', '962026' ...
            //    Personele saniyede bir "PLAKAYI DOGRULAYIN" uyarisi cikiyordu.
            //
            //    Ikinci ve daha agir sonucu: her hayalet okuma "supheli" sayildigi icin
            //    Tesseract (30 cagri) + ROI ile TUM HATTIN IKINCI KEZ calismasi tetikleniyor,
            //    kare suresi 121 ms'den 3-5 SANIYEYE cikiyordu. Kamera 500 ms'de kare
            //    urettigi icin 10 karenin 9'u atlaniyor ve GERCEK ARACLAR KACIYORDU.
            //
            //    05.08 olcumunde bu gorulmedi cunku olcum betigi yalnizca ONNX yolunu
            //    taklit ediyordu; Haar yedegi hic calistirilmamisti.
            if (onnxVar) return regions;   // bos liste

            // ---- Buradan sonrasi YALNIZCA ONNX modeli HIC YOKKEN calisir ----

            // 3) Haar cascade — ONNX yoksa gercek bir dedektor daha denenir.
            if (_haarDetector != null && _haarDetector.IsAvailable)
            {
                var boxes = _haarDetector.Detect(src);
                foreach (var box in boxes.Take(5))
                    regions.Add(new PlateRegion(ExpandAndCrop(src, box, 5), KenardaMi(box, src)));
                if (regions.Count > 0) { kaynak = "haar"; return regions; }
            }

            // 4) Son care: kenar + renk sezgiseli (gurultulu, tek kare kabul edilmez)
            foreach (var r in DetectByEdges(src).Take(5))
                regions.Add(new PlateRegion(r, false));
            foreach (var r in DetectByColor(src).Take(3))
                regions.Add(new PlateRegion(r, false));
            if (regions.Count > 0) kaynak = "heuristic";

            return regions;
        }

        /// <summary>
        /// Plaka kutusu KARE KENARINA degiyor mu? (yani plaka goruntunun disina tasmis olabilir)
        ///
        /// NEDEN: 51 arac gecisi uzerinde olculdu -
        ///   kutu kare ICINDE tam ise  : 41/41 dogru (%100)
        ///   kutu kenara DEGIYORSA     : 5 dogru / 4 yanlis / 1 okunamadi (%50)
        /// Veri setindeki TUM hatalar bu siniftan geldi. Kirpik plakada karakterin bir kismi
        /// goruntude olmadigi icin OCR uydurmak zorunda kaliyor.
        ///
        /// Bu bilgi okumayi ENGELLEMEZ (arac kacmasin) - sadece "supheli" isaretler ve
        /// otomatik onayi kapatir; personel gozuyle dogrular.
        /// </summary>
        private static bool KenardaMi(OcvRect box, Mat src)
        {
            const int PAY = 6;   // px
            return box.X <= PAY
                || box.Y <= PAY
                || box.X + box.Width >= src.Cols - PAY
                || box.Y + box.Height >= src.Rows - PAY;
        }

        private static Mat ExpandAndCrop(Mat src, OcvRect box, int pad)
        {
            int x = Math.Max(0, box.X - pad);
            int y = Math.Max(0, box.Y - pad);
            int w = Math.Min(src.Cols - x, box.Width + 2 * pad);
            int h = Math.Min(src.Rows - y, box.Height + 2 * pad);
            return new Mat(src, new OcvRect(x, y, w, h)).Clone();
        }

        private static List<Mat> DetectByEdges(Mat src)
        {
            var regions = new List<Mat>();
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                using var blurred = new Mat();
                Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
                using var edges = new Mat();
                Cv2.Canny(blurred, edges, 30, 200);
                using var dilated = new Mat();
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(20, 5));
                Cv2.Dilate(edges, dilated, kernel);

                Cv2.FindContours(dilated, out var contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var c in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(12))
                {
                    var r = Cv2.BoundingRect(c);
                    double aspect = (double)r.Width / Math.Max(1, r.Height);
                    if (aspect < 1.6 || aspect > 9.0) continue;
                    if (r.Width < 50 || r.Height < 12) continue;
                    if (r.Width * r.Height < 1500) continue;
                    regions.Add(new Mat(src, ExpandRect(r, src.Size(), 8)).Clone());
                }
            }
            catch { }
            return regions;
        }

        private static List<Mat> DetectByColor(Mat src)
        {
            var regions = new List<Mat>();
            try
            {
                using var hsv = new Mat();
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
                using var whiteMask = new Mat();
                Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 60, 255), whiteMask);
                using var yellowMask = new Mat();
                Cv2.InRange(hsv, new Scalar(15, 80, 100), new Scalar(35, 255, 255), yellowMask);
                using var combined = new Mat();
                Cv2.BitwiseOr(whiteMask, yellowMask, combined);
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 3));
                using var closed = new Mat();
                Cv2.MorphologyEx(combined, closed, MorphTypes.Close, kernel);

                Cv2.FindContours(closed, out var contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var c in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(10))
                {
                    var r = Cv2.BoundingRect(c);
                    double aspect = (double)r.Width / Math.Max(1, r.Height);
                    if (aspect < 1.8 || aspect > 8.0) continue;
                    if (r.Width < 60 || r.Height < 15) continue;
                    if (r.Width * r.Height < 2000) continue;
                    regions.Add(new Mat(src, ExpandRect(r, src.Size(), 10)).Clone());
                }
            }
            catch { }
            return regions;
        }

        private static OcvRect ExpandRect(OcvRect r, Size imgSize, int pad)
        {
            return new OcvRect(
                Math.Max(0, r.X - pad),
                Math.Max(0, r.Y - pad),
                Math.Min(imgSize.Width - Math.Max(0, r.X - pad), r.Width + 2 * pad),
                Math.Min(imgSize.Height - Math.Max(0, r.Y - pad), r.Height + 2 * pad));
        }

        // ===== ONNX OCR VARYANTLARI =====

        /// <summary>
        /// ONNX OCR'a verilecek farkli on-isleme varyantlari:
        /// 1) Orijinal (renkli)
        /// 2) Kontrast artirilmis (CLAHE)
        /// 3) Keskinlestirilmis (unsharp mask)
        /// 4) 2x buyutulmus (kucuk plakalar icin)
        /// 5) 2x buyutulmus + keskinlestirilmis
        /// Birden fazla varyant -> daha guvenilir consensus + karakter segmentasyonu.
        /// </summary>
        private static IEnumerable<Mat> OcrVariants(Mat region)
        {
            // 1) Orijinal (clone)
            yield return region.Clone();

            // 2) CLAHE kontrast
            Mat? clahe = null;
            try
            {
                using var gray = new Mat();
                if (region.Channels() > 1)
                    Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
                else
                    region.CopyTo(gray);
                using var claheOp = Cv2.CreateCLAHE(3.0, new Size(8, 8));
                var enhanced = new Mat();
                claheOp.Apply(gray, enhanced);
                clahe = new Mat();
                Cv2.CvtColor(enhanced, clahe, ColorConversionCodes.GRAY2BGR);
                enhanced.Dispose();
            }
            catch { clahe?.Dispose(); clahe = null; }
            if (clahe != null) yield return clahe;

            // 3) Unsharp mask (keskinlestir)
            Mat? sharp = null;
            try
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(region, blurred, new Size(0, 0), 2.0);
                sharp = new Mat();
                Cv2.AddWeighted(region, 1.6, blurred, -0.6, 0, sharp);
            }
            catch { sharp?.Dispose(); sharp = null; }
            if (sharp != null) yield return sharp;

            // 4) 2x upscale (kucuk plakalar icin karakter ayrimini artirir)
            Mat? upscaled = null;
            try
            {
                upscaled = new Mat();
                Cv2.Resize(region, upscaled, new Size(region.Cols * 2, region.Rows * 2),
                    interpolation: InterpolationFlags.Cubic);
            }
            catch { upscaled?.Dispose(); upscaled = null; }
            if (upscaled != null) yield return upscaled;

            // 5) 2x upscale + keskinlestir
            Mat? upsharp = null;
            try
            {
                using var bigRegion = new Mat();
                Cv2.Resize(region, bigRegion, new Size(region.Cols * 2, region.Rows * 2),
                    interpolation: InterpolationFlags.Cubic);
                using var blurred2 = new Mat();
                Cv2.GaussianBlur(bigRegion, blurred2, new Size(0, 0), 2.5);
                upsharp = new Mat();
                Cv2.AddWeighted(bigRegion, 1.8, blurred2, -0.8, 0, upsharp);
            }
            catch { upsharp?.Dispose(); upsharp = null; }
            if (upsharp != null) yield return upsharp;
        }

        // ===== TESSERACT PREPROCESSING =====

        private static IEnumerable<Mat> PreprocessVariants(Mat region)
        {
            using var resized = ScaleForOcr(region);
            using var gray = new Mat();
            if (resized.Channels() > 1)
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
            else
                resized.CopyTo(gray);

            yield return PreprocessOtsu(gray);
            yield return PreprocessAdaptive(gray);
            yield return PreprocessSharpen(gray);
        }

        private static Mat ScaleForOcr(Mat src)
        {
            var dst = new Mat();
            // Tesseract optimum karakter yuksekligi 32-48px. Plaka tipik yuksekligi
            // detection sonrasi 30-80px arasindaysa, satir yuksekligi 200px hedefleyelim.
            double scale = Math.Max(1.0, 200.0 / Math.Max(1, src.Rows));
            if (scale > 1.0)
                Cv2.Resize(src, dst, new Size((int)(src.Cols * scale), (int)(src.Rows * scale)),
                    interpolation: InterpolationFlags.Cubic);
            else
                src.CopyTo(dst);
            return dst;
        }

        private static Mat PreprocessOtsu(Mat gray)
        {
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(gray, enhanced);
            using var denoised = new Mat();
            Cv2.FastNlMeansDenoising(enhanced, denoised, h: 10);
            var result = new Mat();
            Cv2.Threshold(denoised, result, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
            return result;
        }

        private static Mat PreprocessAdaptive(Mat gray)
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
            var result = new Mat();
            Cv2.AdaptiveThreshold(blurred, result, 255,
                AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 7);
            return result;
        }

        private static Mat PreprocessSharpen(Mat gray)
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 3);
            using var sharp = new Mat();
            Cv2.AddWeighted(gray, 1.5, blurred, -0.5, 0, sharp);
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(sharp, enhanced);
            var result = new Mat();
            Cv2.Threshold(enhanced, result, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
            return result;
        }

        // ===== OCR =====

        private Candidate? TryTesseract(Mat image, PageSegMode psm)
        {
            if (_engine == null || image.Empty()) return null;

            byte[]? imgBytes;
            try { imgBytes = image.ToBytes(".png"); }
            catch { return null; }

            lock (_lock)
            {
                try
                {
                    using var pix = Pix.LoadFromMemory(imgBytes);
                    using var page = _engine.Process(pix, psm);

                    var raw = page.GetText()?.Trim() ?? "";
                    var plate = NormalizePlate(raw);
                    double score = page.GetMeanConfidence();

                    if (plate.Length < 5 || plate.Length > 10) return null;
                    // Threshold dusuruldu (0.20 -> 0.05), format library yanlis pozitifleri filtreler
                    if (score < 0.05) return null;
                    return new Candidate(plate, score, "tesseract");
                }
                catch (Exception ex) { AppLog($"Tesseract hata: {ex.Message}"); return null; }
            }
        }

        // ===== HELPERS =====

        private static string NormalizePlate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var s = raw.Trim().ToUpperInvariant();
            s = new string(s.Where(char.IsLetterOrDigit).ToArray());
            s = s.Replace('İ', 'I').Replace('Ö', 'O').Replace('Ü', 'U')
                 .Replace('Ş', 'S').Replace('Ç', 'C').Replace('Ğ', 'G');
            return s;
        }

        /// <summary>
        /// TR plaka formatina gore karakter karisikligi duzeltir (1-I, 0-O vb).
        /// Pattern: 99[A-Z]{1,3}[0-9]{2,4}
        /// </summary>
        private static string CorrectPlateFormat(string plate)
        {
            if (string.IsNullOrEmpty(plate) || plate.Length < 5 || plate.Length > 10)
                return plate;

            var chars = plate.ToCharArray();
            int n = chars.Length;

            // Pos 0,1: rakam
            for (int i = 0; i < 2; i++)
                if (char.IsLetter(chars[i]) && LetterToDigit.TryGetValue(chars[i], out var d))
                    chars[i] = d;

            // Sondaki rakam blogunu bul
            int trailingDigits = 0, idx = n - 1;
            while (idx >= 2 && trailingDigits < 4)
            {
                char c = chars[idx];
                bool isDigitOrLike = char.IsDigit(c) || LetterToDigit.ContainsKey(c);
                if (!isDigitOrLike) break;
                trailingDigits++; idx--;
            }
            if (trailingDigits < 2) return plate;
            if (trailingDigits > 4) trailingDigits = 4;

            int letterEnd = n - trailingDigits;
            int letterCount = letterEnd - 2;
            if (letterCount < 1 || letterCount > 3) return plate;

            // Orta blok harf olmali
            for (int i = 2; i < letterEnd; i++)
            {
                if (char.IsDigit(chars[i]))
                {
                    chars[i] = chars[i] switch
                    {
                        '0' => 'O', '1' => 'I', '2' => 'Z', '5' => 'S', '6' => 'G', '8' => 'B',
                        _ => chars[i]
                    };
                }
            }
            // Son blok rakam olmali
            for (int i = letterEnd; i < n; i++)
                if (char.IsLetter(chars[i]) && LetterToDigit.TryGetValue(chars[i], out var d))
                    chars[i] = d;

            return new string(chars);
        }

        private static void AppLog(string msg)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // ===== ARAC FRAME'LERI KAYIT (gelistirme/debug icin) =====

        /// <summary>
        /// Detection bulundugunda orijinal kamera frame'ini ayri klasore kopyalar:
        ///   C:\Otopark\VehicleFrames\YYYY-MM-DD\HHmmss_fff__originalName.jpg
        /// Plaka tanindiktan sonra TryRenameVehicleFrame ile filename guncellenir.
        /// </summary>
        private static string? TrySaveVehicleFrame(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;
                var dir = Path.Combine(@"C:\Otopark\VehicleFrames", DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);
                var srcName = Path.GetFileNameWithoutExtension(sourcePath);
                var destPath = Path.Combine(dir, $"{DateTime.Now:HHmmss_fff}__{srcName}.jpg");
                File.Copy(sourcePath, destPath, overwrite: false);
                return destPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Plaka tanindiktan sonra kayitli vehicle frame dosyasinin adini plakayla guncel.ler
        /// Ornek: 100548_344__snap_xxx.jpg -> 100548_344__snap_xxx__81DH999.jpg
        /// </summary>
        private static void TryRenameVehicleFrame(string framePath, string plate)
        {
            try
            {
                if (!File.Exists(framePath) || string.IsNullOrWhiteSpace(plate)) return;
                var safePlate = string.Concat(plate.Where(char.IsLetterOrDigit));
                if (safePlate.Length == 0) return;

                var dir = Path.GetDirectoryName(framePath)!;
                var nameNoExt = Path.GetFileNameWithoutExtension(framePath);
                var ext = Path.GetExtension(framePath);
                var newPath = Path.Combine(dir, $"{nameNoExt}__{safePlate}{ext}");
                if (!File.Exists(newPath))
                    File.Move(framePath, newPath);
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _engine?.Dispose();
                _onnxDetector?.Dispose();
                _onnxOcr?.Dispose();
                _haarDetector?.Dispose();
                _disposed = true;
            }
        }

        private readonly record struct Candidate(
            string Plate,
            double Score,
            string Source,
            double MinCharProb = 0,
            int RegionId = -1,
            bool Kenarda = false);

        /// <summary>Dedektorden gelen plaka bolgesi + kutunun kare kenarina degip degmedigi.</summary>
        private readonly record struct PlateRegion(Mat Region, bool Kenarda);
    }

    public sealed class PlateRecognitionResult
    {
        public string Plate { get; }
        public double Score { get; }

        /// <summary>
        /// TRUE ise okuma otomatik onaya GIRMEMELIDIR - personel gozuyle dogrulamali.
        /// Sebepler: dusuk model guveni, plaka kutusunun kare kenarina degmesi veya
        /// bolgenin guvenilir olmayan bir dedektorden gelmesi.
        /// </summary>
        public bool Supheli { get; }

        /// <summary>En zayif karakterin model guveni [0,1]. Supheli kararinin ana olcutu.</summary>
        public double MinCharProb { get; }

        /// <summary>Plaka kutusu kare kenarina degiyor mu (plaka yarim kalmis olabilir).</summary>
        public bool Kenarda { get; }

        /// <summary>OCR modelinin bolge/ulke sinifi (60 = TR). -1 = bilinmiyor.</summary>
        public int RegionId { get; }

        public PlateRecognitionResult(string plate, double score,
            bool supheli = false, double minCharProb = 1.0, bool kenarda = false, int regionId = -1)
        {
            Plate = plate;
            Score = score;
            Supheli = supheli;
            MinCharProb = minCharProb;
            Kenarda = kenarda;
            RegionId = regionId;
        }
    }

    /// <summary>
    /// Lokal tanima sonucu + karede plaka kutusu bulunup bulunmadigi bilgisi.
    /// KutuBulundu=false ise kare bos koridordur; bulut API'ye gitmek kota israfidir.
    /// </summary>
    public sealed class PlateReadOutcome
    {
        public bool KutuBulundu { get; set; }
        public PlateRecognitionResult? Sonuc { get; set; }
    }
}
