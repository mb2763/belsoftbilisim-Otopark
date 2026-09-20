using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Otopark.Api.Services;
using Otopark.Core.Session;
using Otopark.Core.Offline;


namespace Otopark.Core;

public partial class LoginViewModel : ObservableObject
{
    private readonly AuthApiService _auth;
    private readonly ZoneApiService _zone;
    private readonly MainViewModel _main;

    // ===== ÇEVRİMDIŞI ÇALIŞMA (20.09.2026) — bkz. PLAN_OFFLINE_CALISMA.md =====
    private readonly CihazKimligi _cihazKimligi;
    private readonly SahaOfflineClient _sahaClient;
    private readonly IslemKuyrugu _offlineKuyruk;
    private readonly AnlikGoruntu _offlineAnlik;
    private readonly BaglantiDurumu _baglanti;
    private readonly SahaAjaniSunucusu? _sahaAjani;
    private static bool _offlineDonguBaslatildi;

    public LoginViewModel(AuthApiService auth, ZoneApiService zone, MainViewModel main,
        CihazKimligi cihazKimligi, SahaOfflineClient sahaClient, IslemKuyrugu offlineKuyruk,
        AnlikGoruntu offlineAnlik, BaglantiDurumu baglanti, SahaAjaniSunucusu? sahaAjani = null)
    {
        _sahaAjani = sahaAjani;
        _auth = auth;
        _zone = zone;
        _main = main;
        _cihazKimligi = cihazKimligi;
        _sahaClient = sahaClient;
        _offlineKuyruk = offlineKuyruk;
        _offlineAnlik = offlineAnlik;
        _baglanti = baglanti;

        ZoneId = 1;
        LoginType = 3;

        // Onceki giris bilgilerini geri yukle
        UserNameEmail = LoginMemory.UserNameEmail;
        CompanyCode = LoginMemory.CompanyCode;

        _ = LoadZonesAsync();
    }

    public ObservableCollection<ZoneDto> Zones { get; } = new();

    [ObservableProperty] private ZoneDto? selectedZone;

    private async Task LoadZonesAsync()
    {
        if (_zone == null) return;
        try
        {
            var zones = await _zone.GetZonesAsync(companyId: 2, zoneClassId: 424);
            Zones.Clear();
            foreach (var z in zones)
                Zones.Add(z);

            // Onceki secili bolgeyi geri yukle
            if (LoginMemory.SelectedZoneId.HasValue)
                SelectedZone = Zones.FirstOrDefault(z => z.Id == LoginMemory.SelectedZoneId.Value);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Bölge listesi yüklenemedi: " + ex.Message;
        }
    }



    [ObservableProperty] private string userNameEmail = "";
    [ObservableProperty] private string companyCode = "";
    [ObservableProperty] private string password = "";

    [ObservableProperty] private long zoneId;
    [ObservableProperty] private int loginType;

