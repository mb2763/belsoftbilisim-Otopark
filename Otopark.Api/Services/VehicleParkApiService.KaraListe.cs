using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

/// <summary>
/// KAPALI OTOPARK KARA LISTESI uclari (11.10.2026) — sunucu: KapaliKaraListeController.
/// Sunucu eski surumse (uc yoksa, 404) ya da tablo henuz olusturulmadiysa (500) cagri NULL
/// doner; masaustu bu durumda kara listeyi devre disi sayar (eski davranis).
/// </summary>
public partial class VehicleParkApiService
{
    private static readonly JsonSerializerOptions KaraListeJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Firma genelindeki AKTIF kara liste (guncel borcla). Uc yoksa / hata varsa null.</summary>
    public async Task<List<KaraListeSatiriDto>?> KaraListeGetirAsync(long companyId)
    {
        try
        {
            using var resp = await _http.PostAsync($"KapaliKaraListe/Liste?companyId={companyId}&sadeceAktif=true", null);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<KaraListeSatiriDto>>(KaraListeJson);
        }
        catch { return null; }
    }

    /// <summary>Giris oncesi kontrol. Uc yoksa / ag hatasi varsa null (cagiran onbellege bakar).</summary>
    public async Task<KaraListeGirisDto?> KaraListeGirisKontrolAsync(long companyId, string plaka)
    {
        try
        {
            using var cts = new CancellationTokenSource(System.TimeSpan.FromSeconds(4));
            using var resp = await _http.PostAsync(
                $"KapaliKaraListe/GirisKontrol?companyId={companyId}&plaka={Uri.EscapeDataString(plaka ?? "")}", null, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<KaraListeGirisDto>(KaraListeJson, cts.Token);
        }
        catch { return null; }
    }

    /// <summary>Kara listeye ekler (acik girisi borclu cikisla kapatir). Uc yoksa null.</summary>
    public async Task<KaraListeSonucDto?> KaraListeEkleAsync(KaraListeEkleRequest req)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("KapaliKaraListe/Ekle", req,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<KaraListeSonucDto>(KaraListeJson);
        }
        catch { return null; }
    }
}

public class KaraListeEkleRequest
{
    public long CompanyId { get; set; }
    public long CurrentUserId { get; set; }
    public long VehicleDefinitionId { get; set; }
    public string? Plaka { get; set; }
    public long? VehicleEntryId { get; set; }
    public long? ZoneId { get; set; }
    public DateTime? SupheZamani { get; set; }
    public string? Neden { get; set; }
    public string? Kaynak { get; set; }
}

public class KaraListeSonucDto
{
    public bool Basarili { get; set; }
    public bool ZatenVar { get; set; }
    public long Id { get; set; }
    public decimal Borc { get; set; }
    public string? Mesaj { get; set; }
    public string? Uyari { get; set; }
}

public class KaraListeGirisDto
{
    public bool Engelli { get; set; }
    public bool OtomatikKapandi { get; set; }
    public long Id { get; set; }
    public string? Plaka { get; set; }
    public decimal Borc { get; set; }
    public string? Neden { get; set; }
    public DateTime? EklenmeTarihi { get; set; }
    public string? BolgeAdi { get; set; }
}

public class KaraListeSatiriDto
{
    public long Id { get; set; }
    public long VehicleDefinitionId { get; set; }
    public string? Plaka { get; set; }
    public long? BolgeId { get; set; }
    public string? BolgeAdi { get; set; }
    public long? VehicleEntryId { get; set; }
    public string? Neden { get; set; }
    public string? Kaynak { get; set; }
    public DateTime? SupheZamani { get; set; }
    public decimal EklenmeBorcu { get; set; }
    public decimal GuncelBorc { get; set; }
    public bool Aktif { get; set; }
    public DateTime EklenmeTarihi { get; set; }
    public string? Ekleyen { get; set; }
}
