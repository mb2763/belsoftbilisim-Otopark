using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading.Tasks;
using Otopark.Api.Services;
using Otopark.Core.Session;
using Otopark.Core.Models;
using Newtonsoft.Json;

namespace Otopark.Core;

public partial class PersonnelDashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly VehicleParkApiService _vehicleApi;
    private readonly VehicleDefinitionApiService _vehicleDefApi;
    private readonly ZoneApiService _zoneApi;

    // Yikama penceresi gibi yardimci ekranlarin API'ye erismesi icin.
    public VehicleParkApiService ApiService => _vehicleApi;
    private readonly VehicleParkQueryService _parkQuery;
    private readonly LookupApiService _lookupApi;

    // Tum arac kayitlari (filtrelenmemis)
    private readonly ObservableCollection<VehicleRow> _allVehicles = new();

    // Ekranda gorunen (filtrelenmis)
    public ObservableCollection<VehicleRow> VehicleList { get; } = new();
    public ObservableCollection<ParkingItem> Parkings { get; } = new();
    public ObservableCollection<PlateRow> PlateList { get; set; }

    [ObservableProperty] private ParkingItem? selectedParking;

    // Giris
    [ObservableProperty] private string entryDetectedPlate = "";
    [ObservableProperty] private string entryCameraImagePath = "";
    [ObservableProperty] private string entryPhoto1 = "";
    [ObservableProperty] private string entryPhoto2 = "";
    private string _entryPendingPhotoBase64 = "";

    // Cikis
    [ObservableProperty] private string exitDetectedPlate = "";
    [ObservableProperty] private string exitCameraImagePath = "";
    [ObservableProperty] private string exitPhoto1 = "";
    [ObservableProperty] private string exitPhoto2 = "";

    // Toast
    [ObservableProperty] private string toastMessage = "";
    [ObservableProperty] private bool isToastVisible;
    [ObservableProperty] private bool isToastSuccess;

    // Kullanici / Bolge
    [ObservableProperty] private string loggedUserName = "";
    [ObservableProperty] private string loggedZoneName = "";
    [ObservableProperty] private int bolgeId;
    [ObservableProperty] private bool isAdmin;

    // Admin bolge secimi
    public ObservableCollection<ZoneDto> AllZones { get; } = new();
    [ObservableProperty] private ZoneDto? selectedAdminZone;

    partial void OnSelectedAdminZoneChanged(ZoneDto? value)
    {
        if (value == null) return;
        BolgeId = (int)value.Id;
        LoggedZoneName = value.ZoneName;
        TotalCapacity = value.Capacity;
        _ = LoadParkDataAsync();
    }

    // KPI
    [ObservableProperty] private int totalCapacity;
    [ObservableProperty] private int currentVehicleCount;
    [ObservableProperty] private int emptyParkCount;
    [ObservableProperty] private decimal totalRevenue;
    [ObservableProperty] private decimal subscriptionRevenue;

    // Filtre: Plaka arama
    [ObservableProperty] private string plateSearchText = "";

    // Filtre: Durum
    [ObservableProperty] private bool isStatusAllInOut = true;       // Tumu (Giris+Cikis) - yeni default
    [ObservableProperty] private bool isStatusApproved;              // Onaylilar (cikis yapmis)
    [ObservableProperty] private bool isStatusUnapprovedOnly;        // Onaysizlar (sadece giris yapmis)
    [ObservableProperty] private bool isStatusUnapproved;             // Iceridekiler (cikis yapmamis - icerideki araclar)
    [ObservableProperty] private bool isStatusCancelled;              // Iptaller
    [ObservableProperty] private bool isStatusAll;                    // Hepsi (iptal dahil)
    [ObservableProperty] private bool isStatusBlacklist;             // Kara liste (odenmemis borcu olan araclar)

    // NOT: Iptaller ve Kara Liste FARKLI veri kaynagi kullanir (silinmis girisler / borc kayitlari),
    // bu yuzden secildiklerinde ve birakildiklarinda liste yeniden YUKLENIR; digerlerinde filtre yeter.
    partial void OnIsStatusAllInOutChanged(bool value) { if (value) ReloadOrFilter(); }
    partial void OnIsStatusApprovedChanged(bool value) { if (value) ReloadOrFilter(); }
    partial void OnIsStatusUnapprovedOnlyChanged(bool value) { if (value) ReloadOrFilter(); }
    partial void OnIsStatusUnapprovedChanged(bool value) { if (value) ReloadOrFilter(); }
    partial void OnIsStatusCancelledChanged(bool value) { if (value) _ = LoadParkDataAsync(); }
    partial void OnIsStatusAllChanged(bool value) { if (value) ReloadOrFilter(); }
    partial void OnIsStatusBlacklistChanged(bool value) { if (value) _ = LoadParkDataAsync(); }

    /// <summary>Ozel kaynakli (iptal/kara liste) listeden normal listeye donuluyorsa yeniden yukle; degilse sadece filtrele.</summary>
    private void ReloadOrFilter()
    {
        if (_specialSourceLoaded) { _ = LoadParkDataAsync(); return; }
        ApplyFiltersInternal();
    }

    /// <summary>Son yuklenen liste ozel kaynaktan mi geldi (iptaller / kara liste)?</summary>
    private bool _specialSourceLoaded;

    // Filtre: Zaman
    [ObservableProperty] private bool isTimeShift = true;
    [ObservableProperty] private bool isTimeDay;
    [ObservableProperty] private bool isTimeWeek;
    [ObservableProperty] private bool isTimeMonth;

    // Bariyer event'leri
    public event Func<Task>? OnOpenEntryGateRequested;
    public event Func<Task>? OnOpenExitGateRequested;

    // Fis basma event'i - code-behind'da ReceiptPrintService cagirilir
    public event Action<ReceiptInfo>? OnPrintEntryReceipt;
    public event Action<ReceiptInfo>? OnPrintExitReceipt;

    // Plaka okundugunda View tarafindan set edilir
    public string[] EntryPlateSnapshotPaths { get; set; } = Array.Empty<string>();
    public string[] ExitPlateSnapshotPaths { get; set; } = Array.Empty<string>();

    // Cikis panelinde giriş anı görseli
    [ObservableProperty] private string exitEntryImagePath = "";

    // Secili satir
    [ObservableProperty] private VehicleRow? selectedVehicle;

    partial void OnSelectedVehicleChanged(VehicleRow? value)
    {
        if (value == null) return;

        // Giris panelini her zaman doldur
        EntryDetectedPlate = value.Plate;
        EntryCameraImagePath = value.EntryPlateImagePath;

        // Cikis panelini: sadece cikis kaydi varsa
        if (value.ExitDateTime.HasValue)
        {
            ExitDetectedPlate = value.Plate;
            ExitCameraImagePath = value.ExitPlateImagePath;
            ExitEntryImagePath = value.EntryPlateImagePath;
        }
        else
        {
            ExitDetectedPlate = "";
            ExitCameraImagePath = "";
            ExitEntryImagePath = "";
        }
    }

    // Popup event: plaka kayitli degilse code-behind popup acar
    // string=plate, return: true=kayit yapildi, false=iptal
    public event Func<string, LookupApiService, Task<bool>>? OnVehicleRegistrationRequired;

    // Onay dialog event'i (code-behind WPF MessageBox acar)
    public event Func<string, string, Task<bool>>? OnConfirmRequired;

    public LookupApiService LookupApi => _lookupApi;

    public PersonnelDashboardViewModel(MainViewModel main, VehicleParkApiService vehicleApi,
        VehicleDefinitionApiService vehicleDefApi, ZoneApiService zoneApi,
        VehicleParkQueryService parkQuery, LookupApiService lookupApi)
    {
        _main = main;
        _vehicleApi = vehicleApi;
        _vehicleDefApi = vehicleDefApi;
        _zoneApi = zoneApi;
        _parkQuery = parkQuery;
        _lookupApi = lookupApi;

        TotalCapacity = 0;
        CurrentVehicleCount = 0;
        EmptyParkCount = 0;
        LoggedUserName = "";

        PlateList = new ObservableCollection<PlateRow>();

        // Sure timer - her saniye guncelle
        StartDurationTimer();
    }

    private async void StartDurationTimer()
    {
        while (true)
        {
            await Task.Delay(1000);
            foreach (var v in _allVehicles)
                v.UpdateDuration();
        }
    }


    // ===== KAPASITE YUKLE =====

    public async Task LoadZoneCapacityAsync()
    {
        try
        {
            var zones = await _zoneApi.GetZonesAsync(UserSession.CompanyId, 424);
            var zone = zones.FirstOrDefault(z => z.Id == BolgeId);
            if (zone != null)
            {
                TotalCapacity = zone.Capacity;
                UpdateParkCounts();
            }
        }
        catch { }
    }

    // ===== TABLO VERILERINI API'DEN YUKLE =====

    public async Task LoadParkDataAsync()
    {
        if (BolgeId == 0) return;

        try
        {
            // KARA LISTE: tarihten BAGIMSIZ — bolgede odenmemis (eski) borcu olan TUM araclar.
            // Arac o gun otoparka girmemis olsa bile listelenir.
            if (IsStatusBlacklist)
            {
                _specialSourceLoaded = true;
                await LoadBlacklistDataAsync();
                return;
            }

            // Iptaller de ozel kaynak; normal listeye donunce yeniden yukleme gerekir.
            _specialSourceLoaded = IsStatusCancelled;

            List<VewVehicleParkCurrentDto> data;

            // Secili zaman araligi (Mesai/Gun -> bugun, Hafta -> 7 gun, Ay -> 30 gun)
            DateTime? rangeStart = IsTimeWeek ? DateTime.Now.AddDays(-7)
                                 : IsTimeMonth ? DateTime.Now.AddDays(-30)
                                 : (DateTime?)null;

            // IPTALLER: iptal edilen girisler sunucuda soft-delete edildigi icin normal
            // liste sorgusunda GELMEZ; ayri (silinmis kayitlar) sorgusu kullanilir.
            if (IsStatusCancelled)
            {
                data = await _parkQuery.GetCancelledByZoneAndDateRangeAsync(
                    UserSession.CompanyId, BolgeId,
                    rangeStart ?? DateTime.Now.Date, DateTime.Now);
            }
            else if (rangeStart.HasValue)
            {
                data = await _parkQuery.GetByZoneAndDateRangeAsync(
                    UserSession.CompanyId, BolgeId, rangeStart.Value, DateTime.Now);
            }
            else
            {
                // Gun veya Mesai -> bugunun verileri
                data = await _parkQuery.GetByZoneTodayAsync(
                    UserSession.CompanyId, BolgeId);
            }

            _allVehicles.Clear();
            foreach (var d in data)
            {
                var row = new VehicleRow
                {
                    EntryId = d.EntryId,
                    Plate = d.Plate ?? "",
                    ParkingName = LoggedZoneName,
                    // Iptaller sekmesinde gelen kayitlar SILINMIS girislerdir -> "Iptal" olarak isaretlenir.
                    ParkType = IsStatusCancelled ? "Iptal"
                             : (d.ExitTimestamp.HasValue ? "Cikis" : "Giris"),
                    EntryDateTime = d.EntryTimestamp,
                    ExitDateTime = d.ExitTimestamp,
                    EntryPlateImagePath = "",
                    ExitPlateImagePath = "",
                    OldDebt = (decimal)d.Balance,
                    CurrentDebt = (decimal)(d.CalculatedFee ?? 0),
                    TotalDebt = (decimal)(d.CurrentDebitAmount ?? 0),
                    // Cikis yapilmissa cikis tutari hasilata sayilir; icerideki araclarda 0.
                    ExitFee = d.ExitTimestamp.HasValue ? (decimal)(d.CalculatedFee ?? 0) : 0m,
                };

                // Lokal cache'te plaka+timestamp ile esles
                row.EntryPlateImagePath = FindLocalImageForRow(row.Plate, d.EntryTimestamp, isEntry: true);
                if (d.ExitTimestamp.HasValue)
                    row.ExitPlateImagePath = FindLocalImageForRow(row.Plate, d.ExitTimestamp.Value, isEntry: false);

                _allVehicles.Add(row);

                // Lokal cache'te yoksa API'den cek (arka planda, sadece giris icin)
                if (string.IsNullOrEmpty(row.EntryPlateImagePath) && d.EntryId > 0)
                    _ = LoadEntryImageAsync(row, d.EntryId);
            }

            // TUMU sekmesinde: kiosk uzerinden islem gormus (gunluk ucret/abonelik/borc) ama
            // FIZIKSEL GIRIS KAYDI (VEHICLE_PARK_ENTRIES) olusmamis plakalar normal sorguda
            // (yukaridaki VEW_VEHICLE_PARK bazli liste) hic gorunmuyordu; personel bu araclarin
            // cikisini/plakasini "Tumu" listesinden bulamiyordu. Bu bolgede odenmemis borcu olan
            // TUM plakalar (GetZoneDebtorsAsync, tarihten bagimsiz) buraya da eklenir; zaten
            // normal listede olanlar (plaka bazinda) TEKRAR EKLENMEZ.
            if (IsStatusAllInOut)
            {
                try
                {
                    var debtors = await _parkQuery.GetZoneDebtorsAsync(UserSession.CompanyId, BolgeId);
                    var mevcutPlakalar = _allVehicles.Select(v => v.Plate.ToUpperInvariant()).ToHashSet();
                    foreach (var b in debtors)
                    {
                        var plaka = (b.Plate ?? "").ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(plaka) || mevcutPlakalar.Contains(plaka)) continue;

                        _allVehicles.Add(new VehicleRow
                        {
                            EntryId = 0,
                            Plate = b.Plate ?? "",
                            ParkingName = LoggedZoneName,
                            ParkType = "Borclu",           // giris kaydi yok; sadece borc kaydi
                            EntryDateTime = b.LastDebtDate,
                            ExitDateTime = null,
                            OldDebt = b.DebtAmount,
                            CurrentDebt = 0m,
                            TotalDebt = b.DebtAmount,
                            ExitFee = 0m,
                            EntryPlateImagePath = "",
                            ExitPlateImagePath = "",
                        });
                    }
                }
                catch { /* ek liste alinamazsa ana liste yine de gorunur kalsin */ }
            }

            UpdateParkCounts();
            ApplyFiltersInternal();
        }
        catch (Exception ex)
        {
            ShowToast("Veri yuklenemedi: " + ex.Message, false);
        }
    }

    /// <summary>
    /// KARA LISTE verisi: bu (kapali otopark) bolgesinde ODENMEMIS eski borcu olan TUM araclar.
    /// Tarih sinirlamasi yoktur; arac bugun otoparka girmemis olsa bile borcu varsa listelenir.
    /// </summary>
    private async Task LoadBlacklistDataAsync()
    {
        try
        {
            var debtors = await _parkQuery.GetZoneDebtorsAsync(UserSession.CompanyId, BolgeId);

            _allVehicles.Clear();
            foreach (var d in debtors)
            {
                _allVehicles.Add(new VehicleRow
                {
                    EntryId = 0,
                    Plate = d.Plate ?? "",
                    ParkingName = LoggedZoneName,
                    ParkType = "Borclu",              // giris/cikis kaydi degil, borc kaydi
                    EntryDateTime = d.LastDebtDate,   // en son borc tarihi
                    ExitDateTime = null,
                    OldDebt = d.DebtAmount,           // IsBlacklisted => OldDebt > 0
                    CurrentDebt = 0m,
                    TotalDebt = d.DebtAmount,
                    ExitFee = 0m,
                    EntryPlateImagePath = "",
                    ExitPlateImagePath = "",
                });
            }

            UpdateParkCounts();
            ApplyFiltersInternal();
        }
        catch (Exception ex)
        {
            ShowToast("Kara liste yuklenemedi: " + ex.Message, false);
        }
    }

    // Admin icin tum bolgeleri yukle
    public async Task LoadAllZonesAsync()
    {
        try
        {
            var zones = await _zoneApi.GetZonesAsync(UserSession.CompanyId, 424);
            AllZones.Clear();
            foreach (var z in zones)
                AllZones.Add(z);
        }
        catch { }
    }

    private void UpdateParkCounts()
    {
        // Kara liste / Iptaller modunda _allVehicles otoparkin MEVCUT nufusu DEGILDIR
        // (borc kayitlari veya silinmis girisler). Sayac ve hasilat bozulmasin diye guncellenmez.
        if (IsStatusBlacklist || IsStatusCancelled) return;

        // "Borclu" satirlar (Tumu sekmesine eklenen, fiziksel girisi olmayan borc kayitlari)
        // icerideki arac sayisina DAHIL EDILMEZ - gercekte otoparkta degiller.
        CurrentVehicleCount = _allVehicles.Count(v => v.ExitDateTime == null && v.ParkType != "Borclu");
        EmptyParkCount = Math.Max(0, TotalCapacity - CurrentVehicleCount);
        // Hasilat: yalnizca CIKIS yapan araclarin (bu bolge) cikis tutarlari sayilir.
        // Iceride bekleyen veya iptal edilen araclar hasilata yansimaz.
        TotalRevenue = _allVehicles
            .Where(v => v.ExitDateTime != null && v.ParkType != "Iptal")
            .Sum(v => v.ExitFee);
        SubscriptionRevenue = 0; // TODO: Abonelik hasilati ayri hesaplanacak
    }

    // ===== GIRIS: Plaka tanima callback =====

    public void SetPendingEntry(string plate, string photoBase64)
    {
        _entryPendingPhotoBase64 = photoBase64;
    }

    // ===== GIRIS: Onayla =====

    private async Task DoApproveEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(EntryDetectedPlate))
        {
            ShowToast("Onaylanacak plaka yok. Once plaka tanitiniz.", false);
            return;
        }

        var plate = EntryDetectedPlate.Trim();
        var photo = _entryPendingPhotoBase64;

        // Son 5 dakikada benzer (Levenshtein <= 2) aktif giris var mi?
        // OCR farkli okumus olabilir (33BAT102 vs 33BT1021 gibi) - duplicate kayit onlenir.
        var recentSimilar = _allVehicles
            .Where(v => v.ExitDateTime == null && v.ParkType != "Iptal")
            .Where(v => (DateTime.Now - v.EntryDateTime).TotalMinutes <= 5)
            .Select(v => new { Row = v, Dist = LevenshteinDistance(v.Plate, plate) })
            .Where(x => x.Dist <= 2)
            .OrderBy(x => x.Dist)
            .FirstOrDefault();

        if (recentSimilar != null)
        {
            var dk = (int)(DateTime.Now - recentSimilar.Row.EntryDateTime).TotalMinutes;
            ShowToast($"Bu arac zaten icerde: {recentSimilar.Row.Plate} ({dk} dk once giris yapti).", false);
            return;
        }

        // ===== KAPASITE KONTROLU =====
        // Otopark DOLU ise (icerideki arac sayisi >= kapasite) yalnizca ABONE araclar girebilir.
        // Ucretli (abone olmayan) araclar icin bariyer ACILMAZ, giris engellenir.
        // Kapasite tanimsiz (0) ise kontrol uygulanmaz (sinirsiz).
        if (TotalCapacity > 0 && CurrentVehicleCount >= TotalCapacity)
        {
            bool isSubscriber = false;
            try
            {
                var capSubResp = await _vehicleApi.CheckSubscriptionAsync(plate, UserSession.CompanyId);
                isSubscriber = capSubResp != null && capSubResp.IsSubscriber;
            }
            catch
            {
                // abonelik sorgulanamadi -> dolu otoparkta riski almamak icin girisi engelle
                isSubscriber = false;
            }

            if (!isSubscriber)
            {
                ShowToast($"Otopark dolu ({CurrentVehicleCount}/{TotalCapacity}), sadece abonelere acik. {plate} girisi yapilamaz.", false);
                return;
            }
            // Abone arac -> normal akisa devam, giris + bariyer acilir.
        }

        try
        {
            // 1. Once plaka kayitli mi sorgula
            var vehicleCheck = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);

            if (vehicleCheck?.Result == null)
            {
                // Plaka kayitli degil - OTOMATIK KAYIT (otomobil + bolgenin tarifesi).
                // Onceden popup acilirdi; artik personel beklemeden hizli giris.
                var autoOk = await TryAutoRegisterVehicleAsync(plate);
                if (!autoOk)
                {
                    ShowToast($"{plate} icin otomatik arac kaydi yapilamadi.", false);
                    return;
                }
                vehicleCheck = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);
                if (vehicleCheck?.Result == null)
                {
                    ShowToast("Otomatik kayit sonrasi arac dogrulanamadi.", false);
                    return;
                }
            }

            var veh = vehicleCheck?.Result;

            // 2. Giris API'ye gonder
            var req = new VehicleParkEntryRequest
            {
                Plate = plate,
                CurrentUserId = UserSession.UserId,
                EntryUserId = UserSession.UserId,
                EntryZoneId = BolgeId,
                CompanyId = UserSession.CompanyId,
                EntryTimeStamp = DateTime.Now,
                Photo = string.IsNullOrEmpty(photo) ? "" : $"data:image/jpg;base64,{photo}",
                VehicleDefinitionModel = new VehicleDefinitionModel
                {
                    Plate = veh?.Plate ?? plate,
                    CompanyId = UserSession.CompanyId,
                    CurrentUserId = UserSession.UserId,
                    VehicleTypeId = veh?.VehicleTypeId ?? 0,
                    TariffId = veh?.TariffId ?? 0,
                    CustomerCompanyId = veh?.CustomerCompanyId ?? 0,
                    WarningCheck = veh?.WarningCheck ?? false,
                    WarningNote = veh?.WarningNote ?? ""
                }
            };

            var response = await _vehicleApi.AddEntryAsync(req);
            var json = JsonConvert.SerializeObject(req);

            if (response == null)
            {
                ShowToast("Sunucudan yanit alinamadi.", false);
                return;
            }

            if (response.Errors != null && response.Errors.Count > 0)
            {
                var errorMsg = string.Join(", ", response.Errors
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .Select(e => e.Message));
                ShowToast(string.IsNullOrWhiteSpace(errorMsg)
                    ? "Giris kaydedilemedi." : errorMsg, false);
                return;
            }

            var entry = response.Result;
            var vehDef = entry?.VehicleDefinition;

            // Plaka okundugunda zaten kaydedilmis snapshot'larin ilk yolunu al
            var imgPath = GetFirstSnapshotPath(isEntry: true);

            var row = new VehicleRow
            {
                EntryId = entry?.Id ?? 0,
                Plate = vehDef?.Plate ?? plate,
                ParkingName = LoggedZoneName,
                ParkType = "Giris",
                EntryDateTime = entry?.EntryTimestamp ?? DateTime.Now,
                EntryPlateImagePath = imgPath,
                OldDebt = (decimal)(veh?.Balance ?? 0),
                CurrentDebt = 0,
                EntryType = "N",        // default Normal
                IsSubscriber = false,
            };

            _allVehicles.Insert(0, row);
            UpdateParkCounts();
            ApplyFiltersInternal();

            // ===== ARACIN ABONELIK DURUMUNU API'DEN KONTROL ET =====
            // A (abone, yesil) ya da N (normal, sari) badge'i + abonelik turu
            try
            {
                var subResp = await _vehicleApi.CheckSubscriptionAsync(row.Plate, UserSession.CompanyId);
                if (subResp != null && subResp.IsSubscriber)
                {
                    row.IsSubscriber = true;
                    row.EntryType = "A";
                    row.SubscriptionName = subResp.SubscriptionName ?? "";
                }
                else
                {
                    row.IsSubscriber = false;
                    row.EntryType = "N";
                    row.SubscriptionName = "";
                }
            }
            catch
            {
                // hata olursa default (N) kalir
            }

            // Giris basarili - tarife ucretini cek ve aracı borclandir.
            // ABONE araclardan giriste ucret ALINMAZ (abonelik tutari 0) -> yalnizca abone DEGILSE borclandir.
            if (entry?.Id > 0 && !row.IsSubscriber)
            {
                try
                {
                    var parkPrice = await _vehicleApi.GetParkPriceAsync(entry.Id);
                    if (parkPrice > 0 && veh != null)
                    {
                        var creditReq = new AddVehicleCreditRequest
                        {
                            CurrentUserId = UserSession.UserId,
                            VehicleDefinitionId = veh.Id,
                            DebtAmount = parkPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            PaidAmount = "0",
                            Description = $"Kapali Otopark Giris - {plate}",
                            CompanyId = UserSession.CompanyId,
                            ZoneId = BolgeId,
                            VehicleExitId = 0
                        };

                        await _vehicleApi.AddVehicleCreditAsync(creditReq);

                        // Tablodaki anlik borcu guncelle
                        row.CurrentDebt = (decimal)parkPrice;
                        row.TotalDebt = row.OldDebt + row.CurrentDebt;
                    }
                }
                catch { /* borclandirma hatasi girisi engellemez */ }
            }

            ShowToast($"{vehDef?.Plate ?? plate} giris kaydedildi.", true);

            if (OnOpenEntryGateRequested != null)
                await OnOpenEntryGateRequested.Invoke();

            EntryDetectedPlate = "";
            _entryPendingPhotoBase64 = "";
        }
        catch (Exception ex)
        {
            ShowToast("API Hatasi: " + ex.Message, false);
        }
    }

    [RelayCommand]
    private async Task ApproveEntryAsync() => await DoApproveEntryAsync();

    // ===== KACIRMALARDAN ICERI AL =====

    [ObservableProperty] private string missedPlateInput = "";

    [RelayCommand]
    private async Task ImportMissedAsync()
    {
        var plate = (MissedPlateInput ?? "").Trim().ToUpperInvariant();
        plate = new string(plate.Where(char.IsLetterOrDigit).ToArray());

        if (string.IsNullOrWhiteSpace(plate) || plate.Length < 5)
        {
            ShowToast("Gecerli bir plaka giriniz.", false);
            return;
        }

        EntryDetectedPlate = plate;
        // Gorsel yok; pending photo bosaltilir
        _entryPendingPhotoBase64 = "";
        await DoApproveEntryAsync();

        MissedPlateInput = "";
    }

    // ===== KACIRMALARDAN DISARI AL =====
    // "Iceri Al" karsisindaki buton: kacirilan plakanin CIKISINI yapar.
    [RelayCommand]
    private async Task ImportMissedExitAsync()
    {
        var plate = (MissedPlateInput ?? "").Trim().ToUpperInvariant();
        plate = new string(plate.Where(char.IsLetterOrDigit).ToArray());

        if (string.IsNullOrWhiteSpace(plate) || plate.Length < 5)
        {
            ShowToast("Gecerli bir plaka giriniz.", false);
            return;
        }

        ExitDetectedPlate = plate;
        await DoApproveExitAsync();

        MissedPlateInput = "";
    }

    // ===== PLAKA DUZELTME =====

    // Code-behind popup acar, secili satirin plakasini gunceller
    public event Func<VehicleRow, Task>? OnCorrectPlateRequested;

    [RelayCommand]
    private async Task CorrectPlateAsync(VehicleRow? row)
    {
        if (row == null) return;
        if (OnCorrectPlateRequested != null)
            await OnCorrectPlateRequested.Invoke(row);
    }

    // ===== OTOMATIK KAYIT =====

    // Cache'le, her giriste lookup tekrarlanmasin
    private long _cachedAutoVehicleTypeId;
    private long _cachedAutoTariffId;
    private decimal _cachedDailyFee = -1m;

    /// <summary>
    /// Plakayi otomatik olarak kaydeder: arac turu = OTOMOBIL, tarife = "Kapali" iceren ilk tarife.
    /// Personel popup'i ile mudahalesi yok.
    /// </summary>
    private async Task<bool> TryAutoRegisterVehicleAsync(string plate)
    {
        try
        {
            await EnsureAutoDefaultsAsync();
            if (_cachedAutoVehicleTypeId == 0 || _cachedAutoTariffId == 0) return false;

            var req = new AddVehicleRequest
            {
                CurrentUserId = UserSession.UserId,
                Plate = plate,
                CompanyId = UserSession.CompanyId,
                CustomerCompanyId = null,
                VehicleTypeId = _cachedAutoVehicleTypeId,
                TariffId = _cachedAutoTariffId
            };

            var result = await _lookupApi.AddVehicleAsync(req);
            if (result?.Errors != null && result.Errors.Count > 0) return false;
            return result?.Result != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Arac turu (OTOMOBIL) ve "Kapali Otopark" tarifesini lookup'tan bulur, cache'ler.
    /// Gunluk ucret de tarifeden alinir (varsa).
    /// </summary>
    private async Task EnsureAutoDefaultsAsync()
    {
        if (_cachedAutoVehicleTypeId != 0 && _cachedAutoTariffId != 0) return;
        try
        {
            var types = await _lookupApi.GetVehicleTypesAsync(UserSession.CompanyId);
            var auto = types.FirstOrDefault(t =>
                t.VehicleTypeName != null && t.VehicleTypeName.Contains("OTOMOBIL", StringComparison.OrdinalIgnoreCase));
            _cachedAutoVehicleTypeId = auto?.Id ?? (types.FirstOrDefault()?.Id ?? 0);

            var tariffs = await _lookupApi.GetTariffsAsync(UserSession.CompanyId);
            var closed = tariffs.FirstOrDefault(t => t.TariffName != null &&
                t.TariffName.Contains("Kapal", StringComparison.OrdinalIgnoreCase));
            _cachedAutoTariffId = closed?.Id ?? 422; // 422 = "Kapali Otopark" varsayilan
        }
        catch { /* lookup yetersiz - sonraki cagride tekrar denenir */ }
    }

    // ===== IPTAL =====

    [RelayCommand]
    private async Task CancelEntryAsync(VehicleRow? row)
    {
        if (row == null) return;
        if (row.EntryId <= 0) { ShowToast("Giris Id bulunamadi.", false); return; }
        if (row.ExitDateTime != null) { ShowToast("Cikis yapilmis kayitlar iptal edilemez.", false); return; }
        if (row.ParkType == "Iptal") { ShowToast("Bu kayit zaten iptal edilmis.", false); return; }

        var ok = OnConfirmRequired != null
            ? await OnConfirmRequired.Invoke(
                "Iptal Onayi",
                $"{row.Plate} plakali aracin giris kaydi iptal edilecek. Onayliyor musunuz?")
            : true;
        if (!ok) return;

        try
        {
            var resp = await _vehicleApi.DeleteEntryAsync(row.EntryId, UserSession.CompanyId, UserSession.UserId);
            if (resp?.Errors != null && resp.Errors.Count > 0)
            {
                var msg = string.Join(", ", resp.Errors.Where(e => !string.IsNullOrEmpty(e.Message)).Select(e => e.Message));
                ShowToast(string.IsNullOrWhiteSpace(msg) ? "Iptal basarisiz." : msg, false);
                return;
            }

            row.ParkType = "Iptal";
            UpdateParkCounts();
            ApplyFiltersInternal();
            ShowToast($"{row.Plate} giris kaydi iptal edildi.", true);
        }
        catch (Exception ex)
        {
            ShowToast("Iptal hatasi: " + ex.Message, false);
        }
    }

    /// <summary>
    /// Plakayi backend'de gunceller. Yeni plaka kayitli degilse popup acar, kayit sonrasi tekrar dener.
    /// Basariliysa true, aksi takdirde false doner (satir UI'da geri alinabilir).
    /// </summary>
    public async Task<bool> ApplyPlateCorrectionAsync(VehicleRow row, string newPlate)
    {
        if (row == null || string.IsNullOrWhiteSpace(newPlate)) return false;
        if (row.EntryId <= 0)
        {
            ShowToast("Giris Id bulunamadi.", false);
            return false;
        }

        try
        {
            var resp = await _vehicleApi.UpdateEntryPlateAsync(row.EntryId, newPlate, UserSession.CompanyId, UserSession.UserId);

            // Yeni plaka kayitli degilse backend hata doner -> popup ile kayit yaptiralim
            if (resp?.Errors != null && resp.Errors.Count > 0)
            {
                var msg = string.Join(", ", resp.Errors.Where(e => !string.IsNullOrEmpty(e.Message)).Select(e => e.Message));
                var notRegistered = msg.Contains("kayıtlı değil", StringComparison.OrdinalIgnoreCase)
                                    || msg.Contains("kayitli degil", StringComparison.OrdinalIgnoreCase);

                if (notRegistered && OnVehicleRegistrationRequired != null)
                {
                    var registered = await OnVehicleRegistrationRequired.Invoke(newPlate, _lookupApi);
                    if (!registered) { ShowToast("Plaka duzeltme iptal edildi.", false); return false; }

                    // Arac kaydedildi, tekrar dene
                    resp = await _vehicleApi.UpdateEntryPlateAsync(row.EntryId, newPlate, UserSession.CompanyId, UserSession.UserId);
                    if (resp?.Errors != null && resp.Errors.Count > 0)
                    {
                        ShowToast("Guncelleme basarisiz: " + string.Join(", ", resp.Errors.Select(e => e.Message)), false);
                        return false;
                    }
                }
                else
                {
                    ShowToast("Guncelleme basarisiz: " + msg, false);
                    return false;
                }
            }

            row.Plate = newPlate;
            ShowToast($"Plaka duzeltildi: {newPlate}", true);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast("API hatasi: " + ex.Message, false);
            return false;
        }
    }

    [RelayCommand]
    private async Task ApproveAndPrintEntryAsync()
    {
        await DoApproveEntryAsync();
        // Son eklenen satir varsa fis bas
        var lastRow = _allVehicles.FirstOrDefault();
        if (lastRow != null && lastRow.ParkType == "Giris")
        {
            OnPrintEntryReceipt?.Invoke(new ReceiptInfo
            {
                ReceiptNo = lastRow.EntryId.ToString(),
                Plate = lastRow.Plate,
                ZoneName = LoggedZoneName,
                EntryDateTime = lastRow.EntryDateTime,
                Fee = lastRow.CurrentDebt,
                OldDebt = lastRow.OldDebt,
                OperatorName = LoggedUserName
            });
        }
    }

    // ===== CIKIS: Onayla =====

    private async Task DoApproveExitAsync()
    {
        if (string.IsNullOrWhiteSpace(ExitDetectedPlate))
        {
            ShowToast("Cikis icin plaka yok. Once plaka tanitiniz.", false);
            return;
        }

        var plate = ExitDetectedPlate.Trim();

        try
        {
            // 1. Plaka kayitli mi? Degilse otomatik kayit yap.
            var response = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);
            if (response?.Result == null)
            {
                var autoOk = await TryAutoRegisterVehicleAsync(plate);
                if (!autoOk) { ShowToast($"{plate} otomatik kayit basarisiz.", false); return; }
                response = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);
                if (response?.Result == null) { ShowToast("Arac dogrulanamadi.", false); return; }
            }

            var vehicle = response.Result;

            // 2. Aktif giris var mi?
            var existingRow = _allVehicles.FirstOrDefault(v =>
                string.Equals(v.Plate, plate, StringComparison.OrdinalIgnoreCase) &&
                v.ExitDateTime == null && v.ParkType != "Iptal");

            long entryId = existingRow?.EntryId ?? 0;
            if (entryId == 0)
            {
                try
                {
                    var parkData = await _parkQuery.GetByZoneTodayAsync(UserSession.CompanyId, BolgeId);
                    var parkEntry = parkData.FirstOrDefault(p =>
                        string.Equals(p.Plate, plate, StringComparison.OrdinalIgnoreCase) &&
                        p.ExitTimestamp == null);
                    entryId = parkEntry?.EntryId ?? 0;
                }
                catch { }
            }

            // 3. Giris yoksa OTOMATIK GIRIS olustur (15 dk oncesine).
            if (entryId == 0)
            {
                ShowToast($"{plate} icin giris kaydi yok, 15 dk oncesine otomatik giris olusturuluyor.", true);
                entryId = await CreateAutoBackdatedEntryAsync(plate, vehicle);
                if (entryId == 0) { ShowToast("Otomatik giris olusturulamadi.", false); return; }
            }

            // 4. Borc kontrolu - tum bolge borclari + bu bolgeye ait borc.
            var creditInfo = await GetVehicleDebtsAsync(vehicle.Id);
            decimal zoneDebt = creditInfo.zoneDebt;
            decimal totalDebt = creditInfo.totalDebt;

            // 5. Gunluk ucret hesabi: gece 23:59'i geçtiyse her gun icin gunluk ucret eklenir.
            var entryRow = _allVehicles.FirstOrDefault(v => v.EntryId == entryId);
            DateTime entryTs = entryRow?.EntryDateTime ?? DateTime.Now.AddMinutes(-15);
            int extraDays = ComputeOvernightDays(entryTs, DateTime.Now);
            if (extraDays > 0 && _cachedDailyFee > 0)
            {
                decimal additionalFee = extraDays * _cachedDailyFee;
                zoneDebt += additionalFee;
                totalDebt += additionalFee;
                // Backend'e ekleyelim ki kayit tutulsun
                try
                {
                    await _vehicleApi.AddVehicleCreditAsync(new AddVehicleCreditRequest
                    {
                        CurrentUserId = UserSession.UserId,
                        VehicleDefinitionId = vehicle.Id,
                        DebtAmount = additionalFee.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        PaidAmount = "0",
                        Description = $"{extraDays} gun gecikme ucreti ({_cachedDailyFee:F2} TL/gun)",
                        CompanyId = UserSession.CompanyId,
                        ZoneId = BolgeId,
                        VehicleExitId = 0
                    });
                }
                catch { /* borclanma hatasi cikisi bloke etmez */ }
            }

            // 6a. YIKAMA KONTROLU: bu girise ait aktif yikama fisi var mi?
            //   - Fis var + ucretsiz sure DOLMAMIS  -> otopark ucreti yikama ile karsilanmis sayilir,
            //     bolge borcu (zoneDebt) yok sayilip cikisa devam edilir.
            //   - Fis var + ucretsiz sure DOLMUS     -> normal otopark ucreti odenmeden cikis YAPILMAZ;
            //     mesaj ozel olarak "yikama ucretsiz sureniz bitti, kiosktan odeyin" seklinde verilir.
            bool washBypass = false;
            try
            {
                var washStatus = await _vehicleApi.GetWashStatusAsync(UserSession.CompanyId, entryId);
                if (washStatus.HasWashReceipt)
                {
                    if (!washStatus.IsExpired)
                    {
                        washBypass = true;   // ucretsiz yikama suresi icinde -> borc bypass
                    }
                    else if (zoneDebt > 0)
                    {
                        ShowToast(
                            $"{plate}: Yıkama için belirlenen ücretsiz süreniz bittiği için park ücreti " +
                            $"({zoneDebt:F2} TL) ödemeniz gerekiyor. Lütfen kiosk cihazından ödeme yapınız.",
                            false);
                        return;
                    }
                }
            }
            catch { /* yikama durumu alinamazsa normal borc kontrolune devam edilir */ }

            // 6b. Bu bolgeye ait borc varsa cikisi engelle (yikama ile karsilanmadiysa).
            if (!washBypass && zoneDebt > 0)
            {
                var msg = $"{plate} plakali aracin {LoggedZoneName} kapali otopark icin {zoneDebt:F2} TL borcu bulunmakta. Borcu odenmeden cikis yapilamaz.";
                if (totalDebt > zoneDebt)
                    msg += $" (Tum bolgelerdeki toplam borc: {totalDebt:F2} TL)";
                ShowToast(msg, false);
                return;
            }
            if (washBypass && zoneDebt > 0)
            {
                ShowToast($"{plate}: Otopark ücreti yıkama ile karşılandı, borç ödendi olarak işaretlendi.", true);
            }

            // 7. Cikis API
            var exitReq = new VehicleParkExitRequest
            {
                CurrentUserId = UserSession.UserId,
                VehicleEntryId = entryId,
                PayingUserId = UserSession.UserId,
                ExitUserId = UserSession.UserId,
                ExitZoneId = BolgeId,
                ExitTimeStamp = DateTime.Now,
                CalculatedFee = "0",
                PayableFee = "0",
                MembershipDiscount = "0",
                CompanyId = UserSession.CompanyId,
                Payment = new PaymentModel
                {
                    CurrentUserId = UserSession.UserId,
                    ReceiptNo = 0,
                    PaymentTypeId = 1,
                    AmountCash = "0",
                    PaymentTime = DateTime.Now,
                    CompanyId = UserSession.CompanyId
                }
            };

            var exitResponse = await _vehicleApi.AddExitAsync(exitReq);
            if (exitResponse?.Errors != null && exitResponse.Errors.Count > 0)
            {
                var errorMsg = string.Join(", ", exitResponse.Errors
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .Select(e => e.Message));
                ShowToast(string.IsNullOrWhiteSpace(errorMsg) ? "Cikis kaydedilemedi." : errorMsg, false);
                return;
            }

            if (existingRow != null)
            {
                existingRow.ExitDateTime = DateTime.Now;
                existingRow.ExitPlateImagePath = GetFirstSnapshotPath(isEntry: false);
                existingRow.ParkType = "Cikis";
                existingRow.ExitFee = existingRow.CurrentDebt;   // cikis tutarini hasilata yansit (sifirlanmadan once yakala)
                existingRow.CurrentDebt = 0;
                existingRow.TotalDebt = existingRow.OldDebt;
            }

            UpdateParkCounts();
            ApplyFiltersInternal();

            if (OnOpenExitGateRequested != null)
                await OnOpenExitGateRequested.Invoke();

            string toast = $"{plate} cikis kaydedildi. Bariyer aciliyor...";
            if (totalDebt > 0) toast += $" (Diger bolgelerde toplam borc: {totalDebt:F2} TL)";
            ShowToast(toast, true);
            ExitDetectedPlate = "";
        }
        catch (Exception ex)
        {
            ShowToast("API Hatasi: " + ex.Message, false);
        }
    }

    /// <summary>
    /// Cikista plaka var ama giris yoksa, 15 dakika oncesine geriye donuk giris olusturur.
    /// Borc sorgulamasi bu girise gore yapilir. EntryId doner.
    /// </summary>
    private async Task<long> CreateAutoBackdatedEntryAsync(string plate, VewVehicleDefinition vehicle)
    {
        try
        {
            var req = new VehicleParkEntryRequest
            {
                Plate = plate,
                CurrentUserId = UserSession.UserId,
                EntryUserId = UserSession.UserId,
                EntryZoneId = BolgeId,
                CompanyId = UserSession.CompanyId,
                EntryTimeStamp = DateTime.Now.AddMinutes(-15),
                Photo = "",
                VehicleDefinitionModel = new VehicleDefinitionModel
                {
                    Plate = vehicle.Plate ?? plate,
                    CompanyId = UserSession.CompanyId,
                    CurrentUserId = UserSession.UserId,
                    VehicleTypeId = vehicle.VehicleTypeId,
                    TariffId = vehicle.TariffId,
                    CustomerCompanyId = vehicle.CustomerCompanyId ?? 0,
                    WarningCheck = vehicle.WarningCheck ?? false,
                    WarningNote = vehicle.WarningNote ?? ""
                }
            };
            var resp = await _vehicleApi.AddEntryAsync(req);
            if (resp?.Errors != null && resp.Errors.Count > 0) return 0;
            return resp?.Result?.Id ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Aracin tum bolgelerdeki borc bilgisini doner: (kendi bolge borcu, tum bolge toplami).
    /// </summary>
    private async Task<(decimal zoneDebt, decimal totalDebt)> GetVehicleDebtsAsync(long vehicleDefinitionId)
    {
        try
        {
            var credits = await _vehicleApi.GetVehicleCreditsAsync(vehicleDefinitionId);
            decimal zone = 0, total = 0;
            foreach (var c in credits)
            {
                var balance = c.DebtAmount - c.PaidAmount;
                if (balance <= 0) continue;
                total += balance;
                if (c.ZoneId.HasValue && c.ZoneId.Value == BolgeId)
                    zone += balance;
            }
            return (zone, total);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Iki plaka arasinda Levenshtein (karakter edit) mesafesi.
    /// "33BAT102" - "33BT1021" -> 3 (yakin ama farkli OCR sonucu).
    /// "06BBD660" - "06BBD660" -> 0 (ayni).
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// Giris ile su an arasinda kac kez 23:59'u gectigini hesaplar.
    /// 3 gun kalmissa 3 doner. (Sabit gunluk ucret bu sayiyla carpilir.)
    /// </summary>
    private static int ComputeOvernightDays(DateTime entryTs, DateTime nowTs)
    {
        var entryDate = entryTs.Date;
        var nowDate = nowTs.Date;
        if (nowDate <= entryDate) return 0;
        return (nowDate - entryDate).Days;
    }

    [RelayCommand]
    private async Task ApproveExitAsync() => await DoApproveExitAsync();

    [RelayCommand]
    private async Task ApproveAndPrintExitAsync()
    {
        // Cikis oncesi plaka kaydet
        var exitPlate = ExitDetectedPlate?.Trim() ?? "";
        await DoApproveExitAsync();

        // Cikis yapilmis satiri bul ve fis bas
        var exitRow = _allVehicles.FirstOrDefault(v => v.Plate == exitPlate && v.ParkType == "Cikis");
        if (exitRow != null)
        {
            OnPrintExitReceipt?.Invoke(new ReceiptInfo
            {
                ReceiptNo = exitRow.EntryId.ToString(),
                Plate = exitRow.Plate,
                ZoneName = LoggedZoneName,
                EntryDateTime = exitRow.EntryDateTime,
                ExitDateTime = exitRow.ExitDateTime,
                Fee = exitRow.CurrentDebt,
                OldDebt = exitRow.OldDebt,
                OperatorName = LoggedUserName
            });
        }
    }

    // ===== RESIM YUKLEME =====

    private async Task LoadEntryImageAsync(VehicleRow row, long entryId)
    {
        try
        {
            var path = System.IO.Path.Combine(@"C:\Otopark\ImageCache\", $"entry_{entryId}.jpg");
            if (System.IO.File.Exists(path))
            {
                row.EntryPlateImagePath = path;
                return;
            }

            var base64 = await _vehicleApi.GetEntryImageBase64Async(entryId);
            if (string.IsNullOrEmpty(base64)) return;

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, Convert.FromBase64String(base64));
            row.EntryPlateImagePath = path;
        }
        catch { }
    }

    private string GetFirstSnapshotPath(bool isEntry)
    {
        var paths = isEntry ? EntryPlateSnapshotPaths : ExitPlateSnapshotPaths;
        return paths.Length > 0 ? paths[0] : "";
    }

    /// <summary>
    /// Belirtilen plaka icin giris anindaki gorsel yolunu dondurur.
    /// Once _allVehicles'tan, bulamazsa ImageCache klasorunden _E_ desenli en son dosyayi alir.
    /// </summary>
    public string GetEntryImageForPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";

        var row = _allVehicles.FirstOrDefault(v =>
            string.Equals(v.Plate, plate, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(v.EntryPlateImagePath));

        if (row != null && System.IO.File.Exists(row.EntryPlateImagePath))
            return row.EntryPlateImagePath;

        var cacheDir = @"C:\Otopark\ImageCache\";
        if (!System.IO.Directory.Exists(cacheDir)) return "";

        var safePlate = string.Concat(plate.Split(System.IO.Path.GetInvalidFileNameChars()));
        // Yeni naming: PLAKA_E_yyyyMMddHHmmss.jpg
        var files = System.IO.Directory.GetFiles(cacheDir, $"{safePlate}_E_*.jpg")
            .OrderByDescending(f => f)
            .ToArray();

        return files.Length > 0 ? files[0] : "";
    }

    /// <summary>
    /// Plaka + timestamp eslesmesiyle lokal cache'ten resim yolu bulur.
    /// Format: PLAKA_E_yyyyMMddHHmmss.jpg (giris) / PLAKA_X_yyyyMMddHHmmss.jpg (cikis)
    /// ±10 saniye tolerans icinde en yakin dosyayi dondurur.
    /// </summary>
    public static string FindLocalImageForRow(string plate, DateTime timestamp, bool isEntry)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";

        var cacheDir = @"C:\Otopark\ImageCache\";
        if (!System.IO.Directory.Exists(cacheDir)) return "";

        var safePlate = string.Concat(plate.Split(System.IO.Path.GetInvalidFileNameChars()));
        var prefix = isEntry ? "E" : "X";
        var pattern = $"{safePlate}_{prefix}_*.jpg";

        var candidates = System.IO.Directory.GetFiles(cacheDir, pattern);
        if (candidates.Length == 0) return "";

        string bestPath = "";
        double bestDelta = double.MaxValue;

        foreach (var f in candidates)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(f);
            var underIdx = name.LastIndexOf('_');
            if (underIdx < 0) continue;
            var tsStr = name.Substring(underIdx + 1);
            if (!DateTime.TryParseExact(tsStr, "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.None, out var fileTs))
                continue;

            var delta = Math.Abs((fileTs - timestamp).TotalSeconds);
            if (delta <= 10 && delta < bestDelta)
            {
                bestDelta = delta;
                bestPath = f;
            }
        }

        return bestPath;
    }

    // ===== YARDIMCI =====

    private static string ConvertPhotoPath(string? serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath)) return "";

        // Zaten http URL ise oldugu gibi don
        if (serverPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return serverPath;

        // Relative veya absolute sunucu yolunu web URL'sine cevir
        // \vehicleEntryPhoto\123.jpg -> http://web.belsoft.com.tr:221/vehicleEntryPhoto/123.jpg
        // C:\Parkomat\ParkomatWeb\wwwroot\vehicleEntryPhoto\123.jpg -> ayni sonuc
        var relative = serverPath.Replace('\\', '/').TrimStart('/');

        // wwwroot/ varsa ondan sonrasini al
        const string wwwroot = "wwwroot/";
        var idx = relative.IndexOf(wwwroot, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            relative = relative.Substring(idx + wwwroot.Length);

        return $"http://web.belsoft.com.tr:221/{relative}";
    }

    // ===== FILTRE =====

    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        // Zaman filtresi degistiyse API'den yeniden cek
        await LoadParkDataAsync();
    }

    private void ApplyFiltersInternal()
    {
        var filtered = _allVehicles.AsEnumerable();

        // Plaka filtresi
        if (!string.IsNullOrWhiteSpace(PlateSearchText))
        {
            var search = PlateSearchText.Trim().ToUpperInvariant();
            filtered = filtered.Where(v => v.Plate.ToUpperInvariant().Contains(search));
        }

        // Durum filtresi
        if (IsStatusAllInOut)
            filtered = filtered.Where(v => v.ParkType != "Iptal"); // Iptaller haric tumu
        else if (IsStatusApproved)
            filtered = filtered.Where(v => v.ExitDateTime != null && v.ParkType != "Iptal"); // Cikis yapilmis (onayli)
        else if (IsStatusUnapprovedOnly)
            filtered = filtered.Where(v => v.ExitDateTime == null && v.ParkType != "Iptal"); // Sadece girisi olan (cikis yok)
        else if (IsStatusUnapproved)
            filtered = filtered.Where(v => v.ExitDateTime == null && v.ParkType != "Iptal"); // Iceridekiler (alias)
        else if (IsStatusCancelled)
            filtered = filtered.Where(v => v.ParkType == "Iptal");
        else if (IsStatusBlacklist)
            filtered = filtered.Where(v => v.IsBlacklisted); // Kara liste: odenmemis borcu olan TUM araclar
        // IsStatusAll -> filtre yok (iptal dahil hepsi)

        // Mesai filtresi (API bugunun verisini dondurur, mesai saatine gore daralt).
        // ONAYLILAR / IPTALLER / KARA LISTE sekmelerinde UYGULANMAZ:
        //  - Kara liste zaten tarihten bagimsizdir (tum borclular),
        //  - Onaylilar ve Iptaller icin "cikisi yapilmis/iptal edilmis TUM kayitlar" istenir
        //    (dun girip bugun cikan arac mesai daraltmasinda kayboluyordu).
        if (IsTimeShift && !IsStatusBlacklist && !IsStatusCancelled && !IsStatusApproved)
        {
            var now = DateTime.Now;
            var shiftStart = now.Date.AddHours(now.Hour >= 8 ? 8 : -16);
            filtered = filtered.Where(v => v.EntryDateTime >= shiftStart);
        }

        VehicleList.Clear();
        foreach (var v in filtered)
            VehicleList.Add(v);
    }

    // ===== BARIYER TOAST =====

    public void ShowBarrierToast(bool success, string message)
    {
        ShowToast(message, success);
    }

    // ===== TOAST =====

    private async void ShowToast(string message, bool success)
    {
        ToastMessage = message;
        IsToastSuccess = success;
        IsToastVisible = true;
        await Task.Delay(3500);
        IsToastVisible = false;
    }

    // ===== LOGOUT =====

    [RelayCommand]
    private void Logout()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://web.belsoft.com.tr:221/") };
        var auth = new AuthApiService(http);
        var zone = new ZoneApiService(http);
        var loginVm = new LoginViewModel(auth, zone, _main);
        _main.Navigate(loginVm);
    }

    // ===== INNER CLASSES =====

    public partial class VehicleRow : ObservableObject
    {
        [ObservableProperty] private long entryId;
        [ObservableProperty] private string plate = "";
        [ObservableProperty] private string parkingName = "";
        [ObservableProperty] private string durationText = "";
        [ObservableProperty] private string parkType = "";
        [ObservableProperty] private DateTime entryDateTime = DateTime.Now;
        [ObservableProperty] private DateTime? exitDateTime;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBlacklisted))]
        private decimal oldDebt;
        [ObservableProperty] private decimal currentDebt;
        [ObservableProperty] private decimal totalDebt;
        [ObservableProperty] private decimal exitFee;   // Cikis tutari (hasilat icin) - yalnizca cikis yapan araclarda dolu
        [ObservableProperty] private string entryPlateImagePath = "";
        [ObservableProperty] private string exitPlateImagePath = "";

        // Arac Giris Turu (apiden gelir): A = Abone (yesil), N = Normal (sari)
        [ObservableProperty] private string entryType = "N";          // "A" veya "N"
        [ObservableProperty] private bool isSubscriber;               // true => A
        [ObservableProperty] private string subscriptionName = "";    // sadece abone ise dolu

        // Kara liste: gecmis (odenmemis) borcu olan arac. Tabloda satir arka plani siyah olur.
        public bool IsBlacklisted => OldDebt > 0m;

        // Anlik sure hesaplama
        public void UpdateDuration()
        {
            if (ExitDateTime.HasValue)
            {
                var span = ExitDateTime.Value - EntryDateTime;
                DurationText = $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
            }
            else
            {
                var span = DateTime.Now - EntryDateTime;
                DurationText = $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
            }
            TotalDebt = OldDebt + CurrentDebt;
        }
    }

    public sealed class ParkingItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}

public class PlateRow
{
    public string Plate { get; set; } = "";
    public string Status { get; set; } = "";
}
