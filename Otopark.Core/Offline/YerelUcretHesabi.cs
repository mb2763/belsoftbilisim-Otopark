using System;
using System.Linq;

namespace Otopark.Core.Offline;

/// <summary>
/// SUNUCUDAKİ VehicleParkManager.GetParkPriceDetayli'nin ÇEVRİMDIŞI KARŞILIĞI (20.09.2026).
/// Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 6.5. Yalnızca ÇEVRİMDIŞI EKRAN KARARI (bariyer aç/açma,
/// vatandaşa gösterilecek tutar) için kullanılır — SUNUCU NİHAİ HESABI kendisi yapar; bağlantı
/// gelince gerçek tutar OfflineSenkronManager.IsleGiris/IsleKapaliCikis içinde tekrar hesaplanır.
///
/// GetParkPriceDetayli ile BİREBİR AYNI kurallar:
///   totalSecond = tam saniye, totalMinute = totalSecond/60
///   abone/misafir -> 0
///   ucretsizDakika > 0 && totalMinute <= ucretsizDakika -> 0
///   kademe: UnitTimeStart <= totalMinute olanların en yüksek fiyatı (yoksa en yüksek UnitTimeFinish'li)
///   kapalı otopark gün çarpanı: gun = floor(saniye/86400)+1; gunlukUcret (UnitTimeStart>=240 ilk kademe,
///                                yoksa en geniş) * gun; sonuç kademe fiyatının ALTINA düşmez.
/// </summary>
public static class YerelUcretHesabi
{
    public sealed class Sonuc
    {
        public decimal Fiyat;
        public bool Hata;
        public string? MuafiyetNedeni;
    }

    public static Sonuc Hesapla(AnlikGoruntu anlik, string plaka, long bolgeId, DateTime girisZamani, DateTime cikisZamani,
                                 bool aboneMi, bool misafirMi, long? aracTipiIdVerilen = null)
    {
        var sonuc = new Sonuc();
        try
        {
            if (aboneMi) { sonuc.MuafiyetNedeni = "ABONE"; return sonuc; }
            if (misafirMi) { sonuc.MuafiyetNedeni = "MISAFIR"; return sonuc; }

            long aracTipiId = aracTipiIdVerilen ?? anlik.AracBul(plaka)?.AracTipiId ?? anlik.OtoAracTipiOku();
            var tarife = anlik.TarifeBul(bolgeId, aracTipiId, cikisZamani.Year);
            if (tarife == null) { sonuc.MuafiyetNedeni = "TARIFE_YOK"; return sonuc; }

            int totalSecond = Convert.ToInt32((cikisZamani - girisZamani).TotalSeconds);
            if (totalSecond < 0) totalSecond = 0;
            int totalMinute = totalSecond / 60;

            if (tarife.UcretsizDakika > 0 && totalMinute <= tarife.UcretsizDakika)
            {
                sonuc.MuafiyetNedeni = "UCRETSIZ_SURE";
                return sonuc;
            }

            if (tarife.Kademeler.Count == 0) { sonuc.MuafiyetNedeni = "TARIFE_YOK"; return sonuc; }

            var uygunKademeler = tarife.Kademeler.Where(k => k.bas <= totalMinute).ToList();
            decimal price = uygunKademeler.Count > 0
                ? uygunKademeler.Max(k => k.fiyat)
                : tarife.Kademeler.OrderByDescending(k => k.bit).First().fiyat;

            // ===== KAPALI OTOPARK GÜN ÇARPANI (KapaliOtoparkGunCarpani ile aynı) =====
            int gun = (int)Math.Floor(totalSecond / 86400.0) + 1;
            if (gun > 1)
            {
                var gunlukKademe = tarife.Kademeler.Where(k => k.bas >= 240).OrderBy(k => k.bas).FirstOrDefault();
                decimal gunlukUcret = gunlukKademe.fiyat > 0 || tarife.Kademeler.Any(k => k.bas >= 240)
                    ? gunlukKademe.fiyat
                    : tarife.Kademeler.OrderByDescending(k => k.bit).First().fiyat;

                if (gunlukUcret > 0)
                {
                    var carpim = gunlukUcret * gun;
                    if (carpim > price) price = carpim;
                }
            }

            if (price == 0) sonuc.MuafiyetNedeni = "SIFIR_TL_KADEME";
            sonuc.Fiyat = price;
            return sonuc;
        }
        catch (Exception ex)
        {
            sonuc.Hata = true;
            sonuc.Fiyat = 0;
            sonuc.MuafiyetNedeni = "HATA:" + ex.Message;
            return sonuc;
        }
    }
}
