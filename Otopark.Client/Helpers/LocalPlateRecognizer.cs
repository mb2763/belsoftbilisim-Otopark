using OpenCvSharp;
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
    /// Lokal plaka tanima motoru - API'siz, Tesseract + OpenCV.
    /// Coklu preprocessing + coklu PSM mode + format-aware character correction
    /// ile maksimum dogruluk hedefler.
    /// Gereksinim: C:\Otopark\tessdata\eng.traineddata
    /// </summary>
    internal sealed class LocalPlateRecognizer : IDisposable
    {
        private static readonly string TessDataPath = @"C:\Otopark\tessdata";
        private static readonly Regex TrPlateRx =
            new(@"^(0[1-9]|[1-7][0-9]|8[01])[A-Z]{1,3}[0-9]{2,4}$", RegexOptions.Compiled);

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
        private static readonly Dictionary<char, char> DigitToLetter = new()
        {
            ['0'] = 'O',
            ['1'] = 'I',
            ['2'] = 'Z',
            ['5'] = 'S',
            ['6'] = 'G',
            ['8'] = 'B',
        };

        private TesseractEngine? _engine;
        private readonly OnnxPlateDetector _onnxDetector;
        private readonly HaarPlateDetector _haarDetector;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _initError;

        public LocalPlateRecognizer()
        {
            // Native DLL (OpenCvSharpExtern.dll + VC++ runtime) eksikse hicbir
            // OpenCv tipini olusturma -> finalizer thread'de native call patlamasin.
            if (!IsOpenCvAvailable())
            {
                AppLog("Lokal motor: OpenCvSharp native DLL yok, devre disi.");
                _initError = true;
                _onnxDetector = null!;
                _haarDetector = null!;
                return;
            }

            _onnxDetector = new OnnxPlateDetector();
            _haarDetector = new HaarPlateDetector();
            InitEngine();
        }

        private static bool IsOpenCvAvailable()
        {
            try
            {
                using var m = new Mat(1, 1, MatType.CV_8UC1);
                return !m.Empty();
            }
            catch { return false; }
        }

        private void InitEngine()
        {
            try
            {
                var trainedFile = Path.Combine(TessDataPath, "eng.traineddata");
                if (!File.Exists(trainedFile))
                {
                    AppLog($"HATA: Tesseract veri dosyasi bulunamadi: {trainedFile}");
                    AppLog("Lutfen su adresten eng.traineddata indirin:");
                    AppLog("https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata");
                    AppLog($"Ve su klasore kopyalayin: {TessDataPath}");
                    _initError = true;
                    return;
                }

                _engine = new TesseractEngine(TessDataPath, "eng", EngineMode.LstmOnly);
                _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                _engine.SetVariable("load_system_dawg", "0");
                _engine.SetVariable("load_freq_dawg", "0");
                _engine.SetVariable("classify_bln_numeric_mode", "0");
                AppLog("Lokal plaka tanima motoru hazir.");
            }
            catch (Exception ex)
            {
                AppLog($"Tesseract baslatilamadi: {ex.Message}");
                _initError = true;
            }
        }

        public Task<PlateRecognitionResult?> RecognizeAsync(string imagePath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return RecognizeInternal(imagePath);
            }, ct);
        }

        private PlateRecognitionResult? RecognizeInternal(string imagePath)
        {
            if (_initError || _engine == null || !File.Exists(imagePath)) return null;

            Mat? src = null;
            try
            {
                src = Cv2.ImRead(imagePath);
                if (src.Empty()) return null;

                var allCandidates = new List<Candidate>();

                // 1. Tum aday bolgeleri topla
                int regionCount = 0;
                foreach (var region in GetCandidateRegions(src))
                {
                    if (regionCount++ > 8) { region.Dispose(); break; } // max 8 bolge

                    using (region)
                    {
                        // 2. Her bolge icin sinirli preprocessing (3 varyant)
                        foreach (var preprocessed in PreprocessVariants(region))
                        {
                            using (preprocessed)
                            {
                                // 3. PSM SingleLine + RawLine (plaka tek satir text, fazlasi yanlis pozitif)
                                foreach (var psm in new[] { PageSegMode.SingleLine, PageSegMode.RawLine })
                                {
                                    var c = TryOcr(preprocessed, psm);
                                    if (c == null) continue;
                                    allCandidates.Add(c.Value);

                                    // Her aday icin format-aware duzeltme de adaylara eklensin
                                    var corrected = CorrectPlateFormat(c.Value.Plate);
                                    if (corrected != c.Value.Plate && corrected.Length >= 5)
                                        allCandidates.Add(new Candidate(corrected, c.Value.Score));
                                }
                            }
                        }

                        // Erken cikis: TR formati eslesen aday bulduysak yeter
                        if (allCandidates.Any(x => TrPlateRx.IsMatch(x.Plate)))
                            break;
                    }
                }

                if (allCandidates.Count == 0) return null;

                // 4. Format-aware duzeltmeler ekle
                var withCorrections = new List<Candidate>(allCandidates);
                foreach (var c in allCandidates)
                {
                    var corrected = CorrectPlateFormat(c.Plate);
                    if (corrected != c.Plate && corrected.Length >= 5)
                        withCorrections.Add(new Candidate(corrected, c.Score));
                }

                // 5. Aday secimi: SADECE Turk plaka formatina uyan adaylari kabul et.
                //    Genel plaka kurali rastgele OCR ciktilarinda bile geçtigi icin kullanilmaz.
                var best = withCorrections
                    .Where(c => TrPlateRx.IsMatch(c.Plate))
                    .OrderByDescending(c => c.Score)
                    .FirstOrDefault();

                if (best.Plate == null) return null;

                // 6. SADECE Turk plaka formati eslesirse boost ver. Aksi halde
                //    Tesseract'in ham guveni dosun (genelde dusuk -> downstream esikten geçemez).
                //    Bu, "PPCD45SAT" gibi rastgele OCR ciktilarinin kabulunu onler.
                if (!TrPlateRx.IsMatch(best.Plate))
                    return null;

                // TR plaka formati eslesti -> minimum 0.85 (downstream esik 0.40, rahat geçer)
                double finalScore = Math.Max(0.85, Math.Min(1.0, best.Score + 0.60));

                return new PlateRecognitionResult(best.Plate, finalScore);
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

        private IEnumerable<Mat> GetCandidateRegions(Mat src)
        {
            // 0a. ONNX (YOLOv8) - varsa SADECE onun bolgeleri (en hassas)
            if (_onnxDetector.IsAvailable)
            {
                var onnxBoxes = _onnxDetector.Detect(src);
                foreach (var box in onnxBoxes.Take(3))
                {
                    yield return ExpandAndCrop(src, box, 5);
                }
                if (onnxBoxes.Count > 0) yield break;
            }

            // 0b. Haar cascade - varsa SADECE onun bolgeleri (tam goruntu KULLANILMAZ:
            //     ekrandaki "GIRIS"/"CIKIS" tabela yazilarini plaka sanir)
            if (_haarDetector.IsAvailable)
            {
                var haarBoxes = _haarDetector.Detect(src);
                foreach (var box in haarBoxes.Take(5))
                {
                    yield return ExpandAndCrop(src, box, 5);
                }
                // Haar bulamadiysa fallback'e gec - ama tam goruntu OCR'i yapma
            }

            // Fallback: edge + renk tabanli kirpilmis bolgeler (tabela yazilari icermez)
            int count = 0;
            foreach (var r in DetectPlateRegionsByEdges(src).Take(5))
            {
                yield return r; count++;
                if (count >= 5) break;
            }
            foreach (var r in DetectPlateRegionsByColor(src).Take(3))
            {
                yield return r;
            }
        }

        private static Mat ExpandAndCrop(Mat src, OcvRect box, int pad)
        {
            int x = Math.Max(0, box.X - pad);
            int y = Math.Max(0, box.Y - pad);
            int w = Math.Min(src.Cols - x, box.Width + 2 * pad);
            int h = Math.Min(src.Rows - y, box.Height + 2 * pad);
            return new Mat(src, new OcvRect(x, y, w, h)).Clone();
        }

        private static List<Mat> DetectPlateRegionsByEdges(Mat src)
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

                // Yatay karakterleri birlestir
                using var dilated = new Mat();
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(20, 5));
                Cv2.Dilate(edges, dilated, kernel);

                Cv2.FindContours(dilated, out var contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var contour in contours
                    .OrderByDescending(c => Cv2.ContourArea(c))
                    .Take(12))
                {
                    var r = Cv2.BoundingRect(contour);
                    double aspect = (double)r.Width / Math.Max(1, r.Height);

                    // Plaka oranlari (Turkce: ~4.7:1, biraz tolerans)
                    if (aspect < 1.6 || aspect > 9.0) continue;
                    if (r.Width < 50 || r.Height < 12) continue;
                    if (r.Width * r.Height < 1500) continue;

                    var expanded = ExpandRect(r, src.Size(), 8);
                    regions.Add(new Mat(src, expanded).Clone());
                }
            }
            catch { }
            return regions;
        }

        private static List<Mat> DetectPlateRegionsByColor(Mat src)
        {
            var regions = new List<Mat>();
            try
            {
                using var hsv = new Mat();
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

                // Beyaz: dusuk doygunluk + yuksek aydinlik
                using var whiteMask = new Mat();
                Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 60, 255), whiteMask);

                // Sari (ticari plakalar)
                using var yellowMask = new Mat();
                Cv2.InRange(hsv, new Scalar(15, 80, 100), new Scalar(35, 255, 255), yellowMask);

                using var combined = new Mat();
                Cv2.BitwiseOr(whiteMask, yellowMask, combined);

                // Yatay karakterleri birlestir
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 3));
                using var closed = new Mat();
                Cv2.MorphologyEx(combined, closed, MorphTypes.Close, kernel);

                Cv2.FindContours(closed, out var contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var contour in contours
                    .OrderByDescending(c => Cv2.ContourArea(c))
                    .Take(10))
                {
                    var r = Cv2.BoundingRect(contour);
                    double aspect = (double)r.Width / Math.Max(1, r.Height);

                    if (aspect < 1.8 || aspect > 8.0) continue;
                    if (r.Width < 60 || r.Height < 15) continue;
                    if (r.Width * r.Height < 2000) continue;

                    var expanded = ExpandRect(r, src.Size(), 10);
                    regions.Add(new Mat(src, expanded).Clone());
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

        // ===== PREPROCESSING =====

        /// <summary>
        /// Bir bolge icin 3 hizli preprocessing varyanti.
        /// (Coklu varyant cok yavasti - bolge basina ~18 OCR cagrisi yapiyordu)
        /// </summary>
        private static IEnumerable<Mat> PreprocessVariants(Mat region)
        {
            using var resized = ScaleForOcr(region);

            using var gray = new Mat();
            if (resized.Channels() > 1)
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
            else
                resized.CopyTo(gray);

            // 1. CLAHE + Otsu (klasik beyaz zemin/siyah yazi)
            yield return PreprocessOtsu(gray, false);

            // 2. Adaptive threshold (degisken aydinlik)
            yield return PreprocessAdaptive(gray, false);

            // 3. Sharpen + Otsu (bulanik gorintuler)
            yield return PreprocessSharpen(gray);
        }

        private static Mat ScaleForOcr(Mat src)
        {
            var dst = new Mat();
            // Tesseract optimum yukseklik: 100-200px
            double scale = Math.Max(1.0, 150.0 / Math.Max(1, src.Rows));
            if (scale > 1.0)
            {
                Cv2.Resize(src, dst, new Size((int)(src.Cols * scale), (int)(src.Rows * scale)),
                    interpolation: InterpolationFlags.Lanczos4);
            }
            else
                src.CopyTo(dst);
            return dst;
        }

        private static Mat PreprocessOtsu(Mat gray, bool invert)
        {
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(gray, enhanced);
            clahe.Dispose();

            using var denoised = new Mat();
            Cv2.FastNlMeansDenoising(enhanced, denoised, h: 10);

            var result = new Mat();
            var threshType = ThresholdTypes.Otsu | (invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
            Cv2.Threshold(denoised, result, 0, 255, threshType);
            return result;
        }

        private static Mat PreprocessAdaptive(Mat gray, bool invert)
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);

            var result = new Mat();
            Cv2.AdaptiveThreshold(blurred, result, 255,
                AdaptiveThresholdTypes.GaussianC,
                invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary,
                blockSize: 31, c: 7);
            return result;
        }

        private static Mat PreprocessSharpen(Mat gray)
        {
            // Unsharp mask
            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 3);
            using var sharp = new Mat();
            Cv2.AddWeighted(gray, 1.5, blurred, -0.5, 0, sharp);

            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(sharp, enhanced);
            clahe.Dispose();

            var result = new Mat();
            Cv2.Threshold(enhanced, result, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
            return result;
        }

        private static Mat PreprocessBilateral(Mat gray)
        {
            using var bilateral = new Mat();
            Cv2.BilateralFilter(gray, bilateral, 9, 75, 75);

            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(bilateral, enhanced);
            clahe.Dispose();

            var result = new Mat();
            Cv2.Threshold(enhanced, result, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
            return result;
        }

        // ===== OCR =====

        private Candidate? TryOcr(Mat image, PageSegMode psm)
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
                    var plate = PlateRules.Normalize(raw);

                    if (plate.Length < 5 || plate.Length > 10) return null;

                    // Tesseract ham guven skoru (0-1). Cok dusuk skorlar genelde
                    // halusinasyon (tabela yazisi, gurultu) - reddet.
                    double score = page.GetMeanConfidence();
                    if (score < 0.30) return null;

                    return new Candidate(plate, score);
                }
                catch { return null; }
            }
        }

        // ===== FORMAT-AWARE DUZELTME =====

        /// <summary>
        /// Turk plaka formatina (CC[A-Z]{1,3}[0-9]{2,4}) gore karakter duzeltmeleri yapar.
        /// Pozisyon 0,1 -> rakam olmali (sehir kodu)
        /// Pozisyon 2-4 -> harf olmali (1-3 karakter)
        /// Sonraki -> rakam olmali (2-4 karakter)
        /// </summary>
        private static string CorrectPlateFormat(string plate)
        {
            if (string.IsNullOrEmpty(plate) || plate.Length < 5 || plate.Length > 10)
                return plate;

            var chars = plate.ToCharArray();
            int n = chars.Length;

            // Pozisyon 0,1: rakam olmali
            for (int i = 0; i < 2; i++)
            {
                if (char.IsLetter(chars[i]) && LetterToDigit.TryGetValue(chars[i], out var d))
                    chars[i] = d;
            }

            // Sondaki rakam blogunun uzunlugunu tahmin et (2-4 karakter)
            // Geriye dogru: rakam (veya rakama benzer harf) bulundugu surece say
            int trailingDigitCount = 0;
            int idx = n - 1;
            while (idx >= 2 && trailingDigitCount < 4)
            {
                char c = chars[idx];
                bool isDigitOrLikeDigit = char.IsDigit(c) || LetterToDigit.ContainsKey(c);
                if (!isDigitOrLikeDigit) break;
                trailingDigitCount++;
                idx--;
            }

            // En az 2 rakam olmali
            if (trailingDigitCount < 2) return plate;
            // En fazla 4 al
            if (trailingDigitCount > 4) trailingDigitCount = 4;

            int letterEndExclusive = n - trailingDigitCount;
            // Harf bolumu 1-3 karakter olmali (pozisyon 2..letterEnd)
            int letterCount = letterEndExclusive - 2;
            if (letterCount < 1 || letterCount > 3) return plate;

            // Pozisyon 2..letterEnd: harf olmali
            for (int i = 2; i < letterEndExclusive; i++)
            {
                if (char.IsDigit(chars[i]) && DigitToLetter.TryGetValue(chars[i], out var l))
                    chars[i] = l;
            }

            // Pozisyon letterEnd..n: rakam olmali
            for (int i = letterEndExclusive; i < n; i++)
            {
                if (char.IsLetter(chars[i]) && LetterToDigit.TryGetValue(chars[i], out var d))
                    chars[i] = d;
            }

            return new string(chars);
        }

        // ===== YARDIMCI =====

        private static void AppLog(string msg)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _engine?.Dispose();
                _onnxDetector?.Dispose();
                _haarDetector?.Dispose();
                _disposed = true;
            }
        }

        private readonly record struct Candidate(string Plate, double Score);
    }

    internal sealed class PlateRecognitionResult
    {
        public string Plate { get; }
        public double Score { get; }
        public PlateRecognitionResult(string plate, double score) { Plate = plate; Score = score; }
    }
}
