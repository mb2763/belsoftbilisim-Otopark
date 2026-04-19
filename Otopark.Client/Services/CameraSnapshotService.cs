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
    private static string EntryUrl => AppConfig.Configuration["CameraSnapshot:EntrySnapshotUrl"] ?? "";
    private static string ExitUrl => AppConfig.Configuration["CameraSnapshot:ExitSnapshotUrl"] ?? "";
    private static int SaveIntervalMs => int.TryParse(AppConfig.Configuration["CameraSnapshot:IntervalMs"], out var v) ? v : 500;

    // Klasorde tutulacak maksimum dosya sayisi (eskiler silinir)
    private const int MaxFiles = 15;

    // JPEG marker'lari
    private static readonly byte[] JpegStart = { 0xFF, 0xD8 };
    private static readonly byte[] JpegEnd = { 0xFF, 0xD9 };

    public static void Start(string entryCaptureFolder, string exitCaptureFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EntryUrl) && string.IsNullOrWhiteSpace(ExitUrl))
            return;

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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadMjpegStreamAsync(url, folder, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* baglanti hatasi - tekrar dene */ }

            try { await Task.Delay(2000, ct); }
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

    private static void CleanupOldFiles(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "snap_*.jpg")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var old in files.Skip(MaxFiles))
            {
                try { old.Delete(); } catch { }
            }
        }
        catch { }
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
