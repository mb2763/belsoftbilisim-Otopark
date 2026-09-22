using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Core.Offline;

/// <summary>
/// Sunucudaki /Saha/* ve /Offline/* uçlarını çağıran HTTP istemcisi (20.09.2026).
/// Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 5, 7. KISA ZAMAN AŞIMI kullanır (bugünkü
/// masaüstü/kiosk 30-60 sn bekliyordu; bir aracın bariyerde 30 sn beklemesi
/// kabul edilemez, bkz. plan Bölüm 1.1/5).
/// </summary>
public sealed class SahaOfflineClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly CihazKimligi _kimlik;

    public SahaOfflineClient(string baseUrl, CihazKimligi kimlik)
    {
        _kimlik = kimlik;
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(8) };
    }

    private void ImzaEkle(HttpRequestMessage req)
    {
        var zaman = DateTime.UtcNow.ToString("O");
        req.Headers.Add("X-Saha-Zaman", zaman);
        req.Headers.Add("X-Saha-Imza", _kimlik.Imzala(zaman));
    }

    public async Task<SahaKaydolYanit?> KaydolAsync(SahaKaydolIstek istek, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("Saha/Kaydol", istek, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SahaKaydolYanit>(JsonOpts, ct);
        }
        catch { return null; }
    }

    public async Task<SahaNabizYanit?> NabizAsync(SahaNabizIstek istek, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "Saha/Nabiz") { Content = JsonContent.Create(istek) };
            ImzaEkle(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SahaNabizYanit>(JsonOpts, ct);
        }
        catch { return null; }
    }

    /// <summary>(deger, degistiMi) - 204 dönerse (sürüm aynı) deger null, degistiMi=false.</summary>
    public async Task<(OfflineAnlikDto? deger, bool basarili)> AnlikAsync(OfflineAnlikIstek istek, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("Offline/Anlik", istek, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return (null, true);
            if (!resp.IsSuccessStatusCode) return (null, false);
            var dto = await resp.Content.ReadFromJsonAsync<OfflineAnlikDto>(JsonOpts, ct);
            return (dto, true);
        }
        catch { return (null, false); }
    }

    public async Task<OfflineSenkronYanit?> SenkronAsync(OfflineSenkronIstek istek, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "Offline/Senkron") { Content = JsonContent.Create(istek) };
            ImzaEkle(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<OfflineSenkronYanit>(JsonOpts, ct);
        }
        catch { return null; }
    }

    /// <summary>Son başarılı nabızda sunucunun bildirdiği OfflineIzinli (K10). Henüz yoksa null.</summary>
    public bool? SonOfflineIzinli { get; private set; }

    /// <summary>
    /// Sunucu ERİŞİLEBİLİR mi? Sunucudan HERHANGİ bir HTTP yanıtı gelirse (401/404/500 dahil)
    /// erişilebilirdir. Yalnızca AĞ hatası (zaman aşımı, bağlantı reddi, DNS) ya da servis/ağ
    /// geçidi kapalı (502/503/504) "erişilemez" sayılır. Aksi halde cihaz kayıtlı değilken ya da
    /// saat kaymasından imza reddedilince masaüstü sunucu ayaktayken çevrimdışına geçerdi.
    /// </summary>
    public async Task<bool> ErisilebilirMiAsync(SahaNabizIstek nabizIstek, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "Saha/Nabiz") { Content = JsonContent.Create(nabizIstek) };
            if (_kimlik.Kayitli) ImzaEkle(req);
            using var resp = await _http.SendAsync(req, ct);

            var kod = (int)resp.StatusCode;
            if (kod == 502 || kod == 503 || kod == 504) return false;

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var yanit = await resp.Content.ReadFromJsonAsync<SahaNabizYanit>(JsonOpts, ct);
                    if (yanit?.Basarili == true) SonOfflineIzinli = yanit.OfflineIzinli;
                }
                catch { /* gövde okunamasa da sunucu erişilebilir */ }
            }
            return true;
        }
        catch { return false; }
    }
}
