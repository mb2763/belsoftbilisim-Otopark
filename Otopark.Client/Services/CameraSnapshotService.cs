using Otopark.Core.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Client.Services;

/// <summary>
/// Axis kameralardan MJPEG stream uzerinden surekli frame yakalayan servis.
/// Snapshot yerine canli video akisi cozumu hareket halindeki araclar icin idealdir.
/// </summary>
public static class CameraSnapshotService
{
    // Once web'den DB'ye tanimlanan kamera (CameraConfigService) varsa onu kullan; yoksa appsettings (fallback).
    private static string EntryUrl => !string.IsNullOrWhiteSpace(CameraConfigService.EntryUrl)
        ? CameraConfigService.EntryUrl
        : (AppConfig.Configuration["CameraSnapshot:EntrySnapshotUrl"] ?? "");
    private static string ExitUrl => !string.IsNullOrWhiteSpace(CameraConfigService.ExitUrl)
        ? CameraConfigService.ExitUrl
        : (AppConfig.Configuration["CameraSnapshot:ExitSnapshotUrl"] ?? "");
    private static int SaveIntervalMs => int.TryParse(AppConfig.Configuration["CameraSnapshot:IntervalMs"], out var v) ? v : 500;

    /// <summary>
    /// Capture klasorunde tutulacak maksimum kare sayisi.
    ///
    /// KRITIK (06.08.2026): burasi 0 idi ("gelistirme sureci" notuyla) ve 0 = SILME YOK
    /// anlamina geliyordu. Kamera 500 ms'de bir kare yazdigi icin klasor saatte ~7.200,
    /// 8 saatte ~57.600 dosyaya cikiyordu. UI thread ise her 400 ms'de bu klasoru bastan
    /// sona tarayip siraliyordu. Olculdu (listele+stat+sirala):
    ///     1.000 dosya ->  28 ms/tarama
    ///    20.000 dosya -> 1.078 ms/tarama   (tick basina 4 tarama = 4,3 sn)
    ///    60.000 dosya -> 3.742 ms/tarama   (tick basina 15 sn)
    /// Yani ~2 saat calisma sonrasi arayuz fiilen doniyordu. "Genel yavaslik" buydu.
    ///
    /// 400 neden yeterli: UI yalnizca son 3 kareyi gosteriyor, stabilizer penceresi 10 sn,
    /// DuplicateSuppressor 120 sn. 400 kare ~3,3 dakikalik tampon demek - fazlasiyla yeter.
    /// Plaka tanindiginda o kare zaten VehicleFrames'e ve snapshot klasorune kopyalaniyor,
    /// yani kanit fotograflari bu temizlikten ETKILENMEZ.
    /// </summary>
    private const int MaxFiles = 400;

    // JPEG marker'lari
    private static readonly byte[] JpegStart = { 0xFF, 0xD8 };
    private static readonly byte[] JpegEnd = { 0xFF, 0xD9 };

