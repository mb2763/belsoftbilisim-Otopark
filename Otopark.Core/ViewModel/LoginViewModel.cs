using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Otopark.Api.Services;
using Otopark.Core.Session;


namespace Otopark.Core;

public partial class LoginViewModel : ObservableObject
{
    private readonly AuthApiService _auth;
    private readonly ZoneApiService _zone;
    private readonly MainViewModel _main;


    public LoginViewModel(AuthApiService auth, ZoneApiService zone, MainViewModel main)
    {
        _auth = auth;
        _zone = zone;
        _main = main;

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
}
