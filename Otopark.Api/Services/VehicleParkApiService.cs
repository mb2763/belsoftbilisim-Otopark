using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

public partial class VehicleParkApiService
{
    private readonly HttpClient _http;

    public VehicleParkApiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VehicleParkEntryResponse?> AddEntryAsync(VehicleParkEntryRequest req)
    {
        var url = "VehiclePark/AddVehicleParkEntry";

        using var response = await _http.PostAsJsonAsync(url, req, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // 400 vb. hatalarda API'nin dondurdugu hata mesajini parse etmeye calis
            try
            {
                var errorResult = JsonSerializer.Deserialize<VehicleParkEntryResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (errorResult != null) return errorResult;
            }
            catch { }

            // Parse edilemezse genel hata don
            return new VehicleParkEntryResponse
            {
                Errors = new List<ErrorMessageObject>
                {
                    new() { Message = $"{(int)response.StatusCode} - {json}" }
                }
            };
        }

        return JsonSerializer.Deserialize<VehicleParkEntryResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task<string?> GetEntryImageBase64Async(long entryId)
    {
        using var response = await _http.PostAsJsonAsync("GetImage", entryId);
        if (!response.IsSuccessStatusCode) return null;
        var base64 = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(base64) || base64 == "null") return null;
        return base64.Trim('"');
    }

    public async Task<AddVehicleCreditResponse?> AddVehicleCreditAsync(AddVehicleCreditRequest req)
    {
        using var response = await _http.PostAsJsonAsync("VehicleParkCredit/AddVehicleCredit", req,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AddVehicleCreditResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<double> GetParkPriceAsync(long entryId)
    {
        var url = "VehiclePark/GetParkPrice";

        using var response = await _http.PostAsJsonAsync(url, new { entryId = entryId });

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return 0;

        if (double.TryParse(json, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var price))
            return price;
        return 0;
    }

    public async Task<VehicleParkExitResponse?> AddExitAsync(VehicleParkExitRequest req)
    {
        var url = "VehiclePark/AddVehicleExit";

        using var response = await _http.PostAsJsonAsync(url, req, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var json = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var errorResult = JsonSerializer.Deserialize<VehicleParkExitResponse>(json, options);
                if (errorResult != null) return errorResult;
            }
            catch { }

            return new VehicleParkExitResponse
            {
                Errors = new List<ErrorMessageObject>
                {
                    new() { Message = $"{(int)response.StatusCode} - {json}" }
                }
            };
        }

        return JsonSerializer.Deserialize<VehicleParkExitResponse>(json, options);
    }

    /// <summary>
    /// Mevcut bir arac giris kaydinin plakasini gunceller.
    /// Yeni plaka backend'de kayitli olmalidir; degilse Errors dolu doner.
    /// </summary>
    public async Task<UpdatePlateResponse?> UpdateEntryPlateAsync(long entryId, string newPlate, long companyId, long currentUserId)
    {
        var url = "VehiclePark/UpdateVehicleParkEntryPlate";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            entryId = entryId,
            newPlate = newPlate,
            companyId = companyId,
            currentUserId = currentUserId
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var json = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var errorResult = JsonSerializer.Deserialize<UpdatePlateResponse>(json, options);
                if (errorResult != null) return errorResult;
            }
            catch { }

            return new UpdatePlateResponse
            {
                Errors = new List<ErrorMessageObject>
                {
                    new() { Message = $"{(int)response.StatusCode} - {json}" }
                }
            };
        }

        return JsonSerializer.Deserialize<UpdatePlateResponse>(json, options);
    }
}

public class UpdatePlateResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public object? Result { get; set; }
}

// ===== EK API'LER =====

public partial class VehicleParkApiService
{
    /// <summary>
    /// Bir arac giris kaydini siler (iptal). Backend yumusak silme yapar (IsDelete=true).
    /// </summary>
    public async Task<DeleteEntryResponse?> DeleteEntryAsync(long entryId, long companyId, long currentUserId)
    {
        var url = $"VehiclePark/DeleteVehicleParkEntry?id={entryId}&companyId={companyId}&currentUserId={currentUserId}";
        using var response = await _http.PostAsync(url, null);
        var json = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var err = JsonSerializer.Deserialize<DeleteEntryResponse>(json, options);
                if (err != null) return err;
            }
            catch { }

