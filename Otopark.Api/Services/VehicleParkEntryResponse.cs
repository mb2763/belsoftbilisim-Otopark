namespace Otopark.Api.Services;

public class VehicleParkEntryResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public string? Status { get; set; }
    public string? InvoiceNumber { get; set; }
    public long TaxNumber { get; set; }
    public VehicleParkEntryResult? Result { get; set; }
    public List<VehicleParkEntryResult>? ResultList { get; set; }
}

public class ErrorMessageObject
{
    public int Code { get; set; }
    public string? Message { get; set; }
}

public class VehicleParkEntryResult
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public long VehicleDefinitionId { get; set; }
    public DateTime EntryTimestamp { get; set; }
    public long EntryUserId { get; set; }
    public long EntryZoneId { get; set; }
    public string? EntryPhotoPath { get; set; }
    public DateTime CreateDate { get; set; }
    public VehicleDefinitionResult? VehicleDefinition { get; set; }
}

public class VehicleDefinitionResult
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string? Plate { get; set; }
    public double Balance { get; set; }
    public double Credit { get; set; }
    public long TariffId { get; set; }
    public long VehicleTypeId { get; set; }
    public bool? WarningCheck { get; set; }
    public string? WarningNote { get; set; }
}
