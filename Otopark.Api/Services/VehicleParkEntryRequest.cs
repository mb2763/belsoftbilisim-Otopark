namespace Otopark.Api.Services;

public class VehicleParkEntryRequest
{
    public long CurrentUserId { get; set; }
    public string Id { get; set; } = "";
    public string Plate { get; set; } = "";

    public long EntryUserId { get; set; }
    public long EntryZoneId { get; set; }

    public DateTime EntryTimeStamp { get; set; }

    public long CompanyId { get; set; }

    public VehicleDefinitionModel VehicleDefinitionModel { get; set; }
        = new VehicleDefinitionModel();

    public string Photo { get; set; } = "";
    public string Path { get; set; } = "";
}

public class VehicleDefinitionModel
{
    public long CurrentUserId { get; set; }
    public string Id { get; set; } = "";
    public string Plate { get; set; } = "";

    public long CompanyId { get; set; }

    public long CustomerCompanyId { get; set; }
    public long CustomerPersonId { get; set; }

    public long VehicleTypeId { get; set; }
    public long TariffId { get; set; }

    public bool WarningCheck { get; set; }
    public string WarningNote { get; set; } = "";
}
