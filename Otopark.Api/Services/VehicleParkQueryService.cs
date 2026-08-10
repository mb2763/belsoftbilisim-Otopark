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

    /// <summary>
    /// IPTAL EDILMIS (soft-delete) girisler — bolge + tarih araligi. "Iptaller" sekmesi icin.
    /// Normal liste sorgulari silinmis kayitlari dondurmez.
    /// </summary>
    public async Task<List<VewVehicleParkCurrentDto>> GetCancelledByZoneAndDateRangeAsync(
        long companyId, long entryZoneId, DateTime startDate, DateTime endDate)
    {
        var url = "VehiclePark/GetCancelledVehicleParkByZoneAndDateRange";

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

    /// <summary>
    /// SU AN ICERIDE olan arac sayisi (bolge bazinda) — TARIHTEN BAGIMSIZ.
    ///
    /// NEDEN AYRI: "Bos/Dolu" sayaci onceden GetByZoneTodayAsync listesinden hesaplaniyordu;
    /// o liste yalnizca BUGUNUN kayitlarini dondurdugu icin DUN girip hala iceride olan
    /// araclar doluluga YANSIMIYORDU (sabahlari otopark bos gorunuyordu).
    ///
    /// GetVehicleCurrentPark firma genelini dondurur (bolge parametresi almaz), bu yuzden
    /// entryZoneId ile burada suzulur. Cikis yapmis kayitlar (exitTimestamp dolu) haric tutulur.
    ///
    /// Donus: arac sayisi; sorgu basarisiz olursa -1 (cagiran taraf yerel hesabi korur).
    /// </summary>
    public async Task<int> GetCurrentParkedCountByZoneAsync(long companyId, long currentUserId, long zoneId)
    {
        try
        {
            var url = "VehiclePark/GetVehicleCurrentPark";

            using var response = await _http.PostAsJsonAsync(url, new
            {
                companyId = companyId,
                currentUserId = currentUserId
            });

            if (!response.IsSuccessStatusCode) return -1;

            var json = await response.Content.ReadAsStringAsync();
            var list = JsonSerializer.Deserialize<List<VewVehicleParkCurrentDto>>(json, JsonOpts);
            if (list == null) return -1;

            return list.Count(x => x.EntryZoneId == zoneId && x.ExitTimestamp == null);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// KARA LISTE: bolgede odenmemis (eski) borcu olan TUM araclar. Tarih sinirlamasi yoktur.
    /// </summary>
    public async Task<List<ZoneDebtorDto>> GetZoneDebtorsAsync(long companyId, long zoneId)
    {
        var url = "VehiclePark/GetZoneDebtorVehicles";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            companyId = companyId,
            entryZoneId = zoneId
        });

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return new();

        return JsonSerializer.Deserialize<List<ZoneDebtorDto>>(json, JsonOpts) ?? new();
    }
}

/// <summary>Kara liste satiri: bolgede odenmemis borcu olan arac.</summary>
public class ZoneDebtorDto
{
    public long VehicleDefinitionId { get; set; }
    public string? Plate { get; set; }
    public decimal DebtAmount { get; set; }
    public int DebtCount { get; set; }
    public DateTime LastDebtDate { get; set; }
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
