using System;
using System.Collections.Generic;

namespace Otopark.Core.Offline;

// =====================================================================
// Sunucudaki ParkomatApp.Common.Helper.Models.OfflineModels.cs ile SÖZLEŞME
// olarak eşleşen istemci taraflı DTO'lar. Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 7.
// System.Text.Json PropertyNameCaseInsensitive=true ile deserialize edilir;
// serialize edilirken sunucu (ASP.NET Core varsayılanı: case-insensitive bind)
// PascalCase gönderimi de kabul eder.
// =====================================================================

public sealed class SahaKaydolIstek
{
    public string CihazId { get; set; } = "";
    public string Tur { get; set; } = "";
    public long CompanyId { get; set; }
    public long ZoneId { get; set; }
    public string Surum { get; set; } = "";
    public string MakineAdi { get; set; } = "";
}

public sealed class SahaKaydolYanit
{
    public bool Basarili { get; set; }
    public string? Mesaj { get; set; }
    public string? GizliAnahtar { get; set; }
    public DateTime SunucuZamani { get; set; }
}

public sealed class SahaNabizIstek
{
    public string CihazId { get; set; } = "";
    public int BekleyenIslem { get; set; }
    public int RedIslem { get; set; }
    public int SaatKaymasiSn { get; set; }
    public long? DiskBosMb { get; set; }
    public string Mod { get; set; } = "";
}

public sealed class SahaNabizYanit
{
    public bool Basarili { get; set; }
    public string? Mesaj { get; set; }
    public DateTime SunucuZamani { get; set; }
    public bool OfflineIzinli { get; set; }
    public bool BekleyenKomutVar { get; set; }
}

public sealed class OfflineAnlikIstek
{
    public string CihazId { get; set; } = "";
    public long CompanyId { get; set; }
    public long ZoneId { get; set; }
    public string? BilinenSurum { get; set; }
}

public sealed class OfflineAnlikGirisDto { public long EntryId { get; set; } public long VehicleDefinitionId { get; set; } public string PlakaAnahtar { get; set; } = ""; public DateTime GirisZamani { get; set; } public long BolgeId { get; set; } public long AracTipiId { get; set; } public bool Misafir { get; set; } }
public sealed class OfflineAnlikBorcDto { public long BorcId { get; set; } public long VehicleDefinitionId { get; set; } public string PlakaAnahtar { get; set; } = ""; public long? BolgeId { get; set; } public long? EntryId { get; set; } public decimal Kalan { get; set; } public string? Aciklama { get; set; } public DateTime Olusturma { get; set; } }
public sealed class OfflineAnlikAbonelikDto { public long AbonelikId { get; set; } public long VehicleDefinitionId { get; set; } public string PlakaAnahtar { get; set; } = ""; public long TarifeId { get; set; } public string? TarifeAdi { get; set; } public DateTime Baslangic { get; set; } public DateTime? Bitis { get; set; } public List<long> Bolgeler { get; set; } = new(); public bool Ucretli { get; set; } }
public sealed class OfflineAnlikAracDto { public long AracId { get; set; } public string PlakaAnahtar { get; set; } = ""; public string? Plaka { get; set; } public long AracTipiId { get; set; } public long TarifeId { get; set; } public long? MusteriFirmaId { get; set; } public string? UyariNotu { get; set; } }
public sealed class OfflineAnlikFiyatKademeDto { public int BaslangicDk { get; set; } public int BitisDk { get; set; } public decimal Fiyat { get; set; } }
public sealed class OfflineAnlikTarifeDto { public long TarifeDetayId { get; set; } public long BolgeId { get; set; } public List<long> AracTipleri { get; set; } = new(); public int Yil { get; set; } public int UcretsizDakika { get; set; } public int OpsiyonelSure { get; set; } public List<OfflineAnlikFiyatKademeDto> Kademeler { get; set; } = new(); }
public sealed class OfflineAnlikKameraDto { public int Tip { get; set; } public string? IpAdresi { get; set; } }
public sealed class OfflineAnlikAbonelikTipiDto { public long Id { get; set; } public string? Ad { get; set; } public decimal GunlukUcret { get; set; } }