            return new DeleteEntryResponse
            {
                Errors = new List<ErrorMessageObject>
                {
                    new() { Message = $"{(int)response.StatusCode} - {json}" }
                }
            };
        }
        return JsonSerializer.Deserialize<DeleteEntryResponse>(json, options);
    }

    /// <summary>
    /// Aracin tum bolgelerdeki kredi kayitlarini doner (borc + odeme detaylari).
    /// </summary>
    public async Task<List<VehicleCreditDto>> GetVehicleCreditsAsync(long vehicleDefinitionId)
    {
        var url = $"VehicleParkCredit/GetVehicleCredits?vehicleDefinitionId={vehicleDefinitionId}";
        using var response = await _http.PostAsync(url, null);
        if (!response.IsSuccessStatusCode) return new();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<VehicleCreditDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    /// <summary>
    /// Plakanin AKTIF aboneligi var mi kontrol eder.
    /// Kapali Otopark Desktop arac girisinde A (yesil) / N (sari) badge icin.
    /// </summary>
    public async Task<SubscriptionCheckResponse> CheckSubscriptionAsync(string plate, long companyId)
    {
        try
        {
            var url = $"VehiclePark/CheckSubscription?plate={Uri.EscapeDataString(plate)}&companyId={companyId}";
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new SubscriptionCheckResponse { IsSubscriber = false };
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<SubscriptionCheckResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result ?? new SubscriptionCheckResponse { IsSubscriber = false };
        }
        catch
        {
            return new SubscriptionCheckResponse { IsSubscriber = false };
        }
    }

    // ===== YIKAMA (Wash) =====

    /// <summary>Kapali otoparktaki son N icerideki arac (yikama listesi).</summary>
    public async Task<List<WashEntryDto>> GetWashRecentEntriesAsync(long companyId, int take = 15)
    {
        try
        {
            var url = $"Wash/GetRecentEntries?companyId={companyId}&take={take}";
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new();
            var json = await response.Content.ReadAsStringAsync();
            var env = JsonSerializer.Deserialize<WashEntriesEnvelope>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return env?.Data ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Yikama fisi bas. Basariliysa Success=true ve tutar/sure bilgileri doner.</summary>
    public async Task<WashReceiptResult> PrintWashReceiptAsync(long companyId, long currentUserId, long vehicleEntryId, int freeMinutes)
    {
        try
        {
            var body = new
            {
                CompanyId = companyId,
                CurrentUserId = currentUserId,
                VehicleEntryId = vehicleEntryId,
                FreeMinutes = freeMinutes
            };
            using var response = await _http.PostAsJsonAsync("Wash/PrintReceipt", body);
            var json = await response.Content.ReadAsStringAsync();
            var res = JsonSerializer.Deserialize<WashReceiptResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (res == null) return new WashReceiptResult { Code = 0, Message = "Bos yanit" };
            return res;
        }
        catch (Exception ex)
        {
            return new WashReceiptResult { Code = 0, Message = ex.Message };
        }
    }
}

public class SubscriptionCheckResponse
{
    public bool IsSubscriber { get; set; }
    public string? SubscriptionName { get; set; }
    public DateTime? FinishDate { get; set; }
}

public class WashEntriesEnvelope
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public List<WashEntryDto>? Data { get; set; }
}

public class WashEntryDto
{
    public long EntryId { get; set; }
    public string? Plate { get; set; }
    public DateTime EntryTime { get; set; }
    public int MinutesIn { get; set; }
    public long ZoneId { get; set; }
    public bool AlreadyWashed { get; set; }
}

public class WashReceiptResult
{
    public int Code { get; set; }                 // 1=basarili, 0=hata
    public string? Message { get; set; }
    public long ReceiptId { get; set; }
    public string? Plate { get; set; }
    public DateTime? EntryTime { get; set; }
    public DateTime? ReceiptTime { get; set; }
    public int MinutesIn { get; set; }
    public int FreeMinutes { get; set; }
    public decimal ChargedAmount { get; set; }
    public bool IsFree { get; set; }
    public bool Success => Code == 1;
}

public class DeleteEntryResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public object? Result { get; set; }
}

public class VehicleCreditDto
{
    public long Id { get; set; }
    public long VehicleDefinitionId { get; set; }
    public long? ZoneId { get; set; }
    public string? ZoneName { get; set; }
    public long? VehicleExitId { get; set; }
    public decimal DebtAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal Balance { get; set; }
    public string? Description { get; set; }
    public DateTime CreateDate { get; set; }
}
