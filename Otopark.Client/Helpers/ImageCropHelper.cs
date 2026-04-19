using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Otopark.Client.Helpers;

/// <summary>
/// Plaka tanima onuncesi goruntuyu kirpan yardimci.
/// Arac barriere yaklastiginda plaka genelde goruntunun ortasinda/altinda olur,
/// ROI (Region of Interest) kirpma OCR dogrulugunu artirir.
/// </summary>
internal static class ImageCropHelper
{
    /// <summary>
    /// Goruntuyu ROI'ye gore kirpar, temp klasore kaydedip yolunu dondurur.
    /// Basarisiz olursa orijinal yolu dondurur (OCR yine calisir).
    /// </summary>
    public static string CropToRoi(string sourcePath, double xPct, double yPct, double wPct, double hPct)
    {
        try
        {
            if (!File.Exists(sourcePath)) return sourcePath;

            // Tum parametreler [0,1] araliginda
            xPct = Math.Clamp(xPct, 0.0, 1.0);
            yPct = Math.Clamp(yPct, 0.0, 1.0);
            wPct = Math.Clamp(wPct, 0.01, 1.0);
            hPct = Math.Clamp(hPct, 0.01, 1.0);

            BitmapImage src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            src.UriSource = new Uri(sourcePath, UriKind.Absolute);
            src.EndInit();
            src.Freeze();

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int x = (int)(w * xPct);
            int y = (int)(h * yPct);
            int cw = (int)(w * wPct);
            int ch = (int)(h * hPct);
            if (x + cw > w) cw = w - x;
            if (y + ch > h) ch = h - y;
            if (cw < 10 || ch < 10) return sourcePath;

            var cropped = new CroppedBitmap(src, new System.Windows.Int32Rect(x, y, cw, ch));

            var tempDir = Path.Combine(Path.GetTempPath(), "OtoparkRoi");
            Directory.CreateDirectory(tempDir);
            var dest = Path.Combine(tempDir,
                $"roi_{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.jpg");

            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(fs);

            return dest;
        }
        catch
        {
            return sourcePath;
        }
    }

    /// <summary>
    /// Temp ROI klasorundeki eski dosyalari siler (10 dk'dan eski).
    /// </summary>
    public static void CleanupTempRoi()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "OtoparkRoi");
            if (!Directory.Exists(tempDir)) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var f in Directory.GetFiles(tempDir, "*.jpg"))
            {
                try
                {
                    if (new FileInfo(f).LastWriteTimeUtc < cutoff) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
