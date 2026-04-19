namespace Otopark.Api.Services;

public class VehicleParkExitRequest
{
    public long CurrentUserId { get; set; }
    public string? Id { get; set; }
    public long VehicleEntryId { get; set; }
    public long PayingUserId { get; set; }
    public long ExitUserId { get; set; }
    public long ExitZoneId { get; set; }
    public DateTime ExitTimeStamp { get; set; }
    public string CalculatedFee { get; set; } = "0";
    public string? MembershipDiscount { get; set; }
    public string? PrepaidPayment { get; set; }
    public string PayableFee { get; set; } = "0";
    public long CompanyId { get; set; }
    public PaymentModel Payment { get; set; } = new();
}

public class PaymentModel
{
    public long CurrentUserId { get; set; }
    public string? Id { get; set; }
    public string? ReceiptSeries { get; set; }
    public long ReceiptNo { get; set; }
    public string? AmountCash { get; set; }
    public DateTime PaymentTime { get; set; }
    public int PaymentTypeId { get; set; }
    public long CompanyId { get; set; }
}

public class VehicleParkExitResponse
{
    public List<ErrorMessageObject>? Errors { get; set; }
    public string? Status { get; set; }
}