    [ObservableProperty] private string errorMessage = "";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private async Task LoginAsync()
    {
        ErrorMessage = "";

        if (string.IsNullOrWhiteSpace(UserNameEmail) ||
            string.IsNullOrWhiteSpace(CompanyCode) ||
            string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Lütfen tüm alanları doldurun.";
            return;
        }


        try
        {
            var result = await _auth.LoginAsync(new LoginRequest
            {
                UserNameEmail = UserNameEmail,
                CompanyCode = CompanyCode,
                Password = Password,
                ZoneId = SelectedZone?.Id ?? 0,
                LoginType = 0
            });
            var httpClient = new HttpClient()
            {
                BaseAddress = new Uri("http://web.belsoft.com.tr:221/")
            };
            var vehicleApi = new VehicleParkApiService(httpClient);
            var vehicleDefApi = new VehicleDefinitionApiService(httpClient);
            var zoneApiForDash = new ZoneApiService(httpClient);
            var parkQuery = new VehicleParkQueryService(httpClient);
            var lookupApi = new LookupApiService(httpClient);
            // Şimdilik sadece başarılı girişte mesaj verelim.
            // ADIM 3'te buradan Personel Dashboard'a geçeceğiz.
            if (result?.Errors != null && result.Errors.Count > 0)
            {
                var msg = string.Join(", ", result.Errors
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .Select(e => e.Message));
                ErrorMessage = string.IsNullOrWhiteSpace(msg)
                    ? "Giris basarisiz. Lutfen bilgileri kontrol edin."
                    : msg;
                return;
            }

            // Basarili giris bilgilerini hafizada tut
            LoginMemory.UserNameEmail = UserNameEmail;
            LoginMemory.CompanyCode = CompanyCode;
            LoginMemory.SelectedZoneId = SelectedZone?.Id;
            LoginMemory.Save();

            var isAdmin = string.Equals(result.Result.UserType, "Yönetici", StringComparison.OrdinalIgnoreCase);

            // BOLGE SECIMI HERKES ICIN ZORUNLU (18.08.2026).
            //
            // Onceden yalnizca operatore zorunluydu; yonetici bolge secmeden
            // girebiliyor ve dashboard'a BolgeId = 0 ile dusuyordu. Sonuclari:
            //   - cikistaki borc esitlemesi (c.ZoneId == BolgeId) HICBIR borcu
            //     tutturamiyor -> borc kontrolu fiilen KAPALI,
            //   - giriste yazilan borc da ZoneId = 0 ile kaydediliyor; o borc
            //     kalici olarak hicbir bolgeye eslesmiyor ve arac bir daha hic
            //     engellenmiyor.
            //
            // Yonetici tum bolgeleri gormeye devam eder (LoadAllZonesAsync),
            // ancak arac giris/cikisi icin calisilan bolgenin belli olmasi sart.
            if (SelectedZone == null)
            {
                ErrorMessage = "Lütfen bir bölge seçiniz. Bölge seçilmeden araç giriş/çıkış işlemi yapılamaz.";
                return;
            }

            // BOLGE BAZLI YETKI: operator yalnizca YETKILI oldugu otoparka girebilir.
            // Yetki atanmamissa (bos liste) kisitlama uygulanmaz (kademeli gecis).
            if (!isAdmin && SelectedZone != null)
            {
                try
                {
                    var authZones = await zoneApiForDash.GetAuthorizedZonesAsync(
                        result.Result.Id, 2, SelectedZone.ZoneClassId);
                    if (authZones.Count > 0 && !authZones.Any(z => z.Id == SelectedZone.Id))
                    {
                        ErrorMessage = "Bu otoparka erisim yetkiniz yok. Lutfen yetkili oldugunuz bir bolge seciniz.";
                        return;
                    }
                }
                catch { /* yetki servisi hatasi girisi engellemesin */ }
            }

            UserSession.UserId = result.Result.Id;
            UserSession.CompanyId = 2;
            UserSession.UserName = result.Result.UserName;
            UserSession.IsAdmin = isAdmin;

            // MISAFIR ARAC ISARETLEME YETKISI: web'de tanimlanir, burada uygulanir
            // (ayni kalibin ilk ornegi yukaridaki bolge yetkisi kontrolu).
            // Yoneticiler her zaman yetkili; digerleri icin izin satiri aranir.
            // Yetki servisi hatasi GIRISI ENGELLEMEZ, yalnizca dugme gizli kalir.
            const int MISAFIR_ARAC_MENU_ID = 68;   // MenuType.GuestVehicle
            UserSession.CanMarkGuestVehicle = isAdmin;
            if (!isAdmin)
            {
                try
                {
                    UserSession.CanMarkGuestVehicle =
                        await _auth.HasMenuPrivilegeAsync(result.Result.Id, MISAFIR_ARAC_MENU_ID);
                }
                catch { UserSession.CanMarkGuestVehicle = false; }
            }

            var dashboardVm = new PersonnelDashboardViewModel(_main, vehicleApi, vehicleDefApi, zoneApiForDash, parkQuery, lookupApi);

            dashboardVm.LoggedUserName = result.Result.NameSurname;
            dashboardVm.LoggedZoneName = SelectedZone?.ZoneName ?? (isAdmin ? "Tum Bolgeler" : "");
            dashboardVm.BolgeId = (int)(SelectedZone?.Id ?? 0);
            dashboardVm.IsAdmin = isAdmin;

            // ===== ÇEVRİMDIŞI KATMANI DASHBOARD'A BAĞLA (20.09.2026) =====
            dashboardVm.OfflineBaglanti = _baglanti;
            dashboardVm.OfflineKuyruk = _offlineKuyruk;
            dashboardVm.OfflineAnlik = _offlineAnlik;
            dashboardVm.OfflineCihazKimligi = _cihazKimligi;
            dashboardVm.OfflineSahaClient = _sahaClient;

            // Kiosk LAN ödeme bildirimi -> dashboard toast'ı (21.09.2026).
            if (_sahaAjani != null)
                _sahaAjani.OdemeBildirimiAlindi += b =>
                    dashboardVm.KioskOdemeBildirimi(b.Plaka, b.Tutar, b.DogrulamaKodu);
            if (dashboardVm.BolgeId > 0)
            {
                _offlineAnlik.BolgeAyarla(UserSession.CompanyId, dashboardVm.BolgeId);
                _ = OfflineBaslatAsync(dashboardVm.BolgeId);
            }

            // Bolge listesini, kapasite ve tablo verilerini yukle
            _ = dashboardVm.LoadZoneCapacityAsync();
            if (dashboardVm.BolgeId > 0)
                _ = dashboardVm.LoadParkDataAsync();
            if (isAdmin)
                _ = dashboardVm.LoadAllZonesAsync();

            // Gecis
            _main.Navigate(dashboardVm);


        }
        catch (Exception ex)
        {
            ErrorMessage = "API Hatası: " + ex.Message;
        }
    }

