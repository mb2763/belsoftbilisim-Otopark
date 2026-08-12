namespace Otopark.Api.Services;

public class AddVehicleCreditRequest
{
    public long CurrentUserId { get; set; }
    public string? Id { get; set; }
    public long VehicleDefinitionId { get; set; }
    public string DebtAmount { get; set; } = "0";
    public string PaidAmount { get; set; } = "0";
    public string Description { get; set; } = "";
    public long CompanyId { get; set; }
    public long ZoneId { get; set; }
    public long VehicleExitId { get; set; }
    public long? VehicleSubscriptionId { get; set; }

    /// <summary>
    /// Borcun ait oldugu GIRIS kaydi (VEHICLE_PARK_ENTRY.ID).
    /// Sunucudaki gun basina tahakkuk gorevi "bu girise kac gun yazilmis" sorusunu
    /// bu alanla cevapliyor; gonderilmezse giristeki 1. gun sayilamaz ve gorev
    /// ayni gunu IKINCI KEZ yazar.
    /// </summary>
    public long? VehicleEntryId { get; set; }
}

public class AddVehicleCreditResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public string? Status { get; set; }
}
