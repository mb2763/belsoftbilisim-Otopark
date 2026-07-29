using System;
using System.IO;

namespace Otopark.Client.Services;

/// <summary>
/// KAMERA TANI LOGU. Goruntu gelmedigi durumda sebebini gormek icin
/// (hangi bolge sorgulandi, hangi URL cozuldu, baglanti hatasi ne)
/// exe'nin yanindaki <c>camera.log</c> dosyasina yazar.
/// Kamera hatalari eskiden tamamen yutuluyordu; artik kayit altinda.
/// </summary>
public static class CameraDiag
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "camera.log");

    /// <summary>Son hata mesaji (ekranda gostermek icin).</summary>
    public static string LastError { get; private set; } = "";

    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                // Dosya cok buyurse sifirla (basit koruma)
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(LogPath);
                }
                catch { }

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* log yazilamazsa uygulamayi etkilemesin */ }
    }

    public static void Error(string message)
    {
        LastError = message;
        Write("HATA: " + message);
    }
}
