using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Otopark.Client.Converters;

public sealed class PathToImageConverter : IValueConverter
{
    private static DateTime _lastErrLog = DateTime.MinValue;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return null;

        int decodeWidth = 200;
        if (parameter is string ps && int.TryParse(ps, out var pw) && pw > 0)
            decodeWidth = pw;

        try
        {
            if (!File.Exists(path))
            {
                LogErrThrottled($"PathToImage: dosya yok: {path}");
                return null;
            }

            // Dosyayi belleğe oku
            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                data = ms.ToArray();
            }
            if (data.Length == 0)
            {
                LogErrThrottled($"PathToImage: 0 byte: {path}");
                return null;
            }

            // BitmapFrame: BitmapImage + StreamSource'un 'key null' bug'ina karsi alternatif
            var memStream = new MemoryStream(data);
            var frame = BitmapFrame.Create(memStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            // Buyuk goruntuleri olceklendir (bellek tasarrufu)
            if (frame.PixelWidth > decodeWidth)
            {
                double scale = (double)decodeWidth / frame.PixelWidth;
                var scaled = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                scaled.Freeze();
                return scaled;
            }

            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            LogErrThrottled($"PathToImage HATA: {ex.GetType().Name}: {ex.Message} | path={path}");
            return null;
        }
    }

    private static void LogErrThrottled(string msg)
    {
        // 5 saniyede bir log (spam onleme)
        if ((DateTime.Now - _lastErrLog).TotalSeconds < 5) return;
        _lastErrLog = DateTime.Now;
        try
        {
            File.AppendAllText(@"C:\Otopark\log.txt",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{System.Environment.NewLine}");
        }
        catch { }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
