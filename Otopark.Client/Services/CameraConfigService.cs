using Otopark.Api.Services;
using Otopark.Core.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Otopark.Client.Services;

/// <summary>
/// Kamera (Giris/Cikis) goruntu URL'lerini cozer:
///   1) ONCE web'den DB'ye tanimlanan kamera IP'leri (ZONE_CAMERA, API: Zone/GetZoneCameras),
///   2) bulunamaz/erisilemezse appsettings.json'daki CameraSnapshot degerlerine (FALLBACK) duser.
/// Boylece "DB'den de alacak ama appsettings de kalsin" istegi karsilanir.
/// </summary>
public static class CameraConfigService
{
    public static string EntryUrl { get; private set; } = "";
    public static string ExitUrl { get; private set; } = "";

    private static string ApiBaseUrl =>
        AppConfig.Configuration["Api:BaseUrl"] ?? "http://web.belsoft.com.tr:221/";

    /// <summary>
    /// DB'den (API) kamera IP'lerini yukler; basarisiz/bos donerse appsettings degerleri kalir.
    /// Kamera baslatilmadan ONCE cagrilmali.
    /// </summary>
    public static async Task LoadAsync(long companyId, long zoneId)
    {
        // Fallback (config dosyasi) — "o da kalsin"
        EntryUrl = AppConfig.Configuration["CameraSnapshot:EntrySnapshotUrl"] ?? "";
        ExitUrl = AppConfig.Configuration["CameraSnapshot:ExitSnapshotUrl"] ?? "";

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            var zoneApi = new ZoneApiService(http);
            var cams = await zoneApi.GetZoneCamerasAsync(companyId, zoneId);

            var entry = cams.FirstOrDefault(c => c.CameraType == 1 && !string.IsNullOrWhiteSpace(c.IpAddress));
            var exit = cams.FirstOrDefault(c => c.CameraType == 2 && !string.IsNullOrWhiteSpace(c.IpAddress));

            if (entry != null) EntryUrl = BuildUrl(entry.IpAddress);
            if (exit != null) ExitUrl = BuildUrl(exit.IpAddress);
        }
        catch
        {
            // API'ye ulasilamazsa appsettings degerleri kalir.
        }
    }

    /// <summary>
    /// DB'de tam URL (http/rtsp) girilmisse oldugu gibi kullanir; sadece IP girilmisse
    /// Axis MJPEG URL'i kurar (mevcut appsettings formatiyla ayni).
    /// </summary>
    private static string BuildUrl(string ipOrUrl)
    {
        var v = (ipOrUrl ?? "").Trim();
        if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            return v;
        return $"http://{v}/axis-cgi/mjpg/video.cgi?camera=1&fps=12&.mjpg";
    }
}
