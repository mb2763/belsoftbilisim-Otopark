using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Otopark.Api.Services;

/// <summary>
/// GUN SONU Z RAPORU (01.09.2026 - madde 1).
///
/// Talep: "Gun sonu z rapor eklenmesi (hunat otopark) — otopark client exe'de
/// yapilabilir, mobil z rapor gibi."
///
/// IKI AYRI UC VAR, KARISTIRILMAMALI:
///   GetZReportReview -> ONIZLEME. Hicbir sey YAZMAZ, yalnizca ozet dondurur.
///   GetZReport       -> Z RAPORUNU ALIR ve Z_REPORT tablosuna KAYDEDER.
///
/// Mobil uygulamada tam bu ikisi karistirilmisti: metodun adi getZReport
/// olmasina ragmen istek /GetZReportReview'e gidiyordu; rapor aliniyor, fis
/// basiliyor, ekranda her sey normal gorunuyor ama VERITABANINDA Z RAPORU
/// OLUSMUYORDU. Ayni hataya dusmemek icin iki metot ayri ayri ve adlariyla
/// uyumlu sekilde tanimlandi.
///
/// MESAI UYARISI: GetZReport sunucuda mesai kontrolu yapar
/// (ZReportManager -> GetWorkingControl) ve mesai disinda
/// "Mesai Saatleri Disinda Islem Yapamazsiniz !" doner. Kapali otopark 7/24
/// calistigi icin gun sonu raporu mesai penceresi disinda alinmak istenebilir;
/// o durumda hata SESSIZ GECILMEZ, cagirana oldugu gibi iletilir.
/// </summary>
public class ZReportApiService
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ZReportApiService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>ONIZLEME: ekranda gostermek icin. Veritabanina KAYIT ATMAZ.</summary>
    public async Task<ZReportResult> GetReviewAsync(long personnelId, long companyId)
        => await CagirAsync("ZReport/GetZReportReview", personnelId, companyId, null);

    /// <summary>
    /// Z RAPORUNU ALIR ve KAYDEDER. Makbuz numarasi sunucuda uretilir.
    /// <paramref name="raporTarihi"/> verilirse rapor O GUNE yazilir
    /// (geriye donuk rapor). Bos ise bugun kullanilir.
    /// AYNI GUNE ikinci rapor sunucu tarafindan REDDEDILIR.
    /// </summary>
    public async Task<ZReportResult> AlVeKaydetAsync(long personnelId, long companyId, DateTime? raporTarihi = null)
        => await CagirAsync("ZReport/GetZReport", personnelId, companyId, raporTarihi);

    private async Task<ZReportResult> CagirAsync(string url, long personnelId, long companyId, DateTime? raporTarihi)
    {
        try
        {
            var govde = new
            {
                PersonnelId = personnelId,
                CurrentCompanyId = companyId,
                CurrentUserId = personnelId,
                // Bos gonderilirse sunucu BUGUNU kullanir (eski davranis).
                ReportDate = raporTarihi
            };

            using var response = await _http.PostAsJsonAsync(url, govde);

            if (!response.IsSuccessStatusCode)
                return new ZReportResult { Basarili = false, Mesaj = $"Sunucu yanit vermedi ({(int)response.StatusCode})." };

            var sonuc = await response.Content.ReadFromJsonAsync<ZReportEnvelope>(JsonOpts);

            if (sonuc == null)
                return new ZReportResult { Basarili = false, Mesaj = "Sunucudan bos yanit alindi." };

            if (sonuc.Errors != null && sonuc.Errors.Count > 0)
            {
                var mesaj = string.Join(", ", sonuc.Errors.ConvertAll(e => e.Message));
                return new ZReportResult { Basarili = false, Mesaj = string.IsNullOrWhiteSpace(mesaj) ? "Z raporu alinamadi." : mesaj };
            }

            // Result NULL ise SESSIZ BASARI SAYILMAZ. Bu tuzak sahada yasandi:
            // "hata yok" diye basari kabul edilip aslinda hicbir kayit
            // olusmuyordu.
            if (sonuc.Result == null)
                return new ZReportResult { Basarili = false, Mesaj = "Sunucudan rapor donmedi." };

            return new ZReportResult { Basarili = true, Rapor = sonuc.Result };
        }
        catch (Exception ex)
        {
            return new ZReportResult { Basarili = false, Mesaj = ex.Message };
        }
    }
}

public class ZReportResult
{
    public bool Basarili { get; set; }
    public string? Mesaj { get; set; }
    public ZReportDto? Rapor { get; set; }
}

public class ZReportEnvelope
{
    public ZReportDto? Result { get; set; }
    public List<ZReportError>? Errors { get; set; }
}

public class ZReportError
{
    public string? Message { get; set; }
}

/// <summary>VEW_Z_REPORT_REVIEW karsiligi (sunucu: VewZReportReview).</summary>
public class ZReportDto
{
    public long? ExitUserId { get; set; }
    public string? NameSurname { get; set; }
    public decimal? ParkedVehicleCount { get; set; }
    public decimal? NoPayVehicleCount { get; set; }
    public decimal? SubscriptionVehicleCount { get; set; }
    public decimal? SubscriptionSales { get; set; }
    public decimal? SubscriptionCreditSales { get; set; }
    public decimal? DebitCredit { get; set; }
    public decimal? LoadCreditBalance { get; set; }
    public decimal? PrepaidSales { get; set; }
    public decimal? DebitCollection { get; set; }
    public decimal? CashPayment { get; set; }
    public decimal? CreditCardPayment { get; set; }
    public decimal? HgsPayment { get; set; }
    public decimal? KioskPayment { get; set; }
}
