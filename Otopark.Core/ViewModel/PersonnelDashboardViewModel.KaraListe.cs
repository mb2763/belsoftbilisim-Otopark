using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Otopark.Api.Services;
using Otopark.Core.Session;

namespace Otopark.Core;

/// <summary>
/// KAPALI OTOPARK KARA LISTESI (11.10.2026) — masaustu tarafi.
///
/// SAHA: "Ondeki araca acilan bariyerden arkadaki ucretli arac odemeden cikiyor (HUNAT'ta
/// artti, birbirlerine haber veriyorlar). Bu araclar listeye eklenince tum katli otoparklara
/// giremesin." Kacan aracin girisi sistemde ACIK kaliyordu; arac geri geldiginde giris akisi
/// onu "zaten iceride" sayip bariyeri YENIDEN aciyordu.
///
/// KULLANICI KARARLARI:
///   - Sistem supheyi yakalar, personel onaylar: bariyer baska bir plakaya acildiktan sonraki
///     ARKADAN_CIKIS_PENCERE_SN icinde borclu bir plaka okunursa ekranda "ARKADAN CIKIS SUPHESI"
///     seridi cikar; personel tek tusla listeye ekler. Elle ekleme de vardir (ust dugme).
///   - Liste FIRMA geneli; borc odenince kayit sunucuda otomatik kapanir, giris serbest kalir.
///   - Eklenirken kacilan acik giris kacis aninda borclu cikisla kapanir (sunucu yapar).
///
/// GIRIS HIZI: her girise sunucu sorgusu EKLENMEZ. Liste 2 dk'da bir onbellege alinir;
/// onbellekte OLMAYAN plaka icin hic sorulmaz (bariyer eskisi kadar hizli). Onbellekte olan
/// plaka icin sunucuya "borcunu odedi mi" sorulur; sunucuya ulasilamazsa onbellege gore
/// engellenir. Sunucu eski surumse (uc yok) liste bos kalir = eski davranis.
/// </summary>
public partial class PersonnelDashboardViewModel
{
    /// <summary>Bariyer baska plakaya acildiktan sonra borclu plaka okumasi bu sure icindeyse "arkadan cikis" suphesi.</summary>
    private const int ARKADAN_CIKIS_PENCERE_SN = 20;

    /// <summary>Kara liste onbellegi tazeleme araligi.</summary>
    private static readonly TimeSpan KARA_LISTE_TAZELEME = TimeSpan.FromMinutes(2);

    private Dictionary<string, KaraListeSatiriDto> _karaListe = new();
    private DateTime? _karaListeSonAlim;
    private bool _karaListeUcuVar = true;

    private DateTime _sonCikisBariyerAn = DateTime.MinValue;
    private string? _sonCikisBariyerPlaka;

    private sealed record Suphe(string Plaka, long EntryId, long AracId, decimal Borc, DateTime Zaman, string? OncekiPlaka);
    private Suphe? _bekleyenSuphe;

    [ObservableProperty] private bool arkadanCikisSupheVar;
    [ObservableProperty] private string arkadanCikisSupheMetni = "";

    // ===================== ONBELLEK =====================

