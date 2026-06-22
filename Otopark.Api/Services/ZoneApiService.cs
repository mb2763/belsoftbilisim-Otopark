using System.Net.Http.Json;

namespace Otopark.Api.Services;

public sealed class ZoneDto
{
    public long Id { get; set; }
    public string ZoneName { get; set; } = "";
    public long ZoneClassId { get; set; }
    public long CompanyId { get; set; }
    public bool IsActive { get; set; }
    public int Capacity { get; set; }
}

public sealed class ZoneCameraDto
{
    public long Id { get; set; }
    public long ZoneId { get; set; }
    public int CameraType { get; set; }   // 1 = Giris, 2 = Cikis
    public string IpAddress { get; set; } = "";
    public string CameraName { get; set; } = "";
}

public sealed class ZoneApiService
{
    private readonly HttpClient _http;

    public ZoneApiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<ZoneDto>> GetZonesAsync(long companyId, long zoneClassId)
    {
        var url = "Zone/GetZones";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            companyId = companyId,
            zoneClassId = zoneClassId,
            isActive = true
        });

        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<List<ZoneDto>>();
        return data ?? new List<ZoneDto>();
    }

    /// <summary>
    /// Kullanicinin (operatorun) erisim YETKISI olan bolgeleri doner.
    /// Bos liste = yetki atanmamis (cagiran taraf kisitlama uygulamaz).
    /// </summary>
    public async Task<List<ZoneDto>> GetAuthorizedZonesAsync(long userId, long companyId, long zoneClassId)
    {
        var url = $"Zone/GetAuthorizedZonesByUser?userId={userId}&companyId={companyId}&zoneClassId={zoneClassId}";

        using var response = await _http.PostAsync(url, null);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<List<ZoneDto>>();
        return data ?? new List<ZoneDto>();
    }

    /// <summary>
    /// Bolge (otopark) kamera IP tanimlari (web'den DB'ye girilen). zoneId=0 -> tum bolgeler.
    /// CameraType: 1=Giris, 2=Cikis. Bos liste -> cagiran taraf appsettings'e duser.
    /// </summary>
    public async Task<List<ZoneCameraDto>> GetZoneCamerasAsync(long companyId, long zoneId)
    {
        var url = $"Zone/GetZoneCameras?companyId={companyId}&zoneId={zoneId}";

        using var response = await _http.PostAsync(url, null);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<List<ZoneCameraDto>>();
        return data ?? new List<ZoneCameraDto>();
    }
}
