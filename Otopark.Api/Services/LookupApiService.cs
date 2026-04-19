using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

/// <summary>
/// Musteri Firma, Arac Turu, Tarife gibi lookup verilerini ceken servis
/// </summary>
public sealed class LookupApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public LookupApiService(HttpClient http) => _http = http;

    public async Task<List<CustomerCompanyDto>> GetCustomerCompaniesAsync(long companyId)
    {
        using var resp = await _http.PostAsJsonAsync("CustomerCompany/GetCompanyListVew", new
        {
            companyId = companyId,
            isActive = true
        });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new();
        return JsonSerializer.Deserialize<List<CustomerCompanyDto>>(json, Opts) ?? new();
    }

    public async Task<List<VehicleTypeDto>> GetVehicleTypesAsync(long companyId)
    {
        using var resp = await _http.PostAsJsonAsync("VehicleType/GetVehicleTypes", new
        {
            companyId = companyId,
            isActive = true
        });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new();
        var result = JsonSerializer.Deserialize<VehicleTypeResultDto>(json, Opts);
        return result?.ResultList ?? new();
    }

    public async Task<List<TariffDto>> GetTariffsAsync(long companyId)
    {
        using var resp = await _http.PostAsJsonAsync("Tariff/GetTariff", new
        {
            companyId = companyId,
            isActive = true
        });
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new();
        var result = JsonSerializer.Deserialize<TariffResultDto>(json, Opts);
        return result?.ResultList ?? new();
    }

    public async Task<AddVehicleResponse?> AddVehicleAsync(AddVehicleRequest req)
    {
        using var resp = await _http.PostAsJsonAsync("VehicleDefinition/AddVehicle", req,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AddVehicleResponse>(json, Opts);
    }
}

// DTO'lar

public class CustomerCompanyDto
{
    public long Id { get; set; }
    public string? Title { get; set; }
    public string? TitleStr { get; set; }
    public bool IsActive { get; set; }
}

public class VehicleTypeDto
{
    public long Id { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleTypeCode { get; set; }
    public bool? IsActive { get; set; }
}

public class VehicleTypeResultDto
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public VehicleTypeDto? Result { get; set; }
    public List<VehicleTypeDto>? ResultList { get; set; }
}

public class TariffDto
{
    public long Id { get; set; }
    public string? TariffName { get; set; }
    public bool? IsActive { get; set; }
}

public class TariffResultDto
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public TariffDto? Result { get; set; }
    public List<TariffDto>? ResultList { get; set; }
}

public class AddVehicleRequest
{
    public long CurrentUserId { get; set; }
    public string? Id { get; set; }
    public string Plate { get; set; } = "";
    public long CompanyId { get; set; }
    public long? CustomerCompanyId { get; set; }
    public long? CustomerPersonId { get; set; }
    public long VehicleTypeId { get; set; }
    public long TariffId { get; set; }
    public bool WarningCheck { get; set; }
    public string? WarningNote { get; set; }
}

public class AddVehicleResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public string? Status { get; set; }
    public AddVehicleResultDto? Result { get; set; }
}

public class AddVehicleResultDto
{
    public long Id { get; set; }
    public string? Plate { get; set; }
    public long CompanyId { get; set; }
    public long VehicleTypeId { get; set; }
    public long TariffId { get; set; }
}