    /// <summary>
    /// ÇEVRİMDIŞI KATMAN BAŞLATMA (20.09.2026). Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 7.1, 9.
    ///
    /// 1) Cihaz daha önce /Saha/Kaydol olmadıysa (ilk açılış) kaydolur; gizli anahtar
    ///    DPAPI ile diske yazılır (CihazKimligi.KaydiTamamla). Sunucu erişilemezse
    ///    sessizce vazgeçilir - bir sonraki başarılı çevrimiçi anda tekrar denenir.
    /// 2) İlk anlık görüntü çekilir (abonelik/borç/tarife önbelleği olmadan çevrimdışı
    ///    karar veremeyiz).
    /// 3) Kuyruk boşaltma + anlık görüntü tazeleme döngüsü (yalnızca bir kez, uygulama
    ///    ömrü boyunca) başlatılır: Senkron modda kuyruğu boşaltır, boşalınca
    ///    BaglantiDurumu'nu Çevrimiçi'ye döndürür; Çevrimiçiyken anlık görüntüyü tazeler.
    /// </summary>
    private async Task OfflineBaslatAsync(long bolgeId)
    {
        try
        {
            if (!_cihazKimligi.Kayitli)
            {
                var yanit = await _sahaClient.KaydolAsync(new SahaKaydolIstek
                {
                    CihazId = _cihazKimligi.CihazId,
                    Tur = "MASAUSTU",
                    CompanyId = UserSession.CompanyId,
                    ZoneId = bolgeId,
                    Surum = "1.0",
                    MakineAdi = Environment.MachineName
                });
                if (yanit?.Basarili == true && !string.IsNullOrEmpty(yanit.GizliAnahtar))
                    _cihazKimligi.KaydiTamamla(yanit.GizliAnahtar);
            }

            await _offlineAnlik.TazeleAsync();
        }
        catch { /* kayıt/ilk tazeleme başarısızsa döngü yine de başlar, sonraki turlarda dener */ }

        if (_offlineDonguBaslatildi) return;
        _offlineDonguBaslatildi = true;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (_baglanti.Mod == OfflineMod.Senkron)
                    {
                        var gonderilen = await _offlineKuyruk.SenkronizeEtAsync();
                        if (gonderilen == 0) _baglanti.SenkronTamamlandi();
                    }
                    else if (_baglanti.Mod == OfflineMod.Cevrimici)
                    {
                        await _offlineAnlik.TazeleAsync();
                    }
                }
                catch { /* senkron döngüsü asla çökmemeli */ }

                await Task.Delay(_baglanti.Mod == OfflineMod.Senkron ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(5));
            }
        });
    }
}
