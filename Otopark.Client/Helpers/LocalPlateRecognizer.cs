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
    /// Lokal plaka tanima motoru - API yok, internet yok, kota yok.
    /// OpenCV ile goruntu onisleme + Tesseract OCR.
    ///
    /// Gereksinim: C:\Otopark\tessdata\eng.traineddata
    /// Indirme: https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata
    /// </summary>
    internal sealed class LocalPlateRecognizer : IDisposable
    {
        private static readonly string TessDataPath = @"C:\Otopark\tessdata";
        private static readonly Regex TrPlateRx =
            new(@"^(0[1-9]|[1-7][0-9]|8[01])[A-Z]{1,3}[0-9]{2,4}$", RegexOptions.Compiled);

        private TesseractEngine? _engine;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _initError;

        public LocalPlateRecognizer()
        {
            InitEngine();
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
                // Sadece plaka karakterleri: rakam + buyuk Latin harf
                _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                _engine.SetVariable("load_system_dawg", "0");
                _engine.SetVariable("load_freq_dawg", "0");
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

                var candidates = new List<(string plate, double score)>();

                // Farkli bolgeleri dene: tam goruntu, alt yari, alt ucte bir
                var regions = GetCandidateRegions(src);
                foreach (var (region, _) in regions)
                {
                    using (region)
                    {
                        // Normal ve terslenmiş (beyaz zemin + siyah zemin) threshold
                        foreach (bool invert in new[] { false, true })
                        {
                            var r = TryOcrRegion(region, invert);
                            if (r != null) candidates.Add(r.Value);
                        }
                    }
                }

                if (candidates.Count == 0) return null;

                // Turk plakasinı tercih et, sonra en yuksek skoru al
                var best = candidates
                    .Where(c => TrPlateRx.IsMatch(c.plate))
                    .OrderByDescending(c => c.score)
                    .FirstOrDefault();

                if (best == default)
                    best = candidates
                        .Where(c => c.plate.Length >= 5)
                        .OrderByDescending(c => c.score)
                        .FirstOrDefault();

                if (best == default) return null;

                // Turk plakası için skoru biraz artır (format kontrolü geçti)
                double finalScore = TrPlateRx.IsMatch(best.plate)
                    ? Math.Min(1.0, best.score + 0.15)
                    : best.score;

                return new PlateRecognitionResult(best.plate, finalScore);
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

        private IEnumerable<(Mat region, string label)> GetCandidateRegions(Mat src)
        {
            // 1. Tam goruntu
            yield return (src.Clone(), "full");

            // 2. Alt yari (on kameralar icin plaka genellikle alt bolumdedir)
            if (src.Rows > 60)
            {
                int halfY = src.Rows / 2;
                yield return (new Mat(src, new OcvRect(0, halfY, src.Cols, src.Rows - halfY)).Clone(), "bottom-half");
            }

            // 3. Alt ucte bir
            if (src.Rows > 90)
            {
                int thirdY = src.Rows * 2 / 3;
                yield return (new Mat(src, new OcvRect(0, thirdY, src.Cols, src.Rows - thirdY)).Clone(), "bottom-third");
            }

            // 4. Kenar algilama ile otomatik plaka bolgeleri
            foreach (var region in DetectPlateRegions(src))
                yield return (region, "detected");
        }

        private (string plate, double score)? TryOcrRegion(Mat region, bool invert)
        {
            if (_engine == null) return null;

            using var prepared = PrepareForOcr(region, invert);
            if (prepared.Empty()) return null;

            byte[]? imgBytes = null;
            try
            {
                imgBytes = prepared.ToBytes(".png");
            }
            catch { return null; }

            lock (_lock)
            {
                try
                {
                    using var pix = Pix.LoadFromMemory(imgBytes);
                    using var page = _engine.Process(pix, PageSegMode.SingleLine);

                    var raw = page.GetText()?.Trim() ?? "";
                    var plate = PlateRules.Normalize(raw);

                    if (plate.Length < 5 || plate.Length > 10) return null;
                    if (!PlateRules.IsLikelyPlate(plate)) return null;

                    double confidence = page.GetMeanConfidence(); // 0..1
                    if (confidence < 0.30) return null;

                    return (plate, confidence);
                }
                catch { return null; }
            }
        }

        private static Mat PrepareForOcr(Mat src, bool invert)
        {
            var result = new Mat();

            // Minimum 120px yükseklik (OCR dogrulugu icin)
            double scale = Math.Max(1.0, 120.0 / Math.Max(1, src.Rows));
            if (scale > 1.0)
            {
                var sz = new Size((int)(src.Cols * scale), (int)(src.Rows * scale));
                Cv2.Resize(src, result, sz, interpolation: InterpolationFlags.Lanczos4);
            }
            else
                src.CopyTo(result);

            // Gri tona çevir
            using var gray = new Mat();
            if (result.Channels() > 1)
                Cv2.CvtColor(result, gray, ColorConversionCodes.BGR2GRAY);
            else
                result.CopyTo(gray);

            // CLAHE: kontrast iyilestirme
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(gray, enhanced);
            clahe.Dispose();

            // Gurultu azaltma
            using var denoised = new Mat();
            Cv2.FastNlMeansDenoising(enhanced, denoised, h: 10);

            // Otsu esikleme
            using var thresh = new Mat();
            var threshType = ThresholdTypes.Otsu |
                             (invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
            Cv2.Threshold(denoised, thresh, 0, 255, threshType);

            // Morfolojik temizlik (kucuk gurultu noktalarini kaldir)
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2));
            Cv2.MorphologyEx(thresh, result, MorphTypes.Open, kernel);

            return result;
        }

        private static List<Mat> DetectPlateRegions(Mat src)
        {
            var regions = new List<Mat>();
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

                using var blurred = new Mat();
                Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

                // Kenar algilama
                using var edges = new Mat();
                Cv2.Canny(blurred, edges, 50, 150);

                // Plaka karakterlerini birbirine bagla (yatay dilatasyon)
                using var dilated = new Mat();
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(18, 4));
                Cv2.Dilate(edges, dilated, kernel);

                Cv2.FindContours(dilated, out var contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var contour in contours
                    .OrderByDescending(c => Cv2.ContourArea(c))
                    .Take(8))
                {
                    var r = Cv2.BoundingRect(contour);
                    double aspect = (double)r.Width / Math.Max(1, r.Height);

                    // Turk plakasi yaklasik 4.7:1 oran, biraz tolerans ekle
                    if (aspect < 1.8 || aspect > 9.0) continue;
                    if (r.Width < 50 || r.Height < 12) continue;
                    if (r.Width * r.Height < 2000) continue;

                    var expanded = new OcvRect(
                        Math.Max(0, r.X - 6),
                        Math.Max(0, r.Y - 6),
                        Math.Min(src.Cols - r.X, r.Width + 12),
                        Math.Min(src.Rows - r.Y, r.Height + 12));

                    regions.Add(new Mat(src, expanded).Clone());
                    if (regions.Count >= 4) break;
                }
            }
            catch { }
            return regions;
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _engine?.Dispose();
                _disposed = true;
            }
        }
    }

    // PlateRecognizerClient ile uyumlu sonuc sinifi (View tarafinda ayni kullanim)
    internal sealed class PlateRecognitionResult
    {
        public string Plate { get; }
        public double Score { get; }
        public PlateRecognitionResult(string plate, double score) { Plate = plate; Score = score; }
    }
}
