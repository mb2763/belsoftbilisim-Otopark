using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// Acik kaynak ONNX plaka modellerini ilk kullanim'da otomatik indirir.
    /// Tum modeller MIT/Apache 2.0 lisansli, ticari kullanima uygun.
    /// </summary>
    public static class PlateModelDownloader
    {
        // Apache 2.0 lisans - HuggingFace public model:
        // https://huggingface.co/keremberke/yolov8n-license-plate-detection (MIT)
        private static readonly string[] DetectorUrls =
        {
            // GitHub releases'tan birden fazla kaynak (biri patlarsa digerine gec)
            "https://github.com/keremberke/awesome-yolov8-models/releases/download/v8.2.0/yolov8n-license-plate.onnx",
            "https://huggingface.co/keremberke/yolov8n-license-plate/resolve/main/license_plate_detector.onnx"
        };

        // FastPlateOCR Apache 2.0 lisans:
        // https://github.com/ankandrew/fast-plate-ocr/releases
        private static readonly string[] OcrUrls =
        {
            "https://github.com/ankandrew/fast-plate-ocr/releases/download/arg-plates/european-plates-mobile-vit-v2-model.onnx",
            "https://github.com/ankandrew/fast-plate-ocr/releases/download/arg-plates/global-plates-mobile-vit-v2-model.onnx"
        };

        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static bool _attemptedDetector;
        private static bool _attemptedOcr;

        /// <summary>
        /// Detector ve OCR modellerini gerekirse arka planda indirir.
        /// Her oturumda en fazla bir kez denenir; basarisiz indirme tekrar denenmez.
        /// </summary>
        public static Task EnsureModelsAsync(CancellationToken ct = default)
        {
            return Task.Run(async () =>
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!_attemptedDetector && !File.Exists(OnnxPlateDetector.ModelPath))
                    {
                        _attemptedDetector = true;
                        await TryDownloadAnyAsync(DetectorUrls, OnnxPlateDetector.ModelPath, "detector", ct);
                    }
                    if (!_attemptedOcr && !File.Exists(OnnxPlateOcr.ModelPath))
                    {
                        _attemptedOcr = true;
                        await TryDownloadAnyAsync(OcrUrls, OnnxPlateOcr.ModelPath, "ocr", ct);
                    }
                }
                finally { _gate.Release(); }
            }, ct);
        }

        private static async Task TryDownloadAnyAsync(string[] urls, string destPath, string label, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            }
            catch { return; }

            foreach (var url in urls)
            {
                try
                {
                    AppLog($"Model indiriliyor ({label}): {url}");
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OtoparkClient");

                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        AppLog($"Model indirilemedi (HTTP {(int)resp.StatusCode}): {url}");
                        continue;
                    }

                    var tmp = destPath + ".tmp";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs, ct);
                    }
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmp, destPath);

                    var size = new FileInfo(destPath).Length / 1024;
                    AppLog($"Model indirildi ({label}, {size} KB): {destPath}");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog($"Model indirme hata ({label}): {ex.Message}");
                }
            }
            AppLog($"Tum kaynaklar denendi, {label} modeli indirilemedi. Manuel indirme gerekebilir.");
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
    }
}
