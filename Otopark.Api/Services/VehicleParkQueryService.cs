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
        // ONCE ORTAK UC (27.08.2026): web panosu ile AYNI hesabi dondurur.
        // Basarisiz olursa (eski surum sunucu, ag hatasi) asagidaki ESKI yola duser;
        // boylece sunucu guncellenmeden once de sayac calismaya devam eder.
        var ortak = await GetParkOccupancyAsync(companyId, zoneId);
        if (ortak != null) return ortak.VehicleParkingCount;

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
    /// PLAKAYA GORE ACIK GIRIS - TARIHTEN BAGIMSIZ (28.08.2026).
    ///
    /// NEDEN GEREKLI: cikis akisindaki iki arama da YALNIZCA BUGUNU tariyordu
    /// (_allVehicles ve GetByZoneTodayAsync -> sunucuda GetVehicleParkByZoneToday,
    /// "EntryTimestamp >= today && < tomorrow"). Dun girip bugun cikan ya da manuel
    /// "Iceri Al" ile alinmis bir arac bulunamiyor, akis hayalet giris uretmeye
    /// calisiyor ve bariyer acilmadan duruyordu.
    ///
    /// Bu uc (VehiclePark/GetVehicleCurrentParkByPlate -> GetCurrentVehicleParkByParkPlate)
    /// TARIH FILTRESI ICERMEZ; yalnizca "cikisi yapilmamis" (ExitId == null) kaydi arar.
    /// Kiosk da ayni ucu kullaniyor.
    ///
    /// DIKKAT - BOLGE FILTRESI YOK: uc firma genelini dondurur. Cagiran taraf
    /// KENDI bolgesine gore suzmelidir; aksi halde baska bir otoparkin acik girisi
    /// uzerine cikis yazilir.
    ///
    /// Hata durumunda BOS liste doner (cagiran taraf eski davranisina devam eder).
    /// </summary>
    public async Task<List<VewVehicleParkCurrentDto>> GetOpenParkByPlateAsync(
        long companyId, long currentUserId, string plate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(plate)) return new();

            using var response = await _http.PostAsJsonAsync(
                "VehiclePark/GetVehicleCurrentParkByPlate", new
                {
                    companyId = companyId,
                    currentUserId = currentUserId,
                    plate = plate
                });

            if (!response.IsSuccessStatusCode) return new();

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return new();

            return JsonSerializer.Deserialize<List<VewVehicleParkCurrentDto>>(json, JsonOpts) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// PARK DOLULUGU - web panosuyla ORTAK hesap (27.08.2026).
    ///
    /// Sunucudaki ZoneManager.GetParkOccupancy'yi cagirir; kapasite, icerideki arac,
    /// bos yer ve doluluk orani TEK kaynaktan gelir. Uc yoksa/ulasilamazsa null doner
    /// ve cagiran taraf eski yontemine duser.
    ///
    /// zoneId = 0 -> firma geneli.
    /// </summary>
    /// <summary>
    /// UZAKTAN BARIYER KOMUTLARI (01.09.2026 - madde 3).
    ///
    /// Web'den birakilan "bariyer ac" komutlarini alir. Bariyer, kameranin
    /// yerel agdaki IO cikisindan tetikleniyor; web sunucusu o aga erisemedigi
    /// icin komutu BU ISTEMCI uygular.
    ///
    /// Alinan komut sunucuda kuyruktan DUSER, ikinci kez gelmez.
    /// Hata halinde BOS liste doner: bariyer acilmaz, hicbir yan etki olmaz.
    /// </summary>
    public async Task<List<BarrierCommandDto>> GetPendingBarrierCommandsAsync(long companyId, long zoneId)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"VehiclePark/PendingBarrierCommands?companyId={companyId}&zoneId={zoneId}");

            if (!response.IsSuccessStatusCode) return new();

            var zarf = await response.Content.ReadFromJsonAsync<BarrierCommandEnvelope>(JsonOpts);
            return zarf?.Data ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<ParkOccupancyDto?> GetParkOccupancyAsync(long companyId, long zoneId)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("VehiclePark/GetParkOccupancy", new
            {
                companyId = companyId,
                entryZoneId = zoneId
            });

            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<ParkOccupancyDto>(JsonOpts);
        }
        catch
        {
            return null;
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
/// <summary>
/// VehiclePark/GetParkOccupancy yaniti. Sunucudaki ParkCapacityModel ile birebir.
/// </summary>
/// <summary>Uzaktan bariyer komutu (madde 3 - web'den birakilir, exe uygular).</summary>
public class BarrierCommandDto
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public long ZoneId { get; set; }
    /// <summary>"giris" | "cikis"</summary>
    public string Gate { get; set; } = "cikis";
    public string Plate { get; set; } = "";
    public long RequestUserId { get; set; }
    public DateTime CreateDate { get; set; }
}

/// <summary>PendingBarrierCommands zarfi.</summary>
public class BarrierCommandEnvelope
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public List<BarrierCommandDto>? Data { get; set; }
}

public class ParkOccupancyDto
{
    public int TotalParkCapacity { get; set; }
    public int VehicleParkingCount { get; set; }
    public int EmptyCount { get; set; }
    public decimal OccupancyRate { get; set; }
}

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