    private async void StartKaraListeYenileme()
    {
        // Ilk alim oturum acildiktan sonra (firma kimligi dolunca); ardindan periyodik.
        while (true)
        {
            try
            {
                if (UserSession.CompanyId > 0 &&
                    (_karaListeSonAlim == null || DateTime.Now - _karaListeSonAlim.Value >= KARA_LISTE_TAZELEME))
                    await KaraListeTazeleAsync();
            }
            catch { /* tazeleme dongusu asla cokmemeli */ }
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
    }

    private async Task KaraListeTazeleAsync()
    {
        var liste = await _vehicleApi.KaraListeGetirAsync(UserSession.CompanyId);
        if (liste == null) return;   // uc yok / ag hatasi: eski onbellek kalir
        _karaListe = liste.Where(k => k.Aktif && !string.IsNullOrWhiteSpace(k.Plaka))
                          .GroupBy(k => PlakaAnahtari(k.Plaka!))
                          .ToDictionary(g => g.Key, g => g.First());
        _karaListeSonAlim = DateTime.Now;
        _karaListeUcuVar = true;
    }

    // ===================== GIRIS ENGELI =====================

    /// <summary>
    /// Giris akisi (cevrimici) icin: true donerse bariyer ACILMAZ ve personel uyarilir.
    /// Onbellekte olmayan plakada sunucuya gidilmez (hiz).
    /// </summary>
    private async Task<bool> KaraListeGirisEngelliAsync(string plate)
    {
        if (!_karaListe.TryGetValue(PlakaAnahtari(plate), out var kayit)) return false;

        var r = await _vehicleApi.KaraListeGirisKontrolAsync(UserSession.CompanyId, plate);
        if (r != null)
        {
            if (!r.Engelli)
            {
                // Borc odenmis (kayit sunucuda kapandi) ya da artik listede degil.
                _karaListe.Remove(PlakaAnahtari(plate));
                if (r.OtomatikKapandi)
                    ShowToast($"{plate}: kara liste kaydı borç ödendiği için kapandı, giriş serbest.", true);
                return false;
            }
            KaraListeGirisUyarisi(plate, r.Borc, r.EklenmeTarihi, r.BolgeAdi, onbellek: false);
            return true;
        }

        // Sunucuya ulasilamadi: onbellege gore engelle.
        KaraListeGirisUyarisi(plate, kayit.GuncelBorc, kayit.SupheZamani ?? kayit.EklenmeTarihi, kayit.BolgeAdi, onbellek: true);
        return true;
    }

    /// <summary>Cevrimdisi giris icin: yalnizca onbellek.</summary>
    private bool KaraListeOnbellekEngelli(string plate)
    {
        if (!_karaListe.TryGetValue(PlakaAnahtari(plate), out var kayit)) return false;
        KaraListeGirisUyarisi(plate, kayit.GuncelBorc, kayit.SupheZamani ?? kayit.EklenmeTarihi, kayit.BolgeAdi, onbellek: true);
        return true;
    }

    private void KaraListeGirisUyarisi(string plate, decimal borc, DateTime? tarih, string? bolge, bool onbellek)
    {
        var ne = tarih.HasValue ? $" ({tarih.Value:dd.MM HH:mm}{(string.IsNullOrWhiteSpace(bolge) ? "" : ", " + bolge)})" : "";
        ShowToast($"⛔ KARA LİSTE: {plate} ödemeden çıkış yaptı{ne}. {borc:0.##} TL borcu ödenmeden GİRİŞ YAPAMAZ" +
                  (onbellek ? " (bağlantı yok, son bilinen liste)." : "."), false);
        EntryDetectedPlate = "";
        _entryPendingPhotoBase64 = "";
    }

    // ===================== ARKADAN CIKIS SUPHESI =====================

    /// <summary>
    /// Cikis bariyeri her acildiginda (otomatik onay, borclu cikis, uzaktan komut, elle dugme)
    /// View tarafindan cagrilir. Plaka bos olabilir (elle dugme).
    /// </summary>
    public void CikisBariyeriAcildi(string? plaka)
    {
        _sonCikisBariyerAn = DateTime.Now;
        _sonCikisBariyerPlaka = plaka;
    }

    /// <summary>
    /// Cikis akisi borclu araci durdurdugunda cagrilir: bariyer az once BASKA bir plakaya acildiysa
    /// bu arac acik bariyerden gecmis olabilir -> ekranda supheli seridi.
    /// </summary>
    private void ArkadanCikisSupheKontrol(string plate, long entryId, long aracId, decimal borc)
    {
        var gecen = (DateTime.Now - _sonCikisBariyerAn).TotalSeconds;
        if (gecen < 0 || gecen > ARKADAN_CIKIS_PENCERE_SN) return;
        if (!string.IsNullOrWhiteSpace(_sonCikisBariyerPlaka) && PlakaAyniMi(_sonCikisBariyerPlaka, plate)) return;
        if (_bekleyenSuphe != null && PlakaAyniMi(_bekleyenSuphe.Plaka, plate)) return;
        if (_karaListe.ContainsKey(PlakaAnahtari(plate))) return;

        _bekleyenSuphe = new Suphe(plate, entryId, aracId, borc, DateTime.Now, _sonCikisBariyerPlaka);
        var onceki = string.IsNullOrWhiteSpace(_sonCikisBariyerPlaka) ? "başka bir araç" : _sonCikisBariyerPlaka;
        ArkadanCikisSupheMetni = $"ARKADAN ÇIKIŞ ŞÜPHESİ: {plate} — {borc:0.##} TL borçlu, bariyer {(int)gecen} sn önce {onceki} için açılmıştı ({DateTime.Now:HH:mm:ss}). Kamerayı kontrol edin.";
        ArkadanCikisSupheVar = true;
    }

    [RelayCommand]
    private async Task SupheyiKaraListeyeEkleAsync()
    {
        var s = _bekleyenSuphe;
        if (s == null) { ArkadanCikisSupheVar = false; return; }

        var neden = "Ödeme yapmadan arkadan çıkış" +
                    (string.IsNullOrWhiteSpace(s.OncekiPlaka) ? "" : $" (bariyer {s.OncekiPlaka} için açılmıştı)");
        var ok = await KaraListeyeEkleAsync(s.Plaka, s.EntryId, s.AracId, s.Zaman, neden, "MASAUSTU_SUPHE");
        if (ok) SupheyiKapat();
    }

    [RelayCommand]
    private void SupheyiYoksay() => SupheyiKapat();

    private void SupheyiKapat()
    {
        _bekleyenSuphe = null;
        ArkadanCikisSupheVar = false;
        ArkadanCikisSupheMetni = "";
    }

    // ===================== EKLEME =====================

    /// <summary>
    /// Listeden secili arac icin elle ekleme (ust "Kara Listeye Ekle" dugmesi). Neden View'dan gelir.
    /// </summary>
    public async Task<bool> SeciliAraciKaraListeyeEkleAsync(string neden)
    {
        var row = SelectedVehicle;
        if (row == null || string.IsNullOrWhiteSpace(row.Plate))
        {
            ShowToast("Önce listeden aracı seçiniz.", false);
            return false;
        }
        long entryId = row.ExitDateTime == null ? row.EntryId : 0;
        return await KaraListeyeEkleAsync(row.Plate.Trim(), entryId, 0, null, neden, "MASAUSTU");
    }

    private async Task<bool> KaraListeyeEkleAsync(string plate, long entryId, long aracId, DateTime? supheZamani, string neden, string kaynak)
    {
        var r = await _vehicleApi.KaraListeEkleAsync(new KaraListeEkleRequest
        {
            CompanyId = UserSession.CompanyId,
            CurrentUserId = UserSession.UserId,
            VehicleDefinitionId = aracId,
            Plaka = plate,
            VehicleEntryId = entryId > 0 ? entryId : null,
            ZoneId = BolgeId > 0 ? BolgeId : null,
            SupheZamani = supheZamani,
            Neden = neden,
            Kaynak = kaynak
        });

        if (r == null)
        {
            ShowToast($"{plate}: kara listeye EKLENEMEDİ (sunucuya ulaşılamadı ya da sunucu güncel değil).", false);
            return false;
        }

        var mesaj = (r.Mesaj ?? (r.Basarili ? "Eklendi." : "Eklenemedi.")) + (string.IsNullOrWhiteSpace(r.Uyari) ? "" : " " + r.Uyari);
        ShowToast(mesaj, r.Basarili);
        if (!r.Basarili) return false;

        // Onbellege hemen yaz: ayni arac bu bariyerden hemen girmeye calisirsa engellensin.
        _karaListe[PlakaAnahtari(plate)] = new KaraListeSatiriDto
        {
            Id = r.Id, Plaka = plate, GuncelBorc = r.Borc, EklenmeBorcu = r.Borc, Aktif = true,
            EklenmeTarihi = DateTime.Now, SupheZamani = supheZamani, Neden = neden, BolgeAdi = LoggedZoneName
        };
        try { await LoadParkDataAsync(); } catch { }
        return true;
    }

    // ===================== KARA LISTE SEKMESI =====================

    /// <summary>
    /// "Kara Liste" sekmesi: firma genelindeki aktif kara liste, guncel borcla.
    /// Sunucuda uc yoksa false doner; cagiran eski "bolge borclulari" gorunumune duser.
    /// </summary>
    private async Task<bool> KaraListeSekmesiYukleAsync()
    {
        var liste = await _vehicleApi.KaraListeGetirAsync(UserSession.CompanyId);
        if (liste == null) { _karaListeUcuVar = false; return false; }

        _karaListe = liste.Where(k => k.Aktif && !string.IsNullOrWhiteSpace(k.Plaka))
                          .GroupBy(k => PlakaAnahtari(k.Plaka!))
                          .ToDictionary(g => g.Key, g => g.First());
        _karaListeSonAlim = DateTime.Now;

        _allVehicles.Clear();
        foreach (var k in liste.Where(k => k.Aktif))
        {
            _allVehicles.Add(new VehicleRow
            {
                EntryId = 0,
                Plate = k.Plaka ?? "",
                ParkingName = string.IsNullOrWhiteSpace(k.BolgeAdi) ? LoggedZoneName : k.BolgeAdi!,
                ParkType = "Borclu",
                EntryDateTime = k.SupheZamani ?? k.EklenmeTarihi,   // kacis ani
                ExitDateTime = null,
                OldDebt = k.GuncelBorc,                              // IsBlacklisted => OldDebt > 0
                CurrentDebt = 0m,
                TotalDebt = k.GuncelBorc,
                ExitFee = 0m,
                SubscriptionName = "KARA LİSTE: " + (k.Neden ?? ""),
                EntryPlateImagePath = "",
                ExitPlateImagePath = "",
            });
        }
        return true;
    }
}
