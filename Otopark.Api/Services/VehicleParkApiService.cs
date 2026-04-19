using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

public sealed class VehicleParkApiService
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
