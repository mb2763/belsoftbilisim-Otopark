using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Otopark.Client.Helpers;

/// <summary>
/// C:\Otopark\ImageCache klasoru icin disk bakimi.
/// Calismadigi takdirde klasor sonsuz buyur (367+ dosya, tek dosya 36 MB
/// gibi durumlar olusabiliyor — log analizinden tespit).
///
/// Kurallar:
///   - 7 gunden eski dosyalari sil.
///   - Tek dosya 5 MB'tan buyukse SIL (bozuk/raw - normal plaka snapshot ~100KB).
///   - Klasor toplami 500 MB'i gecerse en eski dosyalari silerek 400 MB'a indir.
/// </summary>
public static class ImageCacheCleanup
{
    private const string CacheDir = @"C:\Otopark\ImageCache";
    private const int MaxAgeDays = 7;
    private const long MaxFileBytes = 5L * 1024 * 1024;          // 5 MB
    private const long MaxFolderBytes = 500L * 1024 * 1024;      // 500 MB
    private const long TargetFolderBytes = 400L * 1024 * 1024;   // 400 MB

    public static void RunInBackground(Action<string>? log = null)
    {
        Task.Run(() =>
        {
            try { Run(log); }
            catch (Exception ex) { log?.Invoke($"ImageCacheCleanup hata: {ex.Message}"); }
        });
    }

    public static void Run(Action<string>? log = null)
    {
        if (!Directory.Exists(CacheDir)) return;

        var di = new DirectoryInfo(CacheDir);
        var files = di.GetFiles("*.*", SearchOption.TopDirectoryOnly);
        var cutoff = DateTime.Now.AddDays(-MaxAgeDays);

        int removedAge = 0, removedBig = 0, removedSize = 0;
        long removedBytes = 0;

        // 1) Eski dosyalar
        foreach (var f in files)
        {
            try
            {
                if (f.LastWriteTime < cutoff)
                {
                    long sz = f.Length;
                    f.Delete();
                    removedAge++; removedBytes += sz;
                }
            }
            catch { }
        }

        // 2) Asiri buyuk tek dosya (raw / bozuk)
        files = di.GetFiles("*.*", SearchOption.TopDirectoryOnly);
        foreach (var f in files)
        {
            try
            {
                if (f.Length > MaxFileBytes)
                {
                    long sz = f.Length;
                    f.Delete();
                    removedBig++; removedBytes += sz;
                }
            }
            catch { }
        }

        // 3) Klasor toplami > 500 MB ise en eskilerden silerek 400 MB'a in
        files = di.GetFiles("*.*", SearchOption.TopDirectoryOnly);
        long total = files.Sum(f => f.Length);
        if (total > MaxFolderBytes)
        {
            var ordered = files.OrderBy(f => f.LastWriteTime).ToList();
            foreach (var f in ordered)
            {
                if (total <= TargetFolderBytes) break;
                try
                {
                    long sz = f.Length;
                    f.Delete();
                    total -= sz;
                    removedSize++; removedBytes += sz;
                }
                catch { }
            }
        }

        log?.Invoke($"ImageCacheCleanup: silinen yas={removedAge} buyuk={removedBig} boyut={removedSize}, " +
                    $"toplam={removedBytes / 1024 / 1024} MB serbest birakildi.");
    }
}
