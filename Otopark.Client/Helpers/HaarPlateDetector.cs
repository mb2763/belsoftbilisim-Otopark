using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using OcvRect = OpenCvSharp.Rect;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// OpenCV Haar Cascade ile plaka bolgesi tespiti.
    /// XML dosyasi C:\Otopark\models\haarcascade_russian_plate_number.xml konumunda olmalidir.
    /// Yoksa otomatik olarak OpenCV GitHub'tan indirilir.
    /// </summary>
    internal sealed class HaarPlateDetector : IDisposable
    {
        private static readonly string CascadeFile = @"C:\Otopark\models\haarcascade_russian_plate_number.xml";
        private const string CascadeUrl = "https://raw.githubusercontent.com/opencv/opencv/4.x/data/haarcascades/haarcascade_russian_plate_number.xml";

        private CascadeClassifier? _classifier;
        private bool _disposed;

        public bool IsAvailable => _classifier != null && !_classifier.Empty();

        public HaarPlateDetector()
        {
            try
            {
                EnsureCascadeFile();
                if (File.Exists(CascadeFile))
                {
                    var c = new CascadeClassifier(CascadeFile);
                    if (c.Empty())
                    {
                        AppLog($"Haar cascade dosyasi yuklenemedi (bos): {CascadeFile}");
                        // Finalizer thread'de native dispose patlamasin diye GC'den cikar
                        GC.SuppressFinalize(c);
                    }
                    else
                    {
                        _classifier = c;
                        AppLog("Haar plaka detektoru hazir.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog($"Haar detector baslatilmadi: {ex.Message}");
                _classifier = null;
            }
        }

        private static void EnsureCascadeFile()
        {
            if (File.Exists(CascadeFile)) return;

            try
            {
                var dir = Path.GetDirectoryName(CascadeFile)!;
                Directory.CreateDirectory(dir);

                AppLog($"Haar cascade indiriliyor: {CascadeUrl}");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                var bytes = http.GetByteArrayAsync(CascadeUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(CascadeFile, bytes);
                AppLog($"Haar cascade indirildi ({bytes.Length / 1024} KB): {CascadeFile}");
            }
            catch (Exception ex)
            {
                AppLog($"Haar cascade indirilemedi: {ex.Message}");
                AppLog($"Manuel indirin: {CascadeUrl}");
                AppLog($"Konum: {CascadeFile}");
            }
        }

        /// <summary>
        /// Goruntude plaka olabilecek dortgen bolgeleri dondurur. Bos liste = bulunamadi.
        /// </summary>
        public List<OcvRect> Detect(Mat image)
        {
            if (_classifier == null || _classifier.Empty() || image.Empty()) return new();

            try
            {
                using var gray = new Mat();
                if (image.Channels() > 1)
                    Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                else
                    image.CopyTo(gray);

                using var equalized = new Mat();
                Cv2.EqualizeHist(gray, equalized);

                var plates = _classifier.DetectMultiScale(
                    image: equalized,
                    scaleFactor: 1.05,
                    minNeighbors: 3,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(40, 12),
                    maxSize: new Size(image.Cols * 3 / 4, image.Rows / 2));

                return plates.ToList();
            }
            catch (Exception ex)
            {
                AppLog($"Haar detect hata: {ex.Message}");
                return new();
            }
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
                _classifier?.Dispose();
                _disposed = true;
            }
        }
    }
}
