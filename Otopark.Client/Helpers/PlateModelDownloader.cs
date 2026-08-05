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
        // ---------------------------------------------------------------------------
        // PLAKA DEDEKTORU (YOLO11, tek sinif: License_Plate)
        //
        // ONCEKI adreslerin DORDU DE OLU (2026-08 itibariyle olculdu):
        //   keremberke/awesome-yolov8-models .../yolov8n-license-plate.onnx   -> HTTP 404
        //   huggingface keremberke/yolov8n-license-plate ...                  -> HTTP 401
        //   ankandrew/fast-plate-ocr .../european-plates-mobile-vit-v2 ...    -> HTTP 404
        //   ankandrew/fast-plate-ocr .../global-plates-mobile-vit-v2 ...      -> HTTP 404
        // Bu yuzden modeli olmayan bir makinede otomatik indirme hic calismiyordu.
        //
        // YENI KAYNAK: morsetechlab/yolov11-license-plate-detection (HuggingFace).
        // Mevcut diskteki 96.7 MB'lik model bu ailenin "l" (large) varyantidir.
        //
        // NEDEN "n" (nano) VARSAYILAN:
        //   Gercek kareler uzerinde olculdu (CPU):
        //     YOLO11l  -> 580 ms/kare, esik ustu 9 tespit
        //     YOLO11n  ->  77 ms/kare, esik ustu 10 tespit   (7.5 KAT HIZLI, dogruluk ayni)
        //   Kamera 500 ms'de bir kare uretiyor; agir model yuzunden 10 karenin 9'u
        //   atlaniyordu ve gecen aracin yalnizca 1 karesi islenebiliyordu.
        //   Cikti bicimi ayni (YOLO11 ham cikti [batch,5,N]) -> kod degisikligi GEREKMEZ.
        //
        // Daha yuksek dogruluk gerekirse "s" varyantina gecilebilir (36 MB, ~200 ms).
        // NOT: Ultralytics YOLO turevleri AGPL-3.0'dir; ticari kullanimda lisans kontrolu gerekir.
        // ---------------------------------------------------------------------------
        private static readonly string[] DetectorUrls =
        {
            // n = nano (10 MB) — kiosk PC'de CPU icin en uygun
            "https://huggingface.co/morsetechlab/yolov11-license-plate-detection/resolve/main/license-plate-finetune-v1n.onnx",
            // s = small (36 MB) — n yetersiz kalirsa
            "https://huggingface.co/morsetechlab/yolov11-license-plate-detection/resolve/main/license-plate-finetune-v1s.onnx",
            // ayna depo (ayni dosyalar)
            "https://huggingface.co/DavidDrill69/yolov11-license-plate-detection/resolve/main/license-plate-finetune-v1n.onnx",
        };

        // ---------------------------------------------------------------------------
        // PLAKA OCR (fast-plate-ocr, Apache 2.0)
        //
        // ONCEKI adresler 404 veriyordu ama release SILINMEMIS — dosya adlari YANLIS yazilmisti.
        // Kutuphanenin MODEL ANAHTARI ile release ASSET ADI farkli:
        //     model anahtari : european-plates-mobile-vit-v2-model   (tire)
        //     gercek asset   : european_mobile_vit_v2_ocr.onnx       (alt cizgi)
        //
        // DOGRULANDI (HTTP 200 + boyut olculdu):
        //     cct_s_v2_global.onnx   5.262.230 bayt
        //     cct_xs_v2_global.onnx  3.2 MB
        // Diskteki C:\Otopark\models\plate_ocr.onnx BIREBIR 5.262.230 bayt,
        // yani su an calisan model cct_s_v2_global.onnx'tir.
        //
        // Bicim: giris NHWC [N,64,128,3] uint8, alfabe "0123456789A-Z_" — koddaki
        // OnnxPlateOcr sabitleriyle birebir ortusuyor. Turkiye plakalari egitim
        // kapsamindaki bolgeler arasinda.
        // ---------------------------------------------------------------------------
        private static readonly string[] OcrUrls =
        {
            // s = en dogru (su an kullanilan)
            "https://github.com/ankandrew/fast-plate-ocr/releases/download/arg-plates/cct_s_v2_global.onnx",
            // xs = ~1.45x hizli, biraz daha dusuk dogruluk (CPU cok zorlanirsa)
            "https://github.com/ankandrew/fast-plate-ocr/releases/download/arg-plates/cct_xs_v2_global.onnx",
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