    public static void Start(string entryCaptureFolder, string exitCaptureFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EntryUrl) && string.IsNullOrWhiteSpace(ExitUrl))
        {
            CameraDiag.Error("Kamera URL'i YOK (ne web'de tanim var ne de appsettings yedegi acik) -> goruntu baslatilamadi.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(EntryUrl))
            Task.Run(() => StreamLoopAsync(EntryUrl, entryCaptureFolder, ct), ct);

        if (!string.IsNullOrWhiteSpace(ExitUrl))
            Task.Run(() => StreamLoopAsync(ExitUrl, exitCaptureFolder, ct), ct);
    }

    /// <summary>
    /// Stream koparsa otomatik olarak yeniden baglanir
    /// </summary>
    private static async Task StreamLoopAsync(string url, string folder, CancellationToken ct)
    {
        bool ilkHata = true;
        string? calisanUrl = null;      // dogrulanmis adres (marka farkli olabilir)
        bool snapshotModu = false;      // true = tek kare (JPEG) adresi, periyodik cekilir

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Yapilandirilan adres calismiyorsa (orn. Axis adresi Hikvision kamerada 404),
                // bilinen alternatifler denenip CALISAN adres bulunur.
                if (calisanUrl == null)
                {
                    var bulunan = await CozumleAsync(url, ct);
                    if (bulunan == null)
                    {
                        if (ilkHata)
                        {
                            CameraDiag.Error($"Kamera adresi calismiyor ve bilinen alternatifler de yanit vermedi: {url}");
                            ilkHata = false;
                        }
                        try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    calisanUrl = bulunan.Value.url;
                    snapshotModu = bulunan.Value.snapshot;
                    ilkHata = true;
                }

                CameraDiag.Write($"Baglaniliyor ({(snapshotModu ? "tek-kare" : "mjpeg")}): {calisanUrl}");

                if (snapshotModu) await ReadSnapshotLoopAsync(calisanUrl, folder, ct);
                else await ReadMjpegStreamAsync(calisanUrl, folder, ct);

                CameraDiag.Write($"Baglanti koptu (tekrar denenecek): {calisanUrl}");
                ilkHata = true;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Eskiden hata TAMAMEN yutuluyordu; artik sebebi camera.log'a yazilir.
                if (ilkHata)
                {
                    CameraDiag.Error($"Kameraya baglanilamadi ({calisanUrl ?? url}) -> {ex.GetType().Name}: {ex.Message}");
                    ilkHata = false;   // ayni hatayi saniyede bir yazip log'u sisirmesin
                }
                calisanUrl = null;     // adres tekrar cozulsun (kamera degismis olabilir)
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Kimlik dogrulamali HttpClient (kamera kullanici/sifre appsettings'ten).</summary>
    private static HttpClient YeniClient(TimeSpan timeout)
    {
        var username = AppConfig.Configuration["Camera:Username"] ?? "admin";
        var password = AppConfig.Configuration["Camera:Password"] ?? "admin";
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Verilen adres calismiyorsa (404 vb.) ayni IP icin bilinen kamera adreslerini dener.
    /// Boylece Axis / Hikvision / Dahua farketmeksizin goruntu alinir.
    /// Donus: (calisan adres, tek-kare modu mu).
    /// </summary>
    private static async Task<(string url, bool snapshot)?> CozumleAsync(string configUrl, CancellationToken ct)
    {
        var adaylar = new List<string>();
        if (!string.IsNullOrWhiteSpace(configUrl)) adaylar.Add(configUrl);

        string host = "";
        try { host = new Uri(configUrl).Host; } catch { }

        if (!string.IsNullOrWhiteSpace(host))
        {
            // Hikvision (ekrandaki /doc/index.html arayuzu bu markadir)
            adaylar.Add($"http://{host}/ISAPI/Streaming/channels/102/httpPreview");  // MJPEG alt akis
            adaylar.Add($"http://{host}/ISAPI/Streaming/channels/101/picture");      // tek kare
            adaylar.Add($"http://{host}/Streaming/channels/1/picture");              // eski surum
            // Dahua
            adaylar.Add($"http://{host}/cgi-bin/mjpg/video.cgi?channel=1&subtype=1");
            adaylar.Add($"http://{host}/cgi-bin/snapshot.cgi?channel=1");
            // Axis
            adaylar.Add($"http://{host}/axis-cgi/mjpg/video.cgi?camera=1&fps=12&.mjpg");
            adaylar.Add($"http://{host}/axis-cgi/jpg/image.cgi");
        }

        foreach (var aday in adaylar.Distinct())
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                using var client = YeniClient(TimeSpan.FromSeconds(8));
                using var req = new HttpRequestMessage(HttpMethod.Get, aday);
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!res.IsSuccessStatusCode)
                {
                    CameraDiag.Write($"  denendi -> {(int)res.StatusCode} : {aday}");
                    continue;
                }

                var tip = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                bool snapshot = tip.Contains("image");                       // image/jpeg -> tek kare
                bool stream = tip.Contains("multipart") || tip.Contains("mjpeg");

                if (!snapshot && !stream)
                {
                    // Icerik tipi belirsizse (bazi kameralar text/html doner) kabul etme.
                    CameraDiag.Write($"  denendi -> beklenmeyen icerik ({tip}) : {aday}");
                    continue;
                }

                CameraDiag.Write($"CALISAN kamera adresi bulundu ({(snapshot ? "tek-kare" : "mjpeg")}): {aday}");
                return (aday, snapshot);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                CameraDiag.Write($"  denendi -> {ex.GetType().Name} : {aday}");
            }
        }
        return null;
    }

    /// <summary>
    /// TEK-KARE (snapshot) modu: kamera MJPEG akisi vermiyorsa, JPEG adresi periyodik cekilir.
    /// Hikvision /ISAPI/.../picture gibi adresler icin kullanilir.
    /// </summary>
    private static async Task ReadSnapshotLoopAsync(string url, string folder, CancellationToken ct)
    {
        using var client = YeniClient(TimeSpan.FromSeconds(10));
        while (!ct.IsCancellationRequested)
        {
            var bytes = await client.GetByteArrayAsync(url, ct);
            if (bytes.Length > 0)
                SaveFrame(bytes, folder);

            try { await Task.Delay(Math.Max(200, SaveIntervalMs), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task ReadMjpegStreamAsync(string url, string folder, CancellationToken ct)
    {
        var username = AppConfig.Configuration["Camera:Username"] ?? "admin";
        var password = AppConfig.Configuration["Camera:Password"] ?? "admin";

        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buf = new byte[256 * 1024];
        var acc = new MemoryStream();
        var lastSaveUtc = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buf, 0, buf.Length, ct);
            if (n <= 0) break;

            acc.Write(buf, 0, n);
            var data = acc.GetBuffer();
            int len = (int)acc.Length;

            // Buffer icinden JPEG frame'lerini ayikla
            while (true)
            {
                int start = IndexOf(data, len, 0, JpegStart);
                if (start < 0) break;

                int end = IndexOf(data, len, start + 2, JpegEnd);
                if (end < 0)
                {
                    // Eksik frame - JPEG baslangicindan sonrasini sakla
                    if (start > 0)
                    {
                        var remaining = len - start;
                        Buffer.BlockCopy(data, start, data, 0, remaining);
                        acc.SetLength(remaining);
                    }
                    break;
                }

                int frameLen = end + 2 - start;
                var frame = new byte[frameLen];
                Buffer.BlockCopy(data, start, frame, 0, frameLen);

                // Disk yazimini kisitla (her SaveIntervalMs ms'de bir)
                var now = DateTime.UtcNow;
                if ((now - lastSaveUtc).TotalMilliseconds >= SaveIntervalMs)
                {
                    lastSaveUtc = now;
                    _ = Task.Run(() => SaveFrame(frame, folder), ct);
                }

                // Bu frame'i buffer'dan kaldir, kalan byte'lari basa tasi
                int consumed = end + 2;
                int left = len - consumed;
                if (left > 0) Buffer.BlockCopy(data, consumed, data, 0, left);
                acc.SetLength(left);
                len = left;
            }

            // Buffer cok buyurse emniyet icin temizle (bozuk stream)
            if (acc.Length > 8 * 1024 * 1024)
                acc.SetLength(0);
        }
    }

    private static void SaveFrame(byte[] bytes, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var fileName = $"snap_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
            var path = Path.Combine(folder, fileName);
            File.WriteAllBytes(path, bytes);
            CleanupOldFiles(folder);
        }
        catch { }
    }

    /// <summary>
    /// Klasorde en fazla MaxFiles kare birakir.
    ///
    /// ESKIDEN her dosya icin FileInfo olusturulup LastWriteTimeUtc'ye gore siralaniyordu;
    /// bu, dosya basina bir stat cagrisi demek (asil pahali kisim). Dosya adi zaten
    /// "snap_yyyyMMdd_HHmmss_fff.jpg" formatinda KRONOLOJIK SIRALI oldugu icin duz metin
    /// siralamasi ayni sonucu verir ve stat'a hic gerek kalmaz.
    ///
    /// Her kare kaydinda cagriliyor; n~400'de maliyeti onemsiz.
    /// </summary>
    private static void CleanupOldFiles(string folder)
    {
        if (MaxFiles <= 0) return;
        try
        {
            var files = Directory.GetFiles(folder, "snap_*.jpg");
            if (files.Length <= MaxFiles) return;

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // eskiden yeniye

            // Bastaki (en eski) fazlaligi sil
            int silinecek = files.Length - MaxFiles;
            for (int i = 0; i < silinecek; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// ACILIS SUPURMESI - sahada birikmis devasa klasorleri kurtarir.
    ///
    /// MaxFiles=0 doneminde klasorlerde 100.000+ dosya birikmis olabilir. Bu supurme
    /// olmadan ilk CleanupOldFiles cagrisi on binlerce dosyayi TEK SEFERDE silmeye
    /// calisir; SaveFrame icinden cagrildigi icin de kamera dongusu dakikalarca tikanir
    /// ve ilk kare cok gec gelir.
    ///
    /// Bu yuzden temizlik kamera dongusu BASLAMADAN, ayri bir Task'ta yapilir.
    /// Sessizdir: basarisiz olursa uygulama normal calismaya devam eder.
    /// </summary>
    public static Task TemizleAsync(params string[] folders)
    {
        return Task.Run(() =>
        {
            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    if (!Directory.Exists(folder)) continue;

                    var files = Directory.GetFiles(folder, "snap_*.jpg");
                    if (files.Length <= MaxFiles) continue;

                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    int silinecek = files.Length - MaxFiles;
                    int basarili = 0;
                    for (int i = 0; i < silinecek; i++)
                    {
                        try { File.Delete(files[i]); basarili++; } catch { }
                    }
                    CameraDiag.Write($"Acilis temizligi: {folder} -> {files.Length} dosyadan " +
                                   $"{basarili} tanesi silindi, {files.Length - basarili} kaldi.");
                }
                catch (Exception ex)
                {
                    CameraDiag.Write($"Acilis temizligi hatasi ({folder}): {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Byte dizisinde pattern arar
    /// </summary>
    private static int IndexOf(byte[] data, int length, int startIndex, byte[] pattern)
    {
        for (int i = startIndex; i <= length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
