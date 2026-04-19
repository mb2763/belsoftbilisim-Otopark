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
}