public sealed class OfflineAnlikDto
{
    public string Surum { get; set; } = "";
    public DateTime AlinmaZamani { get; set; }
    public bool OfflineIzinli { get; set; }
    public int Kapasite { get; set; }
    public List<long> EngelliAracTipleri { get; set; } = new();
    public long OtoAracTipiId { get; set; }
    public long OtoTarifeId { get; set; }
    public List<OfflineAnlikGirisDto> AcikGirisler { get; set; } = new();
    public List<OfflineAnlikBorcDto> AcikBorclar { get; set; } = new();
    public List<OfflineAnlikAbonelikDto> AktifAbonelikler { get; set; } = new();
    public List<OfflineAnlikAracDto> Araclar { get; set; } = new();
    public List<OfflineAnlikTarifeDto> Tarifeler { get; set; } = new();
    public List<OfflineAnlikKameraDto> Kameralar { get; set; } = new();
    public List<OfflineAnlikAbonelikTipiDto> AbonelikTipleri { get; set; } = new();
}

public sealed class OfflineIslemKalemi
{
    public string IslemNo { get; set; } = "";
    public string Tur { get; set; } = "";
    public DateTime OlayZamani { get; set; }
    public DateTime OlayZamaniDuzeltilmis { get; set; }
    public string Kaynak { get; set; } = "";
    public string CihazId { get; set; } = "";
    public long KullaniciId { get; set; }
    public long CompanyId { get; set; }
    public long ZoneId { get; set; }
    public string Plaka { get; set; } = "";
    public string? YerelGirisId { get; set; }
    public string? BagliIslemNo { get; set; }
    public string GovdeJson { get; set; } = "";
}

public sealed class OfflineSenkronIstek
{
    public string CihazId { get; set; } = "";
    public List<OfflineIslemKalemi> Kalemler { get; set; } = new();
}

public sealed class OfflineIslemSonucu
{
    public string IslemNo { get; set; } = "";
    public string Durum { get; set; } = "";
    public string? Mesaj { get; set; }
    public long? SunucuEntryId { get; set; }
    public long? SunucuExitId { get; set; }
    public long? SunucuBorcId { get; set; }
    public long? SunucuOdemeId { get; set; }
    public long? SunucuAbonelikId { get; set; }
    public decimal? UcretSunucu { get; set; }
}

public sealed class OfflineSenkronYanit
{
    public List<OfflineIslemSonucu> Sonuclar { get; set; } = new();
}

// ---- kalem gövdeleri ----
public sealed class OfflineGirisGovdesi { public string Plate { get; set; } = ""; public long VehicleTypeId { get; set; } public long TariffId { get; set; } public long CustomerCompanyId { get; set; } public bool WarningCheck { get; set; } public string? WarningNote { get; set; } public string? Photo { get; set; } }
public sealed class OfflineKapaliCikisGovdesi { public long? EntryId { get; set; } public bool UcretsizCikis { get; set; } public string? Neden { get; set; } public decimal? YerelUcret { get; set; } public string? Photo { get; set; } public string? PersonelAciklama { get; set; } }
public sealed class OfflineBorcOdemeGovdesi { public long? EntryId { get; set; } public long[]? BorcIdler { get; set; } public decimal Tutar { get; set; } public int OdemeTipi { get; set; } public long VehicleDefinitionId { get; set; } public string? Aciklama { get; set; } public string? PavoOrderNo { get; set; } public string? PavoSaleNumber { get; set; } public string? PavoFaturaLinki { get; set; } public string? PavoFaturaNo { get; set; } }
public sealed class OfflineAbonelikEkleGovdesi { public long VehicleDefinitionId { get; set; } public long TariffId { get; set; } public DateTime StartDate { get; set; } public DateTime FinishDate { get; set; } public decimal Price { get; set; } public int DiscountRate { get; set; } public short PaymentTypeId { get; set; } public string? PavoOrderNo { get; set; } public string? PavoSaleNumber { get; set; } }
public sealed class OfflineNotGovdesi { public long? EntryId { get; set; } public string? Not { get; set; } }
public sealed class OfflineGirisIptalGovdesi { public long? EntryId { get; set; } public string? Neden { get; set; } }
