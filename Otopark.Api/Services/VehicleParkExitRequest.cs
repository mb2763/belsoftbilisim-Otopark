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

    /// <summary>
    /// CIKIS anindaki plaka fotografi — "data:image/jpg;base64,..." bicimi.
    /// Girisin Photo alaninin karsiligi. Sunucu bunu vehicleExitPhoto klasorune yazip
    /// yolunu EXIT_PHOTO_PATH'e isler; web "Plaka Revizyon" ekrani bunu gosterir.
    /// Bos gonderilirse cikis normal kaydedilir, yalnizca fotograf olusmaz.
    /// </summary>
    public string? Photo { get; set; }

    /// <summary>
    /// BORC ZATEN VAR — cikista YENI borc yazma, MEVCUT borcu bu cikisa bagla.
    /// Kapali Otopark'ta borc GIRISTE olusturuluyor ("Kapali Otopark Giris - PLAKA").
    /// Bu bayrak olmadan NoPay(3) ile yapilan cikis ikinci bir borc daha yazar ve
    /// VEHICLE_DEFINITION.CREDIT iki katina cikar (80 + 80 = 160).
    /// true gonderildiginde sunucu yeni borc yazmaz; odenmemis ve henuz bir cikisa
    /// baglanmamis borclarin VEHICLE_EXIT_ID'sini olusan cikisa baglar.
    /// </summary>
    public bool BorcZatenVar { get; set; } = false;
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
