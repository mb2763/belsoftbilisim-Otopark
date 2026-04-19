using System.Net.Http.Json;
using System.Text.Json;

namespace Otopark.Api.Services;

public sealed class VehicleDefinitionApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public VehicleDefinitionApiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VehicleByPlateResponse?> GetVehicleByPlateAsync(long companyId, string plate)
    {
        var url = "VehicleDefinition/GetVehicleByPlate";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            companyId = companyId,
            plate = plate
        });

        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try { return JsonSerializer.Deserialize<VehicleByPlateResponse>(json, Opts); }
            catch { }

            return new VehicleByPlateResponse
            {
                Errors = new List<ErrorMessageObject>
                {
                    new() { Message = $"{(int)response.StatusCode} - {json}" }
                }
            };
        }

        return JsonSerializer.Deserialize<VehicleByPlateResponse>(json, Opts);
    }
}

public class VehicleByPlateResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public string? Status { get; set; }
    public string? InvoiceNumber { get; set; }
    public long TaxNumber { get; set; }
    public VewVehicleDefinition? Result { get; set; }
    public List<VewVehicleDefinition>? ResultList { get; set; }
}

public class VewVehicleDefinition
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public long? CustomerCompanyId { get; set; }
    public long? CustomerPersonId { get; set; }
    public long TariffId { get; set; }
    public long VehicleTypeId { get; set; }
    public bool? WarningCheck { get; set; }
    public string? WarningNote { get; set; }
    public string? Plate { get; set; }
    public decimal Balance { get; set; }
    public decimal Credit { get; set; }
    public DateTime CreateDate { get; set; }
    public long CreateUserId { get; set; }
    public bool IsDelete { get; set; }
    public string? CodeStr { get; set; }
    public string? NameStr { get; set; }
    public string? CompanyName { get; set; }
    public string? NameSurname { get; set; }
    public string? TariffName { get; set; }
    public long? TcNumber { get; set; }
    public long? TaxNumber { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? CustomerCompanyStr { get; set; }
}
