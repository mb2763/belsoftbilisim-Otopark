using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

public sealed class VehicleParkQueryService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public VehicleParkQueryService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Bugunun park verilerini bolgeye gore getirir (gun/mesai filtresi)
    /// </summary>
    public async Task<List<VewVehicleParkCurrentDto>> GetByZoneTodayAsync(long companyId, long entryZoneId)
    {
        var url = "VehiclePark/GetVehicleParkByZoneToday";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            companyId = companyId,
            entryZoneId = entryZoneId
        });

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return new();

        return JsonSerializer.Deserialize<List<VewVehicleParkCurrentDto>>(json, JsonOpts) ?? new();
    }

    /// <summary>
    /// Tarih araligindaki park verilerini bolgeye gore getirir (hafta/ay filtresi)
    /// </summary>
    public async Task<List<VewVehicleParkCurrentDto>> GetByZoneAndDateRangeAsync(
        long companyId, long entryZoneId, DateTime startDate, DateTime endDate)
    {
        var url = "VehiclePark/GetVehicleParkByZoneAndDateRange";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            companyId = companyId,
            entryZoneId = entryZoneId,
            startDate = startDate,
            endDate = endDate
        });

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return new();

        return JsonSerializer.Deserialize<List<VewVehicleParkCurrentDto>>(json, JsonOpts) ?? new();
    }
}

public class VewVehicleParkCurrentDto
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public long VehicleDefinitionId { get; set; }
    public DateTime EntryTimestamp { get; set; }
    public long EntryUserId { get; set; }
    public long EntryZoneId { get; set; }
    public long EntryId { get; set; }
    public long? ExitId { get; set; }
    public double? CalculatedFee { get; set; }
    public DateTime? ExitTimestamp { get; set; }
    public long? ExitUserId { get; set; }
    public long? ExitZoneId { get; set; }
    public double? PayableFee { get; set; }
    public double Balance { get; set; }
    public double Credit { get; set; }
    public string? Plate { get; set; }
    public double? AmountCash { get; set; }
    public DateTime? PaymentTime { get; set; }
    public string? EntryPhotoPath { get; set; }
    public long VehicleTypeId { get; set; }
    public double? CurrentDebitAmount { get; set; }
}
