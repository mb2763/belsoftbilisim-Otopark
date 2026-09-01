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
    // Plaka tasinir: bariyer beklemesi plaka bazli uygulanir (bkz. BarrierService).
    public event Func<string?, Task>? OnOpenEntryGateRequested;
    public event Func<string?, Task>? OnOpenExitGateRequested;

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
        StartBarrierCommandPoll();   // madde 3: web'den gelen bariyer komutlari
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

    /// <summary>
    /// UZAKTAN BARIYER KOMUTU YOKLAMASI (01.09.2026 - madde 3).
    ///
    /// Web'deki Plaka Revizyonu ekranindan bariyer acilmak istendiginde komut
    /// sunucuda bir kuyruga birakilir; BU DONGU onu alip kendi agindan uygular.
    ///
    /// NEDEN BOYLE: bariyer, kameranin yerel agdaki IO cikisindan tetikleniyor
    /// (PUT http://{kamera-IP}/ISAPI/System/IO/outputs/..). Web sunucusu farkli
    /// agda oldugu icin o adrese ULASAMAZ. Komutu otoparktaki bu istemci uygular.
    ///
    /// YUK: ucun kendisi TAMAMEN BELLEKTEN okunuyor, veritabanina hic gitmiyor.
    /// Bu yuzden 3 saniyelik yoklama sunucuya anlamli bir maliyet getirmez.
    /// (Mobildeki 3 sn'lik yoklama sorun cikarmisti; oradaki uc her cagrida
    /// arac basina tarife sorgusu yapiyordu, buradaki ise sadece kuyruk okur.)
    ///
    /// GUVENLIK: komutun omru sunucuda 60 sn. Gec alinan komut uygulanmaz;
    /// saatler sonra kendiliginden acilan bariyer olmaz.
    /// </summary>
    private async void StartBarrierCommandPoll()
    {
        while (true)
        {
            await Task.Delay(3000);

            try
            {
                if (BolgeId == 0 || UserSession.CompanyId == 0) continue;

                var komutlar = await _parkQuery.GetPendingBarrierCommandsAsync(UserSession.CompanyId, BolgeId);
                if (komutlar == null || komutlar.Count == 0) continue;

                foreach (var k in komutlar)
                {
                    bool girisMi = string.Equals(k.Gate, "giris", StringComparison.OrdinalIgnoreCase);

                    // Bariyer komutu, ekrandan elle basilmis gibi calisir:
                    // bekleme uygulanmaz (personel bilerek talep etti).
                    if (girisMi && OnOpenEntryGateRequested != null)
                        await OnOpenEntryGateRequested.Invoke(k.Plate);
                    else if (!girisMi && OnOpenExitGateRequested != null)
                        await OnOpenExitGateRequested.Invoke(k.Plate);

                    ShowToast($"Web'den {(girisMi ? "giris" : "cikis")} bariyeri acma talebi uygulandi" +
                              (string.IsNullOrWhiteSpace(k.Plate) ? "." : $" ({k.Plate})."), true);
                }
            }
            catch
            {
                // Yoklama basarisizsa SESSIZ gecilir: bariyer acilmaz, baska
                // hicbir yan etki olmaz. Bir sonraki turda tekrar denenir.
            }
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
                // UpdateParkCounts sayaci YEREL (bugun-only) listeden yeniden yazar;
                // sunucudan gelen gercek dolulugu ezmemesi icin hemen tazelenir.
                await RefreshOccupancyAsync();
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
            // Engelli arac tipleri bir kez yuklenir; listede "(E)" isareti icin.
            await EnsureEngelliTipleriAsync();

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
                    // BORC KOLONLARI GERCEK BORCU GOSTERIR (19.08.2026).
                    //
                    // Onceki eslesme yanlis alanlari okuyordu:
                    //   OldDebt   = d.Balance            -> Balance ON ODEME bakiyesidir, BORC DEGIL
                    //   CurrentDebt = d.CalculatedFee    -> CIKISTA hesaplanir; arac iceride iken 0
                    //   TotalDebt = d.CurrentDebitAmount -> pratikte dolmuyor
                    // Sonuc: iceride duran borclu aracta ucc kolon da 0.00 gorunuyordu.
                    // (Olculdu: 38ARA411'in VEHICLE_DEFINITION.CREDIT = 3 iken ekran 0.00 diyordu.)
                    //
                    // Aracin ACIK BORC toplami VEHICLE_DEFINITION.CREDIT alanindadir ve
                    // DTO'da "Credit" olarak zaten geliyor. TOPLAM artik ondan okunur.
                    // ANLIK yalnizca cikis yapilmis satirda anlamlidir (o an hesaplanan
                    // ucret); iceride duran araclarda ESKI = toplam acik borctur.
                    OldDebt = (decimal)Math.Max(0, d.Credit - (d.CalculatedFee ?? 0)),
                    CurrentDebt = (decimal)(d.CalculatedFee ?? 0),
                    TotalDebt = (decimal)d.Credit,
                    // Cikis yapilmissa cikis tutari hasilata sayilir; icerideki araclarda 0.
                    ExitFee = d.ExitTimestamp.HasValue ? (decimal)(d.CalculatedFee ?? 0) : 0m,
                    VehicleTypeId = d.VehicleTypeId,
                    EngelliMi = _engelliVehicleTypeIds.Contains(d.VehicleTypeId),
                };

                // Lokal cache'te plaka+timestamp ile esles
                row.EntryPlateImagePath = FindLocalImageForRow(row.Plate, d.EntryTimestamp, isEntry: true);
                if (d.ExitTimestamp.HasValue)
                    row.ExitPlateImagePath = CikisGorseliBul(row.Plate, d.ExitTimestamp.Value);

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

            // Doluluk sayaci SUNUCUDAN tazelenir: yukaridaki yerel hesap yalnizca bugunun
            // listesini gordugu icin dun girip hala iceride olan araclari kacirir.
            await RefreshOccupancyAsync();
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

    /// <summary>
    /// Cikis anindaki plaka fotografini base64 olarak dondurur (yoksa null).
    ///
    /// TEK YERDE tutulur cunku cikis IKI ayri yoldan yapiliyor:
    ///   1) DoApproveExitAsync        - normal (odemeli) cikis
    ///   2) BorcluCikisYapAsync       - "Borclu Cikisi Yap" butonu (kuyruk durumu)
    /// Eskiden yalnizca 1. yol foto gonderiyordu; borclu cikislarda web "Plaka Revizyon"
    /// ekraninda hala GIRIS fotografi gorunuyordu.
    ///
    /// Okunamazsa null doner ve CIKIS ENGELLENMEZ — bariyer acilmali.
    /// </summary>
    private string? CikisFotografiBase64()
    {
        try
        {
            var yol = GetFirstSnapshotPath(isEntry: false);
            if (string.IsNullOrWhiteSpace(yol) || !System.IO.File.Exists(yol)) return null;
            return $"data:image/jpg;base64,{Convert.ToBase64String(System.IO.File.ReadAllBytes(yol))}";
        }
        catch
        {
            return null;   // foto okunamadi -> cikis normal devam eder
        }
    }

    /// <summary>
    /// "Bos/Dolu" sayacini SUNUCUDAN tazeler — tarihten bagimsiz gercek doluluk.
    ///
    /// NEDEN GEREKLI: UpdateParkCounts sayimi _allVehicles uzerinden yapar; o liste
    /// varsayilan olarak yalnizca BUGUNUN kayitlarini icerir (GetByZoneTodayAsync).
    /// Bu yuzden DUN girip hala iceride olan araclar doluluga yansimiyordu — 100 araclik
    /// otoparkta gece kalan 5 arac varken sabah "0 dolu / 100 bos" gorunuyordu.
    ///
    /// Sorgu basarisiz olursa (-1) yerel hesap OLDUGU GIBI birakilir; sayac sifirlanmaz.
    /// </summary>
    /// <summary>
    /// BARIYER ACILDI ama CIKIS KAYDI OLUSMADI vakasini diske yazar (01.09.2026).
    ///
    /// Madde 4-b geregi bariyer artik sunucu onayini BEKLEMEDEN aciliyor.
    /// Bunun bedeli, sunucu yazamadiginda aracin cikmis ama kaydinin olusmamis
    /// olmasi. Ekrandaki uyari personel "Tamam"a basinca kaybolur; vaka geriye
    /// donuk bulunabilsin diye ayrica dosyaya yazilir.
    ///
    /// Dosya: %LOCALAPPDATA%\Otopark\cikis_kurtarma.txt
    /// Yazma HICBIR sekilde akisi bozmaz (try/catch): kurtarma notu kozmetiktir,
    /// asil islemi dusuremez.
    /// </summary>
    private static void CikisKurtarmaNotu(string plate, long entryId, string? sebep)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otopark");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "cikis_kurtarma.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BARIYER ACILDI, CIKIS KAYDI YOK | " +
                $"plaka={plate} entryId={entryId} sebep={sebep ?? "-"}" + Environment.NewLine);
        }
        catch { /* not yazilamadi - islem etkilenmez */ }
    }

    public async Task RefreshOccupancyAsync()
    {
        if (BolgeId == 0) return;

        // Kara liste / Iptaller sekmelerinde sayac zaten guncellenmiyor (bkz. UpdateParkCounts).
        if (IsStatusBlacklist || IsStatusCancelled) return;

        try
        {
            // ORTAK UC (27.08.2026): kapasite de, icerideki arac da web panosuyla
            // AYNI hesaptan gelir (sunucu: ZoneManager.GetParkOccupancy).
            // Iki ekranin farkli rakam gostermesinin sebebi kapasitenin ayri
            // hesaplanmasiydi; artik tek kaynak var.
            var doluluk = await _parkQuery.GetParkOccupancyAsync(UserSession.CompanyId, BolgeId);
            if (doluluk != null)
            {
                // Kapasite 0 donerse bolge tanimi eksiktir; yerel deger korunur ki
                // "Bos" sayaci aniden sifirlanmasin.
                if (doluluk.TotalParkCapacity > 0)
                    TotalCapacity = doluluk.TotalParkCapacity;

                CurrentVehicleCount = doluluk.VehicleParkingCount;
                EmptyParkCount = Math.Max(0, TotalCapacity - CurrentVehicleCount);
                return;
            }

            // YEDEK YOL: ortak uc yoksa (sunucu henuz guncellenmemis) eski sayim.
            var count = await _parkQuery.GetCurrentParkedCountByZoneAsync(
                UserSession.CompanyId, UserSession.UserId, BolgeId);

            if (count < 0) return;   // alinamadi -> yerel hesap korunur

            CurrentVehicleCount = count;
            EmptyParkCount = Math.Max(0, TotalCapacity - count);
        }
        catch { /* sayac yerel degeriyle kalir */ }
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

        // BOLGESIZ ISLEM YOK: BolgeId = 0 ile yazilan borc hicbir bolgeye
        // eslesmez ve cikista borc kontrolu fiilen kapanir. Girisi bastan
        // engellemek, sonradan duzeltilemeyen kayit uretmekten iyidir.
        if (!BolgeGecerliMi()) return;

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
                // BolgeId gonderilir: kapali otoparkta SADECE kapali otopark aboneligi gecerli
                var capSubResp = await _vehicleApi.CheckSubscriptionAsync(plate, UserSession.CompanyId, BolgeId);
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
                // SUNUCU KAYIT OLUSTURMADI. Once sunu sor: bu plakanin ZATEN acik
                // bir girisi var mi? (Sunucu "mukerrer giris" gordugunde yeni kayit
                // uretmiyor ve 28.08.2026'dan itibaren acik hata donuyor.)
                //
                // Varsa arac fiziksel olarak kapida bekliyordur; YENI KAYIT
                // URETMEDEN mevcut girisi kullanip bariyeri acmak dogrudur.
                // Yoksa gercek bir hatadir: bariyer ACILMAZ.
                long zatenVarId = await MevcutAcikGirisBulAsync(plate);

                if (zatenVarId > 0)
                {
                    ShowToast($"{plate}: Bu arac zaten kayitli (giris #{zatenVarId}). " +
                              "Yeni kayit olusturulmadi, bariyer aciliyor.", true);
                    BariyeriHemenAc(plate);
                    await LoadParkDataAsync();

                    EntryDetectedPlate = "";
                    _entryPendingPhotoBase64 = "";
                    return;
                }

                var errorMsg = string.Join(", ", response.Errors
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .Select(e => e.Message));
                ShowToast(string.IsNullOrWhiteSpace(errorMsg)
                    ? "Giris kaydedilemedi." : errorMsg, false);
                return;
            }

            // ===== SONUCSUZ YANIT BASARI SAYILMAZ (28.08.2026 - saha vakasi) =====
            //
            // Sunucu, ayni plaka icin yakin zamanli bir giris bulunca mukerrer kabul
            // edip ISLEM YAPMADAN donuyordu. Eski surumlerde bu yanit "Errors = [],
            // Result = null, HTTP 200" seklindeydi; yani yukaridaki iki kontrol de
            // gecti ve akis BASARILI gibi devam etti:
            //   - bariyer acildi,
            //   - satir listeye EntryId = 0 ile eklendi (arac "iceride" gorundu),
            //   - `entry?.Id > 0` FALSE oldugu icin BORC YAZILMADI,
            //   - personele "giris kaydedildi" dendi.
            // Sonra cikista o giris bulunamiyor, sistem hayalet giris uretmeye
            // calisiyor ve BARIYER ACILMIYORDU. Sahadaki sikayet tam olarak buydu.
            //
            // Sunucu artik acik hata donuyor; yine de eski surum sunuculara karsi
            // calisabilmek icin burada da koruma var: Result null ise MEVCUT acik
            // giris plakadan aranir.
            //   - BULUNURSA  : yeni kayit URETILMEZ, o girisin kimligi kullanilir
            //                  (arac zaten iceride, bariyer acilmali - fiziksel
            //                   olarak kapida bekliyor).
            //   - BULUNAMAZSA: bariyer ACILMAZ, satir eklenmez, personele gercek
            //                  durum soylenir. Yalan "kaydedildi" mesaji verilmez.
            var entry = response.Result;

            if (entry == null)
            {
                long mevcutGirisId = await MevcutAcikGirisBulAsync(plate);

                if (mevcutGirisId <= 0)
                {
                    ShowToast($"{plate}: Giris KAYDEDILEMEDI (sunucu kayit olusturmadi). " +
                              "Bariyer acilmadi, lutfen tekrar deneyin.", false);
                    EntryDetectedPlate = "";
                    _entryPendingPhotoBase64 = "";
                    return;
                }

                ShowToast($"{plate}: Bu arac zaten kayitli (giris #{mevcutGirisId}). " +
                          "Yeni kayit olusturulmadi, bariyer aciliyor.", true);

                // Listeyi sunucudan tazele: mevcut giris satiri ekrana gelsin.
                BariyeriHemenAc(plate);
                await LoadParkDataAsync();

                EntryDetectedPlate = "";
                _entryPendingPhotoBase64 = "";
                return;
            }
            var vehDef = entry?.VehicleDefinition;

            // ===== BARIYER: GIRIS SUNUCUYA DUSER DUSMEZ AC =====
            // Bariyer tetigi eskiden bu metodun EN SONUNDAYDI. Arada dort sunucu
            // gidis-donusu vardi: doluluk yenileme, abonelik sorgusu, tarife ucreti,
            // borclandirma. Uzak sunucuda bunlarin toplami saniyeleri buluyor ve
            // arac bariyer onunde bekliyordu ("gec tetik").
            // Artik giris kaydi olustugu ANDA tetik gider; geri kalan islemler
            // (doluluk/abonelik/borc) arkasindan devam eder.
            BariyeriHemenAc(vehDef?.Plate);

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
            await RefreshOccupancyAsync();   // doluluk sunucudan (dun kalan araclar dahil)

            // ===== ARACIN ABONELIK DURUMUNU API'DEN KONTROL ET =====
            // A (abone, yesil) ya da N (normal, sari) badge'i + abonelik turu
            try
            {
                // BolgeId gonderilir: yol kenari aboneligi kapali otoparkta "A" (abone) gosterilmez
                var subResp = await _vehicleApi.CheckSubscriptionAsync(row.Plate, UserSession.CompanyId, BolgeId);
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
                            VehicleExitId = 0,
                            // GIRIS BAGI: sunucudaki gun basina tahakkuk gorevi bu borcu
                            // "1. gun" olarak sayabilsin. Gonderilmezse gorev ayni gunu
                            // IKINCI KEZ yazar (arac hem giriste hem tahakkukta borclanir).
                            VehicleEntryId = entry.Id
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

            // NOT: Bariyer burada DEGIL, giris kaydi olusur olusmaz yukarida acilir.

            EntryDetectedPlate = "";
            _entryPendingPhotoBase64 = "";
        }
        catch (Exception ex)
        {
            ShowToast("API Hatasi: " + ex.Message, false);
        }
    }

    /// <summary>
    /// Giris bariyerini BEKLEMEDEN acar (fire-and-forget).
    ///
    /// Neden beklemiyoruz: cagiran metot bariyerin HTTP yanitini beklerse, kamera
    /// yavas cevap verdiginde (ya da zaman asimina ugradiginda - 5 sn) giris akisi
    /// o kadar sure durur. Tersi de gecerli: tetik, cagiran metodun kalan sunucu
    /// isleriyle geriye kayar. Ikisini de ayirmak icin komut ayri bir gorevde gider.
    ///
    /// Havuz is parcacigi kullanilir (Task.Run): arayuz is parcacigi yeni satiri
    /// cizerken mesgul olsa bile tetik beklemez.
    /// Hata yutulur - bariyer acilamasa da giris kaydi gecerlidir; sonucu personel
    /// olay isleyicisinin gosterdigi toast'ta gorur.
    /// </summary>
    private void BariyeriHemenAc(string? plaka = null)
    {
        var handler = OnOpenEntryGateRequested;
        if (handler == null) return;

        _ = Task.Run(async () =>
        {
            try { await handler.Invoke(plaka); }
            catch { /* bariyer hatasi girisi bozmaz */ }
        });
    }

    [RelayCommand]
    private async Task ApproveEntryAsync() => await DoApproveEntryAsync();

    /// <summary>Kuyruga alinmis GIRIS islemleri icin kilit.</summary>
    private readonly SemaphoreSlim _girisKilidi = new SemaphoreSlim(1, 1);
    private int _girisKuyrugu = 0;

    /// <summary>
    /// ART ARDA GELEN ARACLARDA GIRIS (25.08.2026).
    ///
    /// Cikis tarafiyla AYNI kusur: otomatik onay,
    /// ApproveEntryCommand.CanExecute(null) false ise plakayi SESSIZCE dusuruyordu.
    /// AsyncRelayCommand es zamanli calismaya izin vermedigi icin, onceki aracin
    /// girisi islenirken bu kosul her zaman false oluyordu.
    ///
    /// GIRISTE SONUCU DAHA AGIR: kayit hic olusmaz, dolayisiyla borc da YAZILMAZ.
    /// Arac icerideyken sistemde gorunmez ve cikista "girisi yok" muamelesi gorur;
    /// ucret tahsil edilemez. Bu yuzden cikisla ayni sekilde SIRAYA ALINIYOR.
    /// </summary>
    public async Task GirisiSirayaAlAsync(string plate, string photoBase64)
    {
        const int MAX_BEKLEYEN = 3;
        const int BEKLEME_SANIYE = 45;

        if (_girisKuyrugu >= MAX_BEKLEYEN)
        {
            ShowToast($"{plate}: giris kuyrugu dolu, islem yapilamadi. Lutfen tekrar okutunuz.", false);
            return;
        }

        System.Threading.Interlocked.Increment(ref _girisKuyrugu);
        try
        {
            if (!await _girisKilidi.WaitAsync(TimeSpan.FromSeconds(BEKLEME_SANIYE)))
            {
                ShowToast($"{plate}: onceki islem uzun surdu, giris yapilamadi. Tekrar okutunuz.", false);
                return;
            }

            try
            {
                // Kuyrukta beklerken baska bir okuma bu alanlari degistirmis olabilir;
                // kendi degerlerimizle YENIDEN kuruyoruz.
                SetPendingEntry(plate, photoBase64);
                await DoApproveEntryAsync();
            }
            finally
            {
                _girisKilidi.Release();
            }
        }
        finally
        {
            System.Threading.Interlocked.Decrement(ref _girisKuyrugu);
        }
    }

    /// <summary>
    /// BU PLAKANIN BU BOLGEDE ZATEN ACIK BIR GIRISI VAR MI? (28.08.2026)
    ///
    /// Sunucu ayni plaka icin yakin zamanli bir giris bulunca yeni kayit
    /// OLUSTURMUYOR. Boyle bir durumda arac fiziksel olarak kapida bekliyor
    /// olabilir; yeni kayit uretmeden MEVCUT girisin kimligini bulup bariyeri
    /// acmak dogru davranistir.
    ///
    /// Sorgu TARIHTEN BAGIMSIZ calisir (ExitId == null), cunku giris dun
    /// yapilmis olabilir. Bolgeye gore suzulur: baska bir otoparkin acik
    /// girisi bu bolgenin araci sayilmamalidir.
    ///
    /// Bulunamazsa ya da sorgu basarisiz olursa 0 doner.
    /// </summary>
    private async Task<long> MevcutAcikGirisBulAsync(string plate)
    {
        try
        {
            var acikGirisler = await _parkQuery.GetOpenParkByPlateAsync(
                UserSession.CompanyId, UserSession.UserId, plate);

            var buBolge = acikGirisler
                .Where(p => p.EntryZoneId == BolgeId
                            && p.ExitTimestamp == null
                            && PlakaAyniMi(p.Plate ?? "", plate))
                .OrderByDescending(p => p.EntryId)
                .FirstOrDefault();

            return buBolge?.EntryId ?? 0;
        }
        catch
        {
            return 0;
        }
    }

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

        // ===== ONCEKI ARACIN FOTOGRAFI DEVRALINMASIN (28.08.2026) =====
        //
        // _entryPendingPhotoBase64 bosaltiliyordu ama EntryPlateSnapshotPaths
        // BOSALTILMIYORDU. O dizi yalnizca KAMERA okumasinda doluyor
        // (PersonnelDashboardView.xaml.cs) ve hicbir yerde temizlenmiyor.
        //
        // Sonuc: manuel "Iceri Al" ile eklenen satir, DoApproveEntryAsync icindeki
        // GetFirstSnapshotPath(isEntry: true) uzerinden EN SON KAMERAYA OKUNAN
        // ARACIN fotografini gosteriyordu. Yani ekranda yanlis aracin resmi
        // goruluyordu - fotografin hic olmamasindan daha kotu.
        //
        // Manuel giriste gorsel YOKTUR: ikisi de bosaltilir, satir "Fotograf yok"
        // yer tutucusuyla cizilir.
        _entryPendingPhotoBase64 = "";
        EntryPlateSnapshotPaths = Array.Empty<string>();

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
    // ENGELLI ARAC TIPLERI (18.08.2026).
    // Engelli arac tipine ayri tarife tanimlanabiliyor (orn. HUNAT: 0-930 dk = 0 TL,
    // 930-1440 dk = 80 TL). Personel listede bu araclari ayirt edebilsin diye plaka
    // yaninda "(E)" gosterilir. Tip kimlikleri sabit degil; ada gore bulunur.
    private HashSet<long> _engelliVehicleTypeIds = new HashSet<long>();
    private bool _engelliTipleriYuklendi;

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
    /// <summary>
    /// Engelli arac tiplerinin kimliklerini bir kez yukler.
    ///
    /// Ad esletmesi "ENGELL" on eki ile yapilir: Turkce buyuk-kucuk donusumunde
    /// "İ" harfi kulturden kulture farkli davrandigi icin (I / i / İ / ı) o harfe
    /// hic dokunulmaz. Boylece "ENGELLİ", "Engelli", "ENGELLI" hepsi yakalanir.
    /// </summary>
    private async Task EnsureEngelliTipleriAsync()
    {
        if (_engelliTipleriYuklendi) return;
        try
        {
            var types = await _lookupApi.GetVehicleTypesAsync(UserSession.CompanyId);
            _engelliVehicleTypeIds = types
                .Where(t => t.VehicleTypeName != null &&
                            t.VehicleTypeName.Contains("ENGELL", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Id)
                .ToHashSet();
            _engelliTipleriYuklendi = true;
        }
        catch
        {
            // Alinamazsa isaret gosterilmez; liste normal calisir. Sonraki
            // tazelemede tekrar denenir.
        }
    }

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

    /// <summary>
    /// Iptal nedeni ister (View bir giris penceresi acar). null/bos donerse islem iptal edilir.
    /// </summary>
    public event Func<string, bool, Task<string?>>? OnCancelReasonRequired;

    [RelayCommand]
    private async Task CancelEntryAsync(VehicleRow? row)
    {
        if (row == null) return;
        if (row.EntryId <= 0) { ShowToast("Giris Id bulunamadi.", false); return; }
        if (row.ParkType == "Iptal") { ShowToast("Bu kayit zaten iptal edilmis.", false); return; }

        // CIKISI YAPILMIS kayitlar da iptal edilebilir (web Plaka Revizyon ile ayni davranis).
        bool cikisVar = row.ExitDateTime != null;

        // Iptal nedeni ZORUNLU: sunucu tarafi da bos nedeni reddediyor.
        string? reason = OnCancelReasonRequired != null
            ? await OnCancelReasonRequired.Invoke(row.Plate ?? "", cikisVar)
            : null;

        if (string.IsNullOrWhiteSpace(reason))
        {
            // Neden penceresi yoksa (eski View) ya da kullanici vazgectiyse.
            if (OnCancelReasonRequired == null)
                ShowToast("Iptal nedeni alinamadi.", false);
            return;
        }

        try
        {
            // TAM IPTAL ucu: giris + (varsa) cikis + borclar + CREDIT + ACIKLAMA
            var resp = await _vehicleApi.CancelEntryAsync(
                row.EntryId, UserSession.CompanyId, UserSession.UserId, reason.Trim());

            if (resp == null)
            {
                ShowToast("Iptal DOGRULANAMADI (sunucudan yanit alinamadi).", false);
                return;
            }
            if (resp.Errors != null && resp.Errors.Count > 0)
            {
                var msg = string.Join(", ", resp.Errors.Where(e => !string.IsNullOrEmpty(e.Message)).Select(e => e.Message));
                ShowToast(string.IsNullOrWhiteSpace(msg) ? "Iptal basarisiz." : msg, false);
                return;
            }

            row.ParkType = "Iptal";
            UpdateParkCounts();
            ApplyFiltersInternal();
            await RefreshOccupancyAsync();   // iptal edilen giris dolulugu degistirir
            ShowToast($"{row.Plate} kaydi iptal edildi" + (cikisVar ? " (giris + cikis + borc)." : "."), true);

            // Sunucudan yeniden oku: ekran DB'nin gercek halini gostersin.
            try { await LoadParkDataAsync(); } catch { }
        }
        catch (Exception ex)
        {
            ShowToast("Iptal hatasi: " + ex.Message, false);
        }
    }

    /// <summary>
    /// Plakayi backend'de gunceller (web Plaka Revizyon ekraniyla AYNI uc: UpdateVehicleParkEntryPlate).
    ///
    /// Sunucu tarafi artik:
    ///   - Yeni plaka sistemde YOKSA araci OTOMATIK kaydeder (tarife/tip eski aractan kopyalanir),
    ///     yani "once araci kaydediniz" hatasi normalde artik olusmaz.
    ///   - SADECE bu girise ait borclarin VEHICLE_DEFINITION_ID'sini yeni araca tasir
    ///     (cikis varsa VEHICLE_EXIT_ID'ye, yoksa giris borcuna gore eslesir).
    ///   - Acik borc tasindiysa CREDIT bakiyesini eski aractan dusup yeni araca ekler.
    /// Asagidaki "kayitli degil -> popup" yolu geriye donuk uyum icin BIRAKILDI: eski bir API
    /// surumune baglanildiginda hala calisir.
    ///
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

            // KRITIK: resp == null (HTTP hatasi ya da deserialize basarisiz) durumu ONCEDEN "hata yok"
            // sayiliyordu -> kullaniciya yesil "Plaka duzeltildi" gosteriliyor ama DB'de hicbir sey
            // degismiyordu. Program yeniden acilinca eski plaka geri geliyordu.
            if (resp == null)
            {
                ShowToast("Guncelleme DOGRULANAMADI (sunucudan yanit alinamadi). Plaka degistirilmedi.", false);
                return false;
            }

            // Yeni plaka kayitli degilse backend hata doner -> popup ile kayit yaptiralim
            if (resp.Errors != null && resp.Errors.Count > 0)
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
                    if (resp == null)
                    {
                        ShowToast("Guncelleme DOGRULANAMADI (sunucudan yanit alinamadi). Plaka degistirilmedi.", false);
                        return false;
                    }
                    if (resp.Errors != null && resp.Errors.Count > 0)
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

            // Sunucudan yeniden oku: ekranda gorulen plaka artik DB'nin gercek hali olsun.
            // (Eskiden sadece bellekteki satir degistiriliyordu; yazma basarisiz olsa bile
            //  ekranda dogru gorunuyor, ilk yenilemede eski plaka geri geliyordu.)
            try { await LoadParkDataAsync(); } catch { /* yenileme basarisizsa toast zaten verildi */ }
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

        // BOLGESIZ ISLEM YOK (bkz. DoApproveEntryAsync). Bolge bilinmeden
        // "bu bolgeye ait borc" hesaplanamaz; borclu arac serbest gecerdi.
        if (!BolgeGecerliMi()) return;

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
                PlakaAyniMi(v.Plate, plate) &&
                v.ExitDateTime == null && v.ParkType != "Iptal");

            long entryId = existingRow?.EntryId ?? 0;
            if (entryId == 0)
            {
                try
                {
                    var parkData = await _parkQuery.GetByZoneTodayAsync(UserSession.CompanyId, BolgeId);
                    var parkEntry = parkData.FirstOrDefault(p =>
                        PlakaAyniMi(p.Plate, plate) &&
                        p.ExitTimestamp == null);
                    entryId = parkEntry?.EntryId ?? 0;
                }
                catch { }
            }

            // ===== UCUNCU ARAMA: TARIHTEN BAGIMSIZ (28.08.2026 - saha vakasi) =====
            //
            // Yukaridaki IKI arama da YALNIZCA BUGUNU tariyor:
            //   1) _allVehicles          -> LoadParkDataAsync ile GetByZoneTodayAsync'ten dolar
            //   2) GetByZoneTodayAsync   -> sunucuda "EntryTimestamp >= today && < tomorrow"
            //
            // Bu yuzden DUN girip BUGUN cikan arac (gece kalanlar) ve gun donumu
            // civarindaki kayitlar bulunamiyordu. Bulunamayinca akis 15 dk oncesine
            // HAYALET GIRIS + YENI BORC uretmeye calisiyor; vatandas ESKI borcunu
            // odemis olsa bile bu yeni borc acik oldugu icin bariyer ACILMIYORDU.
            //
            // GetOpenParkByPlateAsync tarih filtresi ICERMEZ (ExitId == null ile
            // "hala iceride" olan kaydi bulur). Kiosk da ayni ucu kullaniyor.
            // Bolgeye gore suzuluyor: baska bir otoparkin acik girisi ALINMAZ.
            if (entryId == 0)
                entryId = await MevcutAcikGirisBulAsync(plate);

            // 3-ONCESI: BU ARAC AZ ONCE ZATEN CIKIS YAPMIS MI? (21.08.2026)
            //
            // Kuyrukta bekleyen aracin plakasi, one gecen arac cikarken kamera
            // tarafindan ERKEN okunabiliyor. Cikis o anda islenip kaydediliyor;
            // arac bariyere geldiginde plaka IKINCI kez okunuyor. Bu ikinci
            // okumada artik acik giris YOK ve asagidaki blok devreye girip
            // 15 dk oncesine HAYALET BIR GIRIS + YENI BORC uretiyordu. Personel
            // "cikis kaydedilmedi, bariyer de acilmadi" diye goruyordu.
            //
            // Artik: yakin zamanda cikisi yapilmis arac icin YENI KAYIT URETILMEZ,
            // yalnizca bariyer TEKRAR ACILIR.
            // Cikisi yapilmis arac icin bariyer karari ZAMANA degil BORCA bakar.
            // Pencere yalnizca akil saglama sinirIdir: saatler once cikmis bir arac
            // girissiz sekilde bariyerde duruyorsa bu "tekrar okuma" degildir, asagidaki
            // otomatik giris akisina dusmelidir.
            const int TEKRAR_OKUMA_DK = 30;

            if (entryId == 0)
            {
                var sonCikis = _allVehicles
                    .Where(v => PlakaAyniMi(v.Plate, plate)
                                && v.ExitDateTime != null)
                    .OrderByDescending(v => v.ExitDateTime)
                    .FirstOrDefault();

                bool cikisiYapilmis = sonCikis?.ExitDateTime != null &&
                                      (DateTime.Now - sonCikis.ExitDateTime.Value).TotalMinutes <= TEKRAR_OKUMA_DK;

                if (cikisiYapilmis)
                {
                    // CIKIS ZATEN VAR -> tek soru kalir: BORCU KAPALI MI?
                    // Kayit uretilmez (hayalet giris + sahte borc olusuyordu).
                    // Arac bariyerde bekledigi surece plaka tekrar tekrar okunabilir;
                    // her okumada borc YENIDEN sorulur, tahsilat arada tamamlanirsa
                    // ikinci ya da ucuncu denemede bariyer acilir.
                    var borcDurumu = await GetVehicleDebtsAsync(vehicle.Id);

                    if (!borcDurumu.basarili)
                    {
                        // Borc sorgusu basarisizsa "borcsuz" VARSAYILMAZ (fail-closed).
                        ShowToast($"{plate}: cikisi yapilmis ancak borc sorgulanamadi. " +
                                  "Bariyer acilmadi, tekrar deneyiniz.", false);
                        return;
                    }

                    if (borcDurumu.zoneDebt > 0)
                    {
                        // YIKAMA ISTISNASI (24.08.2026) - normal cikis yolundaki kuralin ayni si.
                        //
                        // Sahadan gelen sikayet: yikama fisi basilmis arac bariyerin onunde
                        // kaliyordu. Sebep: yikama bypass'li cikista giriste yazilan otopark
                        // borcu KAPATILMIYOR; arac bariyerde beklerken plaka ikinci kez
                        // okununca bu dala dusuluyor ve acik borc yuzunden bariyer acilmiyordu.
                        // Normal cikis yolunda yikama istisnasi VARDI, burada YOKTU.
                        //
                        // Sorgu YALNIZCA borc varken yapilir: borcsuz tekrar okumada
                        // fazladan istek olmaz, bariyer eskisi gibi aninda acilir.
                        // Yikama durumu alinamazsa istisna UYGULANMAZ (fail-closed),
                        // yani borclu arac bedava cikamaz.
                        bool yikamaIleGecsin = false;
                        if (sonCikis!.EntryId > 0)
                        {
                            try
                            {
                                var yikamaDurumu = await _vehicleApi.GetWashStatusAsync(
                                    UserSession.CompanyId, sonCikis.EntryId);
                                yikamaIleGecsin = yikamaDurumu.HasWashReceipt && !yikamaDurumu.IsExpired;
                            }
                            catch { /* yikama durumu alinamazsa normal borc kontrolu calisir */ }
                        }

                        if (!yikamaIleGecsin)
                        {
                            ShowToast($"{plate}: cikisi yapilmis fakat {borcDurumu.zoneDebt:F2} TL borcu var. " +
                                      "Odeme yapildiktan sonra bariyer acilacak.", false);
                            return;
                        }
                    }

                    ShowToast($"{plate}: cikisi zaten kaydedilmis " +
                              $"({sonCikis!.ExitDateTime!.Value:HH:mm:ss}) ve borcu yok. Bariyer aciliyor.", true);

                    if (OnOpenExitGateRequested != null)
                        await OnOpenExitGateRequested.Invoke(plate);

                    return;
                }
            }

            // 3. Giris yoksa OTOMATIK GIRIS olustur (15 dk oncesine).
            if (entryId == 0)
            {
                ShowToast($"{plate} icin giris kaydi yok, 15 dk oncesine otomatik giris olusturuluyor.", true);
                entryId = await CreateAutoBackdatedEntryAsync(plate, vehicle);
                if (entryId == 0) { ShowToast("Otomatik giris olusturulamadi.", false); return; }

                // 3b. GIRISI OLMAYAN ARAC -> ONCE BORCLANDIR, BARIYERI ACMA.
                // Onceden bu yolda hicbir borc olusmuyordu: adim 4 yalnizca MEVCUT borclari
                // okudugu icin zoneDebt=0 cikiyor, adim 6b'deki kapi aciliyor ve adim 7 ucreti
                // "0" gonderiyordu (sunucu ucreti KENDI hesaplamiyor) -> arac BEDAVA cikiyordu.
                // Artik ucret hesaplanip VEHICLE_CREDIT yaziliyor ve cikis DURDURULUYOR;
                // surucu kiosktan odeyip tekrar geldiginde borc kapali olacagi icin cikabilir.
                try
                {
                    decimal ucret = (decimal)await _vehicleApi.GetParkPriceAsync(entryId);

                    if (ucret > 0)
                    {
                        var borcResp = await _vehicleApi.AddVehicleCreditAsync(new AddVehicleCreditRequest
                        {
                            CurrentUserId = UserSession.UserId,
                            VehicleDefinitionId = vehicle.Id,
                            DebtAmount = ucret.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            PaidAmount = "0",
                            Description = $"Park girisi bulunmayan arac - cikista olusturuldu ({LoggedZoneName})",
                            CompanyId = UserSession.CompanyId,
                            ZoneId = BolgeId,
                            VehicleExitId = 0
                        });

                        if (borcResp?.Errors != null && borcResp.Errors.Count > 0)
                        {
                            var bMsg = string.Join(", ", borcResp.Errors
                                .Where(x => !string.IsNullOrEmpty(x.Message)).Select(x => x.Message));
                            // Borc yazilamadiysa da BEDAVA CIKISA IZIN VERME (fail-closed).
                            ShowToast($"{plate}: Borc kaydi olusturulamadi ({bMsg}). Guvenlik geregi cikis yapilmadi.", false);
                            return;
                        }

                        ShowToast(
                            $"{plate}: Park girisi bulunmadigi icin {ucret:F2} TL borc olusturuldu. " +
                            $"Borc odenmeden cikis bariyeri ACILMAZ - lutfen kiosk cihazindan odeme yapiniz.",
                            false);

                        try { await LoadParkDataAsync(); } catch { }
                        return;   // BARIYER ACILMAZ
                    }
                    // ucret 0 ise (ucretsiz tarife / cok kisa sure) normal akisa devam edilir
                }
                catch (Exception exUcret)
                {
                    // Ucret hesaplanamadiysa emin olamayiz -> GECIRME
                    ShowToast($"{plate}: Ucret hesaplanamadi ({exUcret.Message}). Guvenlik geregi cikis yapilmadi.", false);
                    return;
                }
            }

            // 4. Borc kontrolu - tum bolge borclari + bu bolgeye ait borc.
            var creditInfo = await GetVehicleDebtsAsync(vehicle.Id, entryId);

            // BORC BILINMIYORSA CIKIS YOK (18.08.2026).
            // Onceden sorgu hata verdiginde borc 0 kabul ediliyor ve borclu arac
            // sessizce cikiyordu. Artik bilinmeyen durum cikisi DURDURUR; personel
            // gerekirse "Borclu Cikisi Yap" ile bilincli olarak cikarabilir.
            if (!creditInfo.basarili)
            {
                ShowToast(
                    $"{plate}: Borç bilgisi alınamadı (sunucuya ulaşılamıyor). " +
                    "Güvenlik gereği çıkış yapılmadı. Bağlantıyı kontrol edin.",
                    false);
                return;
            }

            decimal zoneDebt = creditInfo.zoneDebt;
            decimal totalDebt = creditInfo.totalDebt;

            // 4b. ABONELIK KONTROLU — BU BOLGEYE AIT MI? (18.08.2026)
            //
            // Cikis akisinda abonelik hic sorgulanmiyordu; abone yalnizca "borcu
            // olmadigi icin" geciyordu. Artik acikca sorulur ve BolgeId gonderilir:
            // sunucu, kapali otoparkta yalnizca O BOLGEYE ait aboneligi gecerli
            // sayar. Baska bir otoparkin ya da yol kenarinin abonesi burada abone
            // DEGILDIR; normal ucret/borc akisina duser.
            bool aboneMi = false;
            try
            {
                var cikisAbone = await _vehicleApi.CheckSubscriptionAsync(
                    plate, UserSession.CompanyId, BolgeId);
                aboneMi = cikisAbone != null && cikisAbone.IsSubscriber;
                if (aboneMi)
                    System.Diagnostics.Debug.WriteLine(
                        $"[CIKIS] {plate}: ABONE ({cikisAbone?.SubscriptionName}) - ucretsiz cikis.");
            }
            catch (Exception exAbone)
            {
                // Abonelik ogrenilemezse abone SAYILMAZ. Gecerli bir abonenin
                // zaten borcu olmadigi icin asagidaki borc engeline takilmaz;
                // yani bu varsayim aboneyi magdur etmez.
                System.Diagnostics.Debug.WriteLine(
                    $"[CIKIS] {plate}: abonelik sorgusu basarisiz ({exAbone.Message}).");
            }

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

            // 6b. Bu bolgeye ait borc varsa cikisi engelle
            //     (abone degilse VE yikama ile karsilanmadiysa).
            //
            // ABONE MUAFIYETI: bu otoparkin abonesi olan arac ucretsiz cikar;
            // bariyer otomatik acilir. Sunucu tarafi da abone icin tum odeme/borc
            // blogunu atlar, dolayisiyla cikis 0 TL olarak kaydedilir.
            // UCRETSIZ CIKIS SERBESTTIR (19.08.2026).
            //
            // Sahadan: "Cikista bariyeri 0 yani ucretsiz olanlarin tamamina acmali.
            // ENGELLI 0 TL ucret gosteriyor ama cikis acilmiyor."
            //
            // Sebep: engelleyen kosul zoneDebt idi ve o, aracin bu bolgedeki TUM
            // acik borclarini topluyor — ONCEKI ziyaretlerden kalanlar dahil.
            // Bugunku konaklamasi ucretsiz olan arac (engelli arac tipi, ucretsiz
            // sure, cok kisa sure) eski bir borcu yuzunden bariyerde kaliyordu.
            //
            // Artik karar BU KONAKLAMAYA bakiyor:
            //   - bu girise bagli acik borc var mi (creditInfo.girisBorcu)
            //   - su anki park ucreti nedir (sunucu hesaplar; arac tipini ve
            //     gecen sureyi bilir)
            // Ikisi de 0 ise cikis UCRETSIZDIR ve bariyer acilir.
            //
            // Eski borclar SILINMEZ, kapatilmaz; kiosktan odenmeye devam eder.
            // Yalnizca ucretsiz bir konaklamayi rehin almalari engellenir.
            decimal buKonaklamaUcreti = 0m;
            try
            {
                buKonaklamaUcreti = (decimal)await _vehicleApi.GetParkPriceAsync(entryId);
            }
            catch
            {
                // Hesaplanamadiysa ucretsiz VARSAYILMAZ; asagidaki kosul
                // eski davranisa doner ve borc varsa cikis durur.
                buKonaklamaUcreti = -1m;
            }

            bool ucretsizCikis = creditInfo.girisBorcu <= 0 && buKonaklamaUcreti == 0m;

            if (!aboneMi && !washBypass && !ucretsizCikis && zoneDebt > 0)
            {
                var msg = $"{plate} plakali aracin {LoggedZoneName} kapali otopark icin {zoneDebt:F2} TL borcu bulunmakta. Borcu odenmeden cikis yapilamaz.";
                if (totalDebt > zoneDebt)
                    msg += $" (Tum bolgelerdeki toplam borc: {totalDebt:F2} TL)";
                ShowToast(msg, false);
                return;
            }

            if (ucretsizCikis && zoneDebt > 0)
            {
                // Cikis serbest ama personel eski borcu bilsin.
                ShowToast($"{plate}: Bu park ucretsiz. Aracin bolgede {zoneDebt:F2} TL ESKI borcu var (cikis engellenmedi).", true);
            }
            if (aboneMi)
            {
                ShowToast($"{plate}: Abonelik geçerli — ücretsiz çıkış yapılıyor.", true);
            }
            else if (washBypass && zoneDebt > 0)
            {
                // DIKKAT: burada borc KAPATILMIYOR - yalnizca cikis engeli kaldiriliyor.
                // Eski metin "borc odendi olarak isaretlendi" diyordu; kodda boyle bir
                // isaretleme YOK. Giriste yazilan VEHICLE_CREDIT satiri acik kalir.
                // Personel borcu tahsil edilmis sanip para istemiyordu; metin gercege cekildi.
                ShowToast($"{plate}: Yıkama fişi geçerli, çıkış serbest. Otopark borcu ({zoneDebt:F2} TL) açık kalmaya devam ediyor.", true);
            }

            // ================= BARIYER BURADA ACILIR (01.09.2026 - madde 4) =================
            //
            // SAHA SIKAYETI: "Cikis bariyer tetigi gec gidiyor, okuduktan sonra
            // acilma suresi 5 saniye."
            //
            // SEBEBI: bariyer, sunucudaki cikis kaydi ONAYLANDIKTAN sonra
            // aciliyordu. Gecikme bariyer komutunda degil, o sunucu gidis
            // donusunde (AddExitAsync ~5 sn).
            //
            // ARTIK: arac cikisa YETKILI hale gelir gelmez bariyer acilir.
            // Yetki bu satira gelinmesiyle zaten kanitlanmistir - yukarida
            // borc kapisi var ve borclu arac "return" ile geri donuyor
            // (abone / ucretsiz / yikama istisnalari da orada karara baglaniyor).
            // Cikis kaydi bundan SONRA yazilir.
            //
            // >>> BILINEREK ALINAN RISK <<<
            // 28.08.2026'da tam tersi yapilmisti: kayit dogrulanmadan bariyer
            // acilmasin diye. O koruma bilerek KALDIRILIYOR (kullanici karari,
            // secenek "b"). Bedeli: sunucu yazmayi beceremezse arac cikmis ama
            // kaydi olusmamis olur. Bu yuzden asagida kayit basarisiz olursa
            // SESSIZ GECILMEZ: personele kirmizi uyari cikar ve kurtarma notu
            // yazilir; boylece vaka kaybolmaz, elle duzeltilebilir.
            bool bariyerAcildi = false;
            if (OnOpenExitGateRequested != null)
            {
                await OnOpenExitGateRequested.Invoke(plate);
                bariyerAcildi = true;
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

            // CIKIS FOTOGRAFI: plaka okundugunda kaydedilen _X_ snapshot'i sunucuya gonderilir.
            // Onceden HIC gonderilmiyordu; bu yuzden web "Plaka Revizyon" ekraninda cikis
            // satirlarinda da GIRIS fotografi gorunuyordu.
            exitReq.Photo = CikisFotografiBase64();

            // ===== KAPALI OTOPARK CIKISI AYRI UCA GIDER (27.08.2026) =====
            //
            // NEDEN: kapali otoparkta para BARIYERDE DEGIL KIOSKTA tahsil edilir;
            // bu cikis yalnizca KAYIT tutar. Ortak uc (AddVehicleExit) ise "bu
            // cikista para aliniyor" varsayimiyla yazilmis ve yazilan ucret 0'dan
            // buyuk olur olmaz:
            //   - PARK_PAYMENTS satiri acar     -> Z raporu ayni parayi CIFT SAYAR
            //     (kiosk tahsilati zaten VEHICLE_CREDIT_PAID'te; kioskun
            //      PARK_PAYMENTS blogu yorum satiri)
            //   - PAYTR TYPE_PARK faturasi atar -> gun sonu IKINCI GERCEK FATURA
            //   - odeme tipi HGS ise provizyon  -> musteriden IKINCI KEZ PARA CEKER
            //
            // Bu yuzden ortak servise kosul EKLENMEDI; kapali otopark komple ayri
            // bir uca alindi ve iki dunya birbirine karismiyor. Yeni uc YALNIZCA
            // VEHICLE_PARK_EXIT satirini yazar:
            //   CALCULATED_FEE = PAYABLE_FEE = ziyaret icin GERCEKTEN tahsil edilen
            //   EXIT_CODE      = o tahsilatin gercek tipi (4 = Kredi Karti, 5 = HGS)
            // Ucreti ve odeme tipini SUNUCU belirler; istemci tutar gondermez.
            //
            // GERIYE DONUK UYUM: uc yoksa (sunucu henuz guncellenmemis) ya da aga
            // erisilemezse null doner ve ESKI uc calisir. Dagitim sirasi onemli
            // olmaz, cikis her halukarda yapilir.
            // Sunucunun yazdigi ucret; yerel hasilat sayaci bunu kullanir (sunucu
            // KAYNAK OTORITEDIR). null ise eski davranis (CurrentDebt) surer.
            decimal? sunucuCikisUcreti = null;

            var kapaliCikis = await _vehicleApi.AddClosedParkExitAsync(new ClosedParkExitRequest
            {
                CompanyId      = UserSession.CompanyId,
                VehicleEntryId = entryId,
                ExitZoneId     = BolgeId,
                ExitUserId     = UserSession.UserId,
                CurrentUserId  = UserSession.UserId,
                ExitTimeStamp  = DateTime.Now,
                Photo          = exitReq.Photo,

                // Politika geregi ucretsiz cikanlar: abone ve yikama fisli arac.
                // Borclari acik olsa bile cikis "odenmedi" sayilmaz; EXIT_CODE
                // bugune kadarki gibi 1 kalir ve mevcut raporlar degismez.
                UcretsizCikis  = aboneMi || washBypass || ucretsizCikis
            });

            if (!kapaliCikis.EndpointMissing)
            {
                var kc = kapaliCikis.Result;

                // ISTEK BASARISIZ (500 / zaman asimi / bozuk yanit):
                // cikis satiri YAZILMIS OLABILIR. Eski uca DUSULMEZ - dusulseydi
                // ayni girise IKINCI cikis kaydi acilirdi. Bariyer de ACILMAZ.
                if (kc == null)
                {
                    // BARIYER ZATEN ACILDI (madde 4-b). Vaka kaybolmasin diye
                    // hem personele hem kurtarma notuna yazilir.
                    ShowToast($"{plate}: BARIYER ACILDI ama CIKIS KAYDI OLUSMADI (sunucu yaniti yok). Kaydi elle tamamlayin!", false);
                    CikisKurtarmaNotu(plate, entryId, "sunucu yaniti alinamadi (kapali otopark ucu)");
                    return;
                }

                // "ZATEN CIKMIS" HATA DEGILDIR: cikis kaydi mevcut, bariyer acilmali.
                // Aksi halde arac icerde kilitli kalir (eski uc bu durumda aciyordu).
                if (!kc.Success && !kc.AlreadyExited)
                {
                    ShowToast(
                        string.IsNullOrWhiteSpace(kc.Message)
                            ? $"{plate}: Cikis kaydedilemedi. Bariyer acilmadi."
                            : $"{plate}: {kc.Message}",
                        false);
                    return;
                }

                sunucuCikisUcreti = kc.CalculatedFee;

                if (kc.Reason == "BORCLU")
                    ShowToast($"{plate}: Cikis kaydedildi ancak bu ziyaretin borcu ACIK kaldi.", true);

                // Basarili -> ortak uc CAGRILMAZ; asagidaki ORTAK tamamlama akisi
                // (yerel satir isaretleme, sayac, bariyer, toast) aynen calisir.
            }
            else
            {
                var exitResponse = await _vehicleApi.AddExitAsync(exitReq);

                // KRITIK: exitResponse == null durumu ONCEDEN sessizce BASARI sayiliyordu
                // ('?.' yuzunden kosul false oluyordu) -> cikis kaydedilmemis olsa bile
                // bariyer aciliyordu. Artik acikca basarisiz kabul edilir.
                if (exitResponse == null)
                {
                    // BARIYER ZATEN ACILDI (madde 4-b).
                    ShowToast($"{plate}: BARIYER ACILDI ama CIKIS KAYDI OLUSMADI (sunucudan yanit yok). Kaydi elle tamamlayin!", false);
                    CikisKurtarmaNotu(plate, entryId, "sunucudan yanit alinamadi");
                    return;
                }

                if (exitResponse.Errors != null && exitResponse.Errors.Count > 0)
                {
                    var errorMsg = string.Join(", ", exitResponse.Errors
                        .Where(e => !string.IsNullOrEmpty(e.Message))
                        .Select(e => e.Message));
                    ShowToast($"{plate}: BARIYER ACILDI ama cikis kaydedilemedi — " +
                              (string.IsNullOrWhiteSpace(errorMsg) ? "sunucu hatasi." : errorMsg), false);
                    CikisKurtarmaNotu(plate, entryId, errorMsg);
                    return;
                }
            }

            // Cikis yapan aracin YEREL satiri isaretlenmezse "Bos/Dolu" sayaci dusmez;
            // UpdateParkCounts sayimi _allVehicles uzerinden yapar.
            //
            // existingRow yalnizca PLAKA ile arandigi icin null kalabiliyordu:
            //   - plaka metni listedekinden farkli okunmus olabilir (bosluk/format),
            //   - giris kaydi listede olmayabilir (entryId yukarida API'den tazelendi).
            // Bu durumda eskiden hicbir satir isaretlenmiyor ve arac SAYILMAYA DEVAM
            // EDIYORDU. Artik EntryId ile de aranir.
            if (existingRow == null && entryId > 0)
                existingRow = _allVehicles.FirstOrDefault(v => v.EntryId == entryId && v.ExitDateTime == null);

            if (existingRow != null)
            {
                existingRow.ExitDateTime = DateTime.Now;
                existingRow.ExitPlateImagePath = GetFirstSnapshotPath(isEntry: false);
                existingRow.ParkType = "Cikis";
                // HASILAT: sunucu yazdiysa ONUN tutari kullanilir (kaynak otorite).
                // Kiosktan odenmis aracta CurrentDebt zaten 0'a dusmus oluyor ve
                // yerel hasilat kutusu 0 gosteriyordu; sunucunun yazdigi tutar gercek.
                existingRow.ExitFee = sunucuCikisUcreti ?? existingRow.CurrentDebt;
                existingRow.CurrentDebt = 0;
                existingRow.TotalDebt = existingRow.OldDebt;
            }

            UpdateParkCounts();
            ApplyFiltersInternal();

            // NOT: Bariyer YUKARIDA, borc kontrolunun hemen ardindan acildi
            // (madde 4-b). Burada TEKRAR cagrilmaz - cikis bariyerinde bekleme
            // uygulanmadigi icin (beklemeyiAtla: true) ikinci komut roleyi bir
            // kez daha tetikler ve bariyer kapanip yeniden acilirdi.

            // DOLULUK SUNUCUDAN TAZELENIR (01.09.2026 - saha: "exe ve web farkli
            // kapasite gosteriyor").
            //
            // UpdateParkCounts icerideki araci YEREL listeden (_allVehicles) sayar;
            // o liste ekranin gosterdigi kumedir ve DUNDEN KALAN araclari icermez.
            // RefreshOccupancyAsync ise web panosuyla AYNI ucu kullanir
            // (ZoneManager.GetParkOccupancy) ve tum acik girisleri sayar.
            //
            // Giris, iptal ve toplu cikis akislarinda ikisi zaten pes pese
            // cagriliyordu; TEK EKSIK burasiydi. Cikistan sonra yerel sayi
            // sunucununkinin uzerine yaziliyor ve iki ekran farkli rakam
            // gosteriyordu.
            //
            // BARIYERDEN SONRA cagriliyor - bilerek. Oncesine konursa cikis
            // aninda bir sunucu gidis-donusu daha eklenir ve bariyerin acilmasi
            // gecikir; bariyer suresi zaten ayri bir sikayet konusu.
            await RefreshOccupancyAsync();

            string toast = bariyerAcildi
                ? $"{plate} cikis kaydedildi, bariyer acildi."
                : $"{plate} cikis kaydedildi.";
            if (totalDebt > 0) toast += $" (Diger bolgelerde toplam borc: {totalDebt:F2} TL)";
            ShowToast(toast, true);
            ExitDetectedPlate = "";

            // Sunucu KAYNAK OTORITEDIR: yerel isaretleme tutmasa bile (satir hic listede
            // yoksa, plaka eslesmediyse) sayac dogru olsun diye liste tazelenir.
            // Bariyer ACILDIKTAN sonra yapilir; kapinin acilmasini geciktirmez.
            await LoadParkDataAsync();
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
    /// BORCLU CIKISI YAP — "Borclu Cikisi Yap" butonunun is mantigi.
    ///
    /// Amac: kuyruk olustugunda borcu tahsil edilemeyen arac BEDAVA CIKMASIN.
    /// Personel bu butona bastiginda:
    ///   1) borc kayitli degilse park ucreti hesaplanip VEHICLE_CREDIT olarak YAZILIR
    ///   2) cikis kaydi islenir (arac "hala iceride" kalmasin)
    ///   3) ACIKLAMA alanina personel notu dusulur
    ///      ("Personel bariyeri acti - borclandirilarak cikis yapildi")
    ///   4) cagiran taraf bariyeri acar — borc ACIK kalir, arac sonraki gelisinde borclu gorunur
    ///
    /// NOT: Normal manuel bariyer butonu (BarrierExit_Click) SORGUSUZ acilir; kuyrukta
    /// hizli kalmasi icin orada borc kontrolu yapilmaz. Bu metot ayri ve BILINCLI bir aksiyondur.
    ///
    /// Borc kaynagi otomatik cikis akisiyla AYNI: plaka -> arac -> VEHICLE_CREDIT.
    /// (Satirdaki OldDebt kullanilmaz; o alan VEW_VEHICLE_PARK.Balance'tan gelir ve
    ///  GERCEK borc degildir.)
    ///
    /// Doner: (bariyerAcilsin, mesaj, mesajBasariliMi)
    /// </summary>
    /// <summary>
    /// MISAFIR ARAC ISARETI (24.08.2026).
    ///
    /// Sunucuda VEHICLE_PLATE_REVISION'a "MISAFIR" logu dusulur ve aciklama yazilir.
    /// UCRET, BORC, CIKIS ve BARIYER akislarina HIC DOKUNMAZ - bilincli bir karardir:
    /// kullanici yalnizca isaretlenmesini ve aciklama yazilmasini istedi. Yeni bir
    /// PaymentType/EXIT_CODE uretmek dashboard'daki KK-HGS kirilimlarini ve tahsilat
    /// raporlarini bozardi.
    /// </summary>
    public async Task<bool> MisafirAracIsaretleAsync(long entryId, string aciklama)
    {
        try
        {
            return await _vehicleApi.MarkGuestVehicleAsync(
                entryId, UserSession.CompanyId, UserSession.UserId, aciklama);
        }
        catch { return false; }
    }

    public async Task<(bool acilsin, string mesaj, bool basarili)> BorcluCikisYapAsync(VehicleRow? row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.Plate))
            return (false, "Once listeden arac seciniz.", false);

        try
        {
            var resp = await _vehicleDefApi.GetVehicleByPlateAsync(UserSession.CompanyId, row.Plate.Trim());
            var vehicle = resp?.Result;
            if (vehicle == null || vehicle.Id == 0)
                return (true, "", true);   // arac kaydi yoksa borc da yoktur

            var borcBilgi = await GetVehicleDebtsAsync(vehicle.Id);

            // Borc sorgusu basarisizsa "borc yok" VARSAYILMAZ: personelin bilerek
            // borclu cikis yaptigi bu akista bile, borcu bilmeden cikarmak kaydin
            // eksik kalmasina yol acar.
            if (!borcBilgi.basarili)
                return (false, $"{row.Plate}: Borç bilgisi alınamadı (sunucuya ulaşılamıyor). Çıkış yapılmadı.", false);

            decimal zoneDebt = borcBilgi.zoneDebt;

            // Yikama fisi + ucretsiz sure DOLMAMIS -> ucret yikama ile karsilanmis, serbest cikis
            try
            {
                var washStatus = await _vehicleApi.GetWashStatusAsync(UserSession.CompanyId, row.EntryId);
                if (washStatus.HasWashReceipt && !washStatus.IsExpired)
                    return (true, $"{row.Plate}: Otopark ücreti yıkama ile karşılandı.", true);
            }
            catch { /* yikama durumu alinamazsa normal akis */ }

            // Bu girise ait GUNCEL park ucreti (cikis kaydina CALCULATED_FEE olarak yazilacak
            // ve sunucu bu tutar uzerinden borcu KENDI olusturacak).
            decimal parkUcreti = 0m;
            if (row.EntryId > 0 && row.ExitDateTime == null)
            {
                try { parkUcreti = (decimal)await _vehicleApi.GetParkPriceAsync(row.EntryId); }
                catch { /* hesaplanamazsa 0 kalir */ }
            }

            // Gosterilecek borc: KAYITLI borc (girişte zaten yazildi).
            // DIKKAT: zoneDebt + parkUcreti TOPLANMAZ - ayni ucret iki kez sayilir
            // (girişte 80 borc yazilmis, GetParkPrice yine 80 doner -> 160 gorunurdu).
            // Kayitli borc yoksa (nadiren) hesaplanan ucrete duselir.
            decimal borc = zoneDebt > 0 ? zoneDebt : parkUcreti;

            // ===== BORCSUZ CIKISTA DA KAYIT YAZILIR (26.08.2026 - saha vakasi) =====
            //
            // ONCEDEN BURADA "return (true, \"\", true);" VARDI: borc 0 ise bariyer
            // aciliyor ama VEHICLE_PARK_EXIT HIC OLUSMUYORDU. Musteri kiosktan
            // odeyip geldiginde borc zaten 0'a dustugu icin tam da bu dala
            // dusuluyor; arac cikiyor, sistemde SONSUZA KADAR "iceride" kaliyordu.
            // HUNAT'ta 25.08'de takili kalan araclarin bir kismi bu yolla olustu
            // ("borcu odenmis ama cikisi yok" kohortu).
            //
            // Artik erken donus YOK: akis asagidaki cikis kaydi blogunda devam eder.
            // Yalnizca PERSONEL ONAYI atlanir - borc yoksa soracak bir sey yoktur.
            bool borcsuzCikis = borc <= 0;

            // ---- BORCLU ARAC: personele onay sor (borcsuzda SORULMAZ) ----
            bool onay = borcsuzCikis
                ? true
                : OnConfirmRequired != null
                ? await OnConfirmRequired.Invoke(
                    "Borclu Cikisi Yap",
                    $"{row.Plate} plakali aracin {borc:0.##} TL borcu var.\n\n" +
                    "Arac BORCLANDIRILARAK cikarilacak:\n" +
                    "  - Borc kayitli kalacak (silinmez)\n" +
                    "  - Cikis kaydi islenecek\n" +
                    "  - Aciklamaya personel notu dusulecek\n" +
                    "  - Bariyer acilacak\n" +
                    "  - Arac bir sonraki gelisinde BORCLU gorunecek\n\n" +
                    "Onayliyor musunuz?")
                : true;

            if (!onay)
                return (false, $"{row.Plate}: Islem iptal edildi. Borc {borc:0.##} TL.", false);

            // ACIKLAMA'ya dusulecek personel notu
            string personelNotu = borcsuzCikis
                ? $"Borcsuz cikis - kayit olusturuldu ({LoggedZoneName}, Kullanici: {UserSession.UserId})"
                : $"Personel bariyeri acti - borclandirilarak cikis yapildi ({LoggedZoneName}, Kullanici: {UserSession.UserId})";

            // ---- CIKIS KAYDI: ucret + "Odeme Yapilmadi" ile ----
            // ONEMLI: Borcu ISTEMCI YAZMAZ. Cikis istegi
            //     CalculatedFee = <park ucreti>, PayableFee = 0, PaymentTypeId = NoPay(3)
            // ile gonderildiginde SUNUCU:
            //   - VEHICLE_PARK_EXIT.CALCULATED_FEE = ucret        (onceden 0 yaziliyordu)
            //   - VEHICLE_PARK_EXIT.EXIT_CODE     = 3 (NoPay)     (onceden 1/Nakit yaziliyordu;
            //                                                      ExitCode = Payment.PaymentTypeId)
            //   - VEHICLE_CREDIT'i KENDI olusturur ve VEHICLE_EXIT_ID = olusan cikis id'si yapar
            //     (onceden istemci borcu ayri yazdigi icin VEHICLE_EXIT_ID = 0 kaliyordu)
            // Bu yuzden istemci tarafinda AddVehicleCredit CAGRILMAZ; aksi halde CIFT borc olusur.
            if (row.EntryId > 0 && row.ExitDateTime == null)
            {
                const int ODEME_YAPILMADI = 3;   // PaymentType.NoPay -> EXIT_CODE = 3

                try
                {
                    var cikisResp = await _vehicleApi.AddExitAsync(new VehicleParkExitRequest
                    {
                        CurrentUserId = UserSession.UserId,
                        VehicleEntryId = row.EntryId,
                        PayingUserId = UserSession.UserId,
                        ExitUserId = UserSession.UserId,
                        ExitZoneId = BolgeId,
                        ExitTimeStamp = DateTime.Now,
                        // Gercek park ucreti -> VEHICLE_PARK_EXIT.CALCULATED_FEE
                        CalculatedFee = parkUcreti.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        PayableFee = "0",           // tahsil edilmedi
                        MembershipDiscount = "0",
                        CompanyId = UserSession.CompanyId,
                        // Borc GIRISTE yazildi ("Kapali Otopark Giris - PLAKA").
                        // Sunucu ikinci borc YAZMASIN; mevcut borcu bu cikisa BAGLASIN.
                        // Aksi halde CREDIT iki katina cikiyordu (80 -> 160).
                        BorcZatenVar = true,
                        // Borclu cikista da CIKIS FOTOGRAFI gonderilir; eskiden yalnizca
                        // normal cikis yolu gonderiyordu ve bu satirlar web'de GIRIS
                        // fotografiyla gorunuyordu.
                        Photo = CikisFotografiBase64(),
                        Payment = new PaymentModel
                        {
                            CurrentUserId = UserSession.UserId,
                            ReceiptNo = 0,
                            PaymentTypeId = ODEME_YAPILMADI,   // -> EXIT_CODE = 3, sunucu borcu kendi yazar
                            AmountCash = "0",
                            PaymentTime = DateTime.Now,
                            CompanyId = UserSession.CompanyId
                        }
                    });

                    if (cikisResp?.Errors != null && cikisResp.Errors.Count > 0)
                    {
                        var cMsg = string.Join(", ", cikisResp.Errors
                            .Where(x => !string.IsNullOrEmpty(x.Message)).Select(x => x.Message));
                        // Cikis islenemediyse borc da olusmaz -> bedava cikisa izin verme
                        return (false, $"{row.Plate}: Cikis kaydedilemedi ({cMsg}). Bariyer acilmadi.", false);
                    }

                    row.ExitDateTime = DateTime.Now;
                    row.ParkType = "Cikis";
                    UpdateParkCounts();
                    ApplyFiltersInternal();
                    await RefreshOccupancyAsync();   // cikan arac doluluktan dusulur
                }
                catch (Exception exC)
                {
                    return (false, $"{row.Plate}: Cikis kaydedilemedi ({exC.Message}). Bariyer acilmadi.", false);
                }
            }
            else if (borcsuzCikis)
            {
                // Acik giris YOK (row.EntryId = 0 ya da cikisi zaten yapilmis).
                // Yazacak bir cikis kaydi da yok; bariyer eskisi gibi acilir.
                return (true, "", true);
            }

            // ACIKLAMA'ya personel notu (giris + olusan cikis kaydina)
            if (row.EntryId > 0)
            {
                try { await _vehicleApi.AddEntryNoteAsync(row.EntryId, UserSession.CompanyId, personelNotu); }
                catch { /* not yazilamazsa islem bozulmaz */ }
            }

            try { await LoadParkDataAsync(); } catch { }

            return (true, $"{row.Plate}: {borc:0.##} TL BORCLANDIRILARAK cikis yapildi. Borc acik kaldi.", true);
        }
        catch (Exception ex)
        {
            // Durum dogrulanamadi -> personel karar versin, bariyeri engelleme
            return (true, $"{row.Plate}: Borc durumu dogrulanamadi ({ex.Message}). Bariyer personel onayiyla aciliyor.", false);
        }
    }

    /// <summary>
    /// Calisilan bolge belli mi? Degilse kullaniciyi uyarip false doner.
    ///
    /// BolgeId = 0 durumu (bolge secmeden giris yapilmis oturum) borc
    /// kontrolunu sessizce devre disi birakiyordu: cikistaki
    /// "c.ZoneId == BolgeId" karsilastirmasi hicbir borcu tutturamiyor,
    /// giriste yazilan borc da ZoneId = 0 ile kaydedilip kalici olarak
    /// hicbir bolgeye eslesmiyordu. Giris ekraninda bolge artik zorunlu;
    /// bu kontrol ikinci savunma hattidir.
    /// </summary>
    private bool BolgeGecerliMi()
    {
        if (BolgeId > 0) return true;

        ShowToast(
            "Bölge seçilmemiş. Araç giriş/çıkış işlemi yapılamaz — " +
            "lütfen çıkış yapıp bölge seçerek tekrar giriş yapın.",
            false);
        return false;
    }

    /// <summary>
    /// Aracin tum bolgelerdeki borc bilgisini doner:
    /// (sorgu basarili mi, kendi bolge borcu, tum bolge toplami).
    ///
    /// GUVENLI TARAF: HATA = "BILINMIYOR", "BORCU YOK" DEGIL (18.08.2026).
    ///
    /// Onceki hali sonunda "catch { return (0, 0); }" tasiyordu. Sunucu hata
    /// verdiginde ya da zaman asimina ugradiginda borc SIFIR hesaplaniyor,
    /// cikistaki "zoneDebt > 0" engeli saglanmiyor ve BORCLU ARAC BARIYERDEN
    /// GECIYORDU — ustelik bu hicbir yere kaydedilmiyordu.
    ///
    /// Artik basarisizlik ayri bir bayrakla bildiriliyor; cagiran taraf cikisi
    /// DURDURUR. Ucret hesabi zaten boyle davraniyordu; iki yol artik tutarli.
    /// </summary>
    private async Task<(bool basarili, decimal zoneDebt, decimal totalDebt, decimal girisBorcu)> GetVehicleDebtsAsync(
        long vehicleDefinitionId, long entryId = 0)
    {
        try
        {
            var credits = await _vehicleApi.GetVehicleCreditsAsync(vehicleDefinitionId);
            decimal zone = 0, total = 0, giris = 0;
            foreach (var c in credits)
            {
                var balance = c.DebtAmount - c.PaidAmount;
                if (balance <= 0) continue;
                total += balance;
                if (c.ZoneId.HasValue && c.ZoneId.Value == BolgeId)
                    zone += balance;

                // BU GIRISE ait borc — eski ziyaretlerden kalanlardan ayrilir.
                if (entryId > 0 && c.VehicleEntryId.HasValue && c.VehicleEntryId.Value == entryId)
                    giris += balance;
            }
            return (true, zone, total, giris);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BORC] Sorgu BASARISIZ: {ex.Message}");
            return (false, 0, 0, 0);
        }
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

    /// <summary>
    /// PLAKA KARSILASTIRMA ANAHTARI (26.08.2026 - saha vakasi).
    ///
    /// SORUN: Kamera OCR'i teknik olarak SADECE ASCII uretir (Tesseract whitelist
    /// "A-Z0-9" ve LocalPlateRecognizer.NormalizePlate Turkce harfi ASCII'ye katlar).
    /// Buna karsilik plakayi VERITABANINA yazan uclar Turkce harfi KORUYOR:
    /// kiosk ekran klavyesi, web "Plaka Revizyon" ekrani ve sunucudaki kulture
    /// duyarli .ToUpper() (tr-TR'de 'i' -> 'I' noktali).
    ///
    /// Sonuc: kayit "TUSB38" (Turkce U), kameradan gelen "TUSB38" (ASCII U) ->
    /// OrdinalIgnoreCase karsilastirmasi TUTMAZ. entryId = 0 kalir, akis
    /// "girisi olmayan arac" daline duser, HAYALET giris + yeni borc yazip
    /// bariyeri ACMADAN doner. Gercek girisin cikisi HIC olusmaz ve arac
    /// sonsuza kadar "iceride" kalir. HUNAT'ta 25.08'de takili kalan araclardan
    /// ikisi (TUSB38, TUGE38) tam olarak bu sekilde olusmustu - o iki aracin
    /// cikisi eski kodla KODEN IMKANSIZDI.
    ///
    /// COZUM: karsilastirma iki tarafta da ayni anahtara indirgeniyor.
    /// VERITABANINDAKI PLATE DEGERI DEGISTIRILMEZ - yalnizca karsilastirma
    /// normalize edilir; gorunen plaka oldugu gibi kalir.
    ///
    /// CAKISMA RISKI YOK: Turk plakalarinda Turkce'ye ozgu harf (C, G, I, O, S, U
    /// noktali/kuyruklu bicimleri) RESMEN BULUNMAZ. Dolayisiyla yalnizca bu
    /// harflerde ayrisan iki GERCEK plaka olamaz.
    /// </summary>
    public static string PlakaAnahtari(string plaka)
    {
        if (string.IsNullOrWhiteSpace(plaka)) return "";

        var sb = new System.Text.StringBuilder(plaka.Length);
        foreach (var ch in plaka)
        {
            char c;
            switch (ch)
            {
                case 'ç': case 'Ç': c = 'C'; break;
                case 'ğ': case 'Ğ': c = 'G'; break;
                case 'ı': case 'İ': c = 'I'; break;
                case 'ö': case 'Ö': c = 'O'; break;
                case 'ş': case 'Ş': c = 'S'; break;
                case 'ü': case 'Ü': c = 'U'; break;
                default:
                    // Harf/rakam disindaki her sey atilir: bosluk, nokta, tire.
                    if (!char.IsLetterOrDigit(ch)) continue;
                    c = char.ToUpperInvariant(ch);
                    break;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Iki plaka ayni araci mi gosteriyor? (Turkce harf ve bosluk toleransli)</summary>
    public static bool PlakaAyniMi(string a, string b) =>
        PlakaAnahtari(a).Length > 0 && PlakaAnahtari(a) == PlakaAnahtari(b);

    [RelayCommand]
    private async Task ApproveExitAsync() => await DoApproveExitAsync();

    /// <summary>
    /// Kuyruga alinmis cikis islemleri icin kilit.
    /// </summary>
    private readonly SemaphoreSlim _cikisKilidi = new SemaphoreSlim(1, 1);

    /// <summary>Kuyrukta bekleyen cikis sayisi (log/uyari icin).</summary>
    private int _cikisKuyrugu = 0;

    /// <summary>
    /// ART ARDA GELEN ARACLARDA CIKIS (25.08.2026 - saha videosu).
    ///
    /// SORUN: Otomatik onay, ApproveExitCommand.CanExecute(null) false ise plakayi
    /// SESSIZCE dusuruyordu. [RelayCommand] async metotta AsyncRelayCommand uretir ve
    /// CommunityToolkit 8.4'te es zamanli calisma varsayilan olarak KAPALIDIR; yani
    /// onceki aracin cikisi islenirken CanExecute FALSE doner.
    /// Sonuc: bariyerde ust uste gelen 2. arac icin ne cikis kaydi olusuyor ne bariyer
    /// aciliyor ne de ekranda uyari cikiyordu - yalnizca log dosyasina bir satir
    /// dusuyordu. 3. arac (ilk islem bittigi icin) normal calisiyordu.
    ///
    /// COZUM: DUSURME, SIRAYA AL. Cagrilar bu kilitle seri hale getiriliyor; bekleyen
    /// arac oncekinin isi bitince kendi sirasinda isleniyor.
    ///
    /// Es zamanli calistirmak (AllowConcurrentExecutions) BILEREK tercih edilmedi:
    /// ayni anda iki cikis istegi bariyer komutlarini ve borc sorgularini ic ice
    /// sokardi.
    ///
    /// KUYRUK SINIRI: ayni anda en fazla MAX_BEKLEYEN arac beklenir. Kamera ayni
    /// plakayi tekrar tekrar okursa kuyruk sisip dakikalar sonra alakasiz cikislar
    /// islenmesin diye. Sinir asilirsa personele uyari gosterilir.
    /// </summary>
    public async Task CikisiSirayaAlAsync(string plate, string[] snapshotPaths, string entryImagePath)
    {
        const int MAX_BEKLEYEN = 3;
        const int BEKLEME_SANIYE = 45;

        if (_cikisKuyrugu >= MAX_BEKLEYEN)
        {
            ShowToast($"{plate}: cikis kuyrugu dolu, islem yapilamadi. Lutfen tekrar okutunuz.", false);
            return;
        }

        System.Threading.Interlocked.Increment(ref _cikisKuyrugu);
        try
        {
            // Sirasi gelene kadar bekler. Onceki islem takilirsa sonsuza kadar
            // beklenmez; sure asiminda personel bilgilendirilir.
            if (!await _cikisKilidi.WaitAsync(TimeSpan.FromSeconds(BEKLEME_SANIYE)))
            {
                ShowToast($"{plate}: onceki islem uzun surdu, cikis yapilamadi. Tekrar okutunuz.", false);
                return;
            }

            try
            {
                // Kuyrukta beklerken bu alanlar baska bir okuma tarafindan
                // degistirilmis olabilir; kendi degerlerimizle YENIDEN kuruyoruz.
                ExitDetectedPlate = plate;
                ExitPlateSnapshotPaths = snapshotPaths ?? Array.Empty<string>();
                ExitEntryImagePath = entryImagePath ?? "";

                await DoApproveExitAsync();
            }
            finally
            {
                _cikisKilidi.Release();
            }
        }
        finally
        {
            System.Threading.Interlocked.Decrement(ref _cikisKuyrugu);
        }
    }

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
            PlakaAyniMi(v.Plate, plate) &&
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
        => FindLocalImageForRow(plate, timestamp, isEntry, toleransSaniye: 10);

    /// <summary>
    /// Plaka + timestamp eslesmesiyle lokal cache'ten resim yolu bulur.
    /// Format: PLAKA_E_yyyyMMddHHmmss.jpg (giris) / PLAKA_X_yyyyMMddHHmmss.jpg (cikis)
    ///
    /// TOLERANS NEDEN PARAMETRE: dosya adindaki saat ISTEMCININ saatidir, aranan
    /// timestamp ise SUNUCUDAN gelir. Iki saat arasinda birkac dakika fark olmasi
    /// normaldir; ±10 sn ile arandiginda cikis gorseli BULUNAMIYOR ve ekranda bos
    /// kaliyordu. Once dar tolerans (en dogru eslesme), tutmazsa genis tolerans denenir.
    /// </summary>
    public static string FindLocalImageForRow(string plate, DateTime timestamp, bool isEntry, int toleransSaniye)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";

        var cacheDir = @"C:\Otopark\ImageCache\";
        if (!System.IO.Directory.Exists(cacheDir)) return "";

        var safePlate = string.Concat(plate.Split(System.IO.Path.GetInvalidFileNameChars()));
        var prefix = isEntry ? "E" : "X";
        var pattern = $"{safePlate}_{prefix}_*.jpg";

        var candidates = System.IO.Directory.GetFiles(cacheDir, pattern);

        // PLAKA REVIZYONU SONRASI GORSEL KAYBOLMASIN (19.08.2026).
        //
        // Dosya adi CEKILDIGI ANDAKI plakayla yazilir. Web'den plaka revizyonu
        // yapilinca satirin plakasi YENI plaka olur, dosya ise ESKI plakayla
        // duruyordur; desen tutmaz ve gorsel ekrandan kaybolur.
        // (Sunucudaki EntryPhotoPath revizyonda DEGISMEZ; kayip yalnizca bu
        //  yerel cache aramasindadir.)
        //
        // Plakayla bulunamazsa ZAMAN DAMGASINA gore aranir: giris/cikis
        // saniyesi pratikte tekildir, bu yuzden yanlis araca ait gorsel
        // eslesme olasiligi cok dusuktur. Yine de tolerans DAR tutulur.
        if (candidates.Length == 0)
        {
            candidates = System.IO.Directory.GetFiles(cacheDir, $"*_{prefix}_*.jpg");
            if (candidates.Length == 0) return "";

            // Zaman esletmesinde tolerans en fazla 10 sn; genis pencereyle
            // (cikis gorselindeki ±15 dk) baska araca ait kare secilebilirdi.
            if (toleransSaniye > 10) toleransSaniye = 10;
        }

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
            if (delta <= toleransSaniye && delta < bestDelta)
            {
                bestDelta = delta;
                bestPath = f;
            }
        }

        return bestPath;
    }

    /// <summary>
    /// Cikis gorselini kademeli olarak arar: once tam eslesme (±10 sn), bulunamazsa
    /// saat farkina karsi genis pencere (±15 dk). Cikis gorseli SUNUCUDA TUTULMADIGI
    /// icin (API'de cikis fotografi alani yok) tek kaynak bu yerel cache'tir; bu yuzden
    /// bulunamayinca ekran bos kalir.
    /// </summary>
    private static string CikisGorseliBul(string plate, DateTime exitTimestamp)
    {
        var yol = FindLocalImageForRow(plate, exitTimestamp, isEntry: false, toleransSaniye: 10);
        if (!string.IsNullOrEmpty(yol)) return yol;

        return FindLocalImageForRow(plate, exitTimestamp, isEntry: false, toleransSaniye: 15 * 60);
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

    /// <summary>
    /// Cikis yapip giris ekranina doner.
    ///
    /// ESKIDEN her cikista "new HttpClient { BaseAddress = ... }" olusturuluyordu.
    /// Iki sorun: (1) her HttpClient kendi baglanti havuzunu acar, atilmadigi icin
    /// soket TIME_WAIT'te birikir; (2) sunucu adresinin 6. kopyasi burada gomuluydu.
    /// Artik zaten elimizde olan _zoneApi'nin HttpClient'i yeniden kullaniliyor -
    /// adres de tek yerden gelmis oluyor.
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        var http = _zoneApi.Http;
        var auth = new AuthApiService(http);
        var zone = new ZoneApiService(http);
        var loginVm = new LoginViewModel(auth, zone, _main);
        _main.Navigate(loginVm);
    }

    // ===== INNER CLASSES =====

    public partial class VehicleRow : ObservableObject
    {
        [ObservableProperty] private long entryId;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlateDisplay))]
        private string plate = "";
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

        // Arac tipi (apiden gelir). Engelli araclarin ayri tarifesi olabildigi icin
        // personel listede ayirt edebilmeli.
        [ObservableProperty] private long vehicleTypeId;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlateDisplay))]
        private bool engelliMi;

        /// <summary>Listede gosterilen plaka. Engelli araclarda sonuna "(E)" eklenir.</summary>
        public string PlateDisplay => EngelliMi ? Plate + " (E)" : Plate;

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
