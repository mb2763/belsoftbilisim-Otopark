using Otopark.Core.Services;

namespace Otopark.Client.Services;

public static class AppConfigHelper
{
    /// <summary>
    /// appsettings.json > Parking:BolgeId - YEDEK bolge numarasi.
    ///
    /// Yalnizca giris ekranindan bolge SECILMEDIGINDE (yonetici "Tum Bolgeler" modu)
    /// kamera tanimini cekmek icin kullanilir. Normal kullanimda secilen bolge gecerlidir.
    ///
    /// Eskiden burada int.Parse vardi; anahtar yoksa veya sayi degilse ISTISNA firlatiyordu.
    /// Cagiran taraf bunu bos catch ile yutup zoneId=0 ile devam ediyordu; sonucta
    /// "kamera gelmiyor" arizasinin sebebi hicbir yere yazilmiyordu.
    /// Artik guvenli: okunamazsa 0 doner ve cagiran taraf durumu ACIKCA loglar.
    /// </summary>
    public static int BolgeId
    {
        get
        {
            var ham = AppConfig.Configuration["Parking:BolgeId"];
            return int.TryParse(ham, out var deger) ? deger : 0;
        }
    }
}
