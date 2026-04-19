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
    [ObservableProperty] private bool isStatusApproved = true;
    [ObservableProperty] private bool isStatusUnapproved;
    [ObservableProperty] private bool isStatusCancelled;
    [ObservableProperty] private bool isStatusAll;

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

    // Popup event: plaka kayitli degilse code-behind popup acar
    // string=plate, return: true=kayit yapildi, false=iptal
    public event Func<string, LookupApiService, Task<bool>>? OnVehicleRegistrationRequired;

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
            List<VewVehicleParkCurrentDto> data;

            if (IsTimeWeek)
            {
                data = await _parkQuery.GetByZoneAndDateRangeAsync(
                    UserSession.CompanyId, BolgeId,
                    DateTime.Now.AddDays(-7), DateTime.Now);
            }
            else if (IsTimeMonth)
            {
                data = await _parkQuery.GetByZoneAndDateRangeAsync(
                    UserSession.CompanyId, BolgeId,
                    DateTime.Now.AddDays(-30), DateTime.Now);
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
                    ParkType = d.ExitTimestamp.HasValue ? "Cikis" : "Giris",
                    EntryDateTime = d.EntryTimestamp,
                    ExitDateTime = d.ExitTimestamp,
                    EntryPlateImagePath = "",
                    OldDebt = (decimal)d.Balance,
                    CurrentDebt = (decimal)(d.CalculatedFee ?? 0),
                    TotalDebt = (decimal)(d.CurrentDebitAmount ?? 0),
                };
                _allVehicles.Add(row);

                // Resmi API'den cek (arka planda)
                if (d.EntryId > 0)
                    _ = LoadEntryImageAsync(row, d.EntryId);
            }

            UpdateParkCounts();
            ApplyFiltersInternal();
        }
        catch (Exception ex)
        {
            ShowToast("Veri yuklenemedi: " + ex.Message, false);
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
        CurrentVehicleCount = _allVehicles.Count(v => v.ExitDateTime == null);
        EmptyParkCount = Math.Max(0, TotalCapacity - CurrentVehicleCount);
        TotalRevenue = _allVehicles.Sum(v => v.CurrentDebt);
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

        try
        {
            // 1. Once plaka kayitli mi sorgula
            var vehicleCheck = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);

            if (vehicleCheck?.Result == null)
            {
                // Plaka kayitli degil - popup ac
                if (OnVehicleRegistrationRequired != null)
                {
                    var registered = await OnVehicleRegistrationRequired.Invoke(plate, _lookupApi);
                    if (!registered)
                    {
                        ShowToast("Arac kaydi iptal edildi.", false);
                        return;
                    }
                    // Kayit sonrasi verileri tekrar cek
                    vehicleCheck = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);
                }
                else
                {
                    ShowToast("Plaka sistemde kayitli degil.", false);
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
            };

            _allVehicles.Insert(0, row);
            UpdateParkCounts();
            ApplyFiltersInternal();

            // Giris basarili - tarife ucretini cek ve aracı borclandir
            if (entry?.Id > 0)
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
            var response = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, plate);

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
                    ? "Arac sorgulanamadi." : errorMsg, false);
                return;
            }

            var vehicle = response.Result;
            if (vehicle == null)
            {
                ShowToast("Arac bulunamadi.", false);
                return;
            }

            if (vehicle.Credit <= 0)
            {
                var existingRow = _allVehicles.FirstOrDefault(v =>
                    v.Plate == plate && v.ExitDateTime == null);

                // EntryId tabloda yoksa API'den cek
                long entryId = existingRow?.EntryId ?? 0;
                if (entryId == 0)
                {
                    try
                    {
                        var parkData = await _parkQuery.GetByZoneTodayAsync(UserSession.CompanyId, BolgeId);
                        var parkEntry = parkData.FirstOrDefault(p =>
                            p.Plate == plate && p.ExitTimestamp == null);
                        entryId = parkEntry?.EntryId ?? 0;
                    }
                    catch { }
                }

                if (entryId == 0)
                {
                    ShowToast("Arac giris kaydi bulunamadi.", false);
                    return;
                }

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
                        PaymentTypeId = 1, // NoPay - borcu yoksa odeme yapilmadan cikis
                        AmountCash = "0",
                        PaymentTime = DateTime.Now,
                        CompanyId = UserSession.CompanyId
                    }
                };

                var exitResponse = await _vehicleApi.AddExitAsync(exitReq);
                var json= JsonConvert.SerializeObject(exitReq);

                if (exitResponse?.Errors != null && exitResponse.Errors.Count > 0)
                {
                    var errorMsg = string.Join(", ", exitResponse.Errors
                        .Where(e => !string.IsNullOrEmpty(e.Message))
                        .Select(e => e.Message));
                    ShowToast(string.IsNullOrWhiteSpace(errorMsg)
                        ? "Cikis kaydedilemedi." : errorMsg, false);
                    return;
                }

                if (existingRow != null)
                {
                    existingRow.ExitDateTime = DateTime.Now;
                    existingRow.ExitPlateImagePath = GetFirstSnapshotPath(isEntry: false);
                    existingRow.ParkType = "Cikis";
                    existingRow.CurrentDebt = 0;
                    existingRow.TotalDebt = existingRow.OldDebt;
                }

                UpdateParkCounts();
                ApplyFiltersInternal();

                if (OnOpenExitGateRequested != null)
                    await OnOpenExitGateRequested.Invoke();

                ShowToast($"{plate} cikis kaydedildi. Bariyer aciliyor...", true);
                ExitDetectedPlate = "";
            }
            else
            {
                ShowToast($"{plate} borclu! ({vehicle.Credit:F2} TL) Kiosk cihazinda odemenizi gerceklestiriniz.", false);
            }
        }
        catch (Exception ex)
        {
            ShowToast("API Hatasi: " + ex.Message, false);
        }
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

    /// <summary>
    /// Plaka okundugunda View'in kaydettigi snapshot yollarinin ilkini dondurur (UI icin).
    /// Dosyalar zaten kaydedilmis oldugu icin burada sadece ilk yolu donduruyoruz.
    /// </summary>
    private string GetFirstSnapshotPath(bool isEntry)
    {
        var paths = isEntry ? EntryPlateSnapshotPaths : ExitPlateSnapshotPaths;
        return paths.Length > 0 ? paths[0] : "";
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
        if (IsStatusApproved)
            filtered = filtered; // Giris + cikis hepsi gelsin
        else if (IsStatusUnapproved)
            filtered = filtered.Where(v => v.ExitDateTime == null); // Sadece icerideki araclar
        else if (IsStatusCancelled)
            filtered = filtered.Where(v => v.ParkType == "Iptal");

        // Mesai filtresi (API bugunun verisini dondurur, mesai saatine gore daralt)
        if (IsTimeShift)
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
        [ObservableProperty] private decimal oldDebt;
        [ObservableProperty] private decimal currentDebt;
        [ObservableProperty] private decimal totalDebt;
        [ObservableProperty] private string entryPlateImagePath = "";
        [ObservableProperty] private string exitPlateImagePath = "";

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
