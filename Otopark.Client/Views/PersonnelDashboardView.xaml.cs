using Microsoft.Extensions.Configuration;
using Otopark.Client.Helpers;
using Otopark.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Otopark.Client.Views
{
    public partial class PersonnelDashboardView : System.Windows.Controls.UserControl
    {
        private readonly DispatcherTimer _uiTimer = new();          // Canli kamera goruntulerini guncelle (hizli)
        private readonly DispatcherTimer _detectTimer = new();      // OCR icin (yavas, kota tasarrufu)
        private FileSystemWatcher? _entryWatcher;
        private FileSystemWatcher? _exitWatcher;

        private readonly CancellationTokenSource _cts = new();
        // Kamera yakalama icin AYRI token: "Kameralari Durdur" butonu yalnizca bunu iptal eder
        // (giris/cikis islemeyi durdurur); ana _cts'e dokunulmaz.
        private CancellationTokenSource _cameraCts = new();
        private bool _camerasPaused = false;
        private readonly SemaphoreSlim _entryGate = new(1, 1);
        // FIX 5 — Snapshot throttling: ayni kameradan saniyede maksimum 2 snapshot
        // isle (500ms minimum aralik). Kamera dakikada 100+ snapshot atinca CPU/Cloud
        // API kotasi tasiyor — gereksiz islemleri bastan kes.
        // appsettings.json: "Snapshot:MinIntervalMs" (default 500) ile ayarlanir.
        private DateTime _lastEntryProcessedUtc = DateTime.MinValue;
        private DateTime _lastExitProcessedUtc = DateTime.MinValue;
        private static int SnapshotMinIntervalMs =>
            int.TryParse(Otopark.Core.Services.AppConfig.Configuration["Snapshot:MinIntervalMs"], out var v) ? v : 500;
        private readonly SemaphoreSlim _exitGate = new(1, 1);
        private bool _tickBusy = false;

        private string _lastEntryFile = "";
        private DateTime _lastEntryWriteUtc = DateTime.MinValue;
        private string _lastExitFile = "";
        private DateTime _lastExitWriteUtc = DateTime.MinValue;

        // Kamera klasorleri - degistirmek icin asagidaki satirlari guncelleyin
        // private const string EntryCaptureFolder = @"C:\Otopark\EntryCaptures\";
        // private const string ExitCaptureFolder = @"C:\Otopark\ExitCaptures\";
        private static string EntryCaptureFolder => $@"D:\GESI\OTOPARK\Entry\{DateTime.Now:yyyy\\MM\\dd}\";
        private static string ExitCaptureFolder => $@"D:\GESI\OTOPARK\Exit\{DateTime.Now:yyyy\\MM\\dd}\";
        private const string EntryShotsFolder = @"D:\GESI\OTOPARK\EntryShots\";
        private const string ExitShotsFolder = @"D:\GESI\OTOPARK\ExitShots\";

        // PlateRecognizer Cloud API tokenleri - appsettings.json'dan alinir, coklu destek
        private static readonly string[] DefaultTokens = new[]
        {
            "2059e14b4a694207a913240af6da257abd38092e",
        };

        // Skor >= 0.90 ise stabilizer tek hit'te kabul (yeni cct_s_v2_global + TR il kodu
        // + format library zinciri sayesinde guvenli). Dusuk skorlu (0.40-0.89) okumalar
        // icin 2-hit gerekli. Pencere 10sn (arac gecisi tipik 3-15sn).
        /// <summary>Kamera dongusu/watcher'lari yalnizca BIR KEZ baslatmak icin (bkz. Loaded).</summary>
        private bool _started;

        private readonly PlateStabilizer _entryStabilizer = new(minScore: 0.40, windowSeconds: 10.0, neededHits: 2);
        private readonly PlateStabilizer _exitStabilizer = new(minScore: 0.40, windowSeconds: 10.0, neededHits: 2);
        // Suppress: ayni/benzer plaka 120sn boyunca tekrar gonderilmez (Levenshtein-tolerant).
        // 60sn cok kisaydi - ayni arac kapakta tekrar tanindiginda duplicate olabiliyordu.
        private readonly DuplicateSuppressor _entrySuppressor = new(suppressSeconds: 120.0);
        private readonly DuplicateSuppressor _exitSuppressor = new(suppressSeconds: 120.0);

        // Birincil: PlateRecognizer Cloud API (coklu token rotasyonu)
        private readonly PlateRecognizerClient _client = new(LoadTokensFromConfig());
        // Yedek: lokal OCR (API kota/network sorunu olursa devreye girer)
        private LocalPlateRecognizer? _recognizer;

        private static IEnumerable<string> LoadTokensFromConfig()
        {
            try
            {
                var section = Otopark.Core.Services.AppConfig.Configuration.GetSection("PlateRecognizer:Tokens");
                var tokens = section.GetChildren()
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Cast<string>()
                    .ToList();
                if (tokens.Count > 0) return tokens;
            }
            catch { }
            return DefaultTokens;
        }

        public PersonnelDashboardView()
        {
            InitializeComponent();

            // ONNX modelleri arka planda indirilsin (varsa atlanir, ilk kez calisirken indirir)
            _ = Otopark.Client.Helpers.PlateModelDownloader.EnsureModelsAsync(_cts.Token);

            // OCR motorunu defensif baslat - native DLL/tessdata sorununda app crash olmasin
            try
            {
                _recognizer = new LocalPlateRecognizer();
            }
            catch (Exception ex)
            {
                Log($"OCR motor baslatilamadi: {ex.Message}");
                _recognizer = null;
            }

            // NOT: Onceden hem burada hem Loaded'da Start() cagriliyordu. Sonuc: AYNI klasore
            // iki MJPEG dongusu (2 kat kare/sn) ve CreateWatcher onceki watcher'i dispose etmeden
            // uzerine yaziyordu -> oksuz FileSystemWatcher'lar olay firlatmaya devam ediyordu.
            // Artik tek sefer baslar (Loaded birden fazla kez tetiklenebilir, bayrakla korunur).
            Loaded += (_, __) => { if (!_started) { _started = true; Start(); } };

            // MISAFIR ARAC dugmesi YETKIYE bagli. Yetki giriste bir kez cekilir
            // (LoginViewModel); servise ulasilamadiysa false kalir ve dugme gizli olur.
            Loaded += (_, __) =>
            {
                PnlMisafirArac.Visibility = Otopark.Core.Session.UserSession.CanMarkGuestVehicle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };
            Unloaded += (_, __) => Stop();

            DataContextChanged += (_, __) =>
            {
                if (DataContext is PersonnelDashboardViewModel vm)
                {
                    vm.OnOpenEntryGateRequested += async (plaka) =>
                    {
                        var r = await Services.BarrierService.OpenEntryGateAsync(plaka);
                        Dispatcher.Invoke(() => vm.ShowBarrierToast(r.Success, r.Message));
                    };

                    vm.OnOpenExitGateRequested += async (plaka) =>
                    {
                        // CIKIS BARIYERINDE BEKLEME UYGULANMAZ (21.08.2026).
                        //
                        // Bekleme, plaka yanlis okundugunda bariyerin kendiliginden
                        // tekrar acilmasini onlemek icindi. Cikista bu koruma GEREKSIZ:
                        // buraya gelinmesi icin sunucuda CIKIS KAYDI olusmus olmasi ya da
                        // personelin bilerek islem yapmis olmasi gerekir.
                        //
                        // Bedeli agirdi: kuyrukta bekleyen aracin plakasi one gecen arac
                        // cikarken ERKEN okunuyor, cikis o anda isleniyor ve bekleme
                        // sayaci tukeniyor. Arac bariyere geldiginde ikinci okuma
                        // "sure yutuldu" ile dusuyor, bariyer ACILMIYORDU.
                        var r = await Services.BarrierService.OpenExitGateAsync(plaka, beklemeyiAtla: true);
                        Dispatcher.Invoke(() => vm.ShowBarrierToast(r.Success, r.Message));
                    };

                    vm.OnPrintEntryReceipt += (info) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                Services.ReceiptPrintService.PrintEntryReceipt(new Services.ReceiptData
                                {
                                    ReceiptNo = info.ReceiptNo,
                                    Plate = info.Plate,
                                    ZoneName = info.ZoneName,
                                    EntryDateTime = info.EntryDateTime,
                                    Fee = info.Fee,
                                    OldDebt = info.OldDebt,
                                    OperatorName = info.OperatorName
                                });
                            }
                            catch (Exception ex)
                            {
                                vm.ShowBarrierToast(false, "Fis basilamadi: " + ex.Message);
                            }
                        });
                    };

                    vm.OnPrintExitReceipt += (info) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                Services.ReceiptPrintService.PrintExitReceipt(new Services.ReceiptData
                                {
                                    ReceiptNo = info.ReceiptNo,
                                    Plate = info.Plate,
                                    ZoneName = info.ZoneName,
                                    EntryDateTime = info.EntryDateTime,
                                    ExitDateTime = info.ExitDateTime,
                                    Fee = info.Fee,
                                    OldDebt = info.OldDebt,
                                    OperatorName = info.OperatorName
                                });
                            }
                            catch (Exception ex)
                            {
                                vm.ShowBarrierToast(false, "Fis basilamadi: " + ex.Message);
                            }
                        });
                    };

                    vm.OnVehicleRegistrationRequired += async (plate, lookupApi) =>
                    {
                        var result = false;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var popup = new AddVehicleWindow(lookupApi, plate);
                            popup.Owner = Window.GetWindow(this);
                            result = popup.ShowDialog() == true;
                        });
                        return result;
                    };

                    vm.OnConfirmRequired += async (title, msg) =>
                    {
                        bool result = false;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            result = MessageBox.Show(msg, title,
                                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                        });
                        return result;
                    };

                    // IPTAL NEDENI penceresi (web Plaka Revizyon ekranindaki ile ayni mantik).
                    // Cikisi yapilmis kayitlarda ek uyari gosterilir.
                    vm.OnCancelReasonRequired += async (plate, hasExit) =>
                    {
                        string? reason = null;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var win = new CancelReasonWindow(plate, hasExit)
                            {
                                Owner = Window.GetWindow(this)
                            };
                            if (win.ShowDialog() == true)
                                reason = win.Reason;
                        });
                        return reason;
                    };

                    vm.OnCorrectPlateRequested += async (row) =>
                    {
                        string? newPlate = null;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var popup = new CorrectPlateWindow(row.Plate, row.EntryPlateImagePath);
                            popup.Owner = Window.GetWindow(this);
                            if (popup.ShowDialog() == true)
                                newPlate = popup.NewPlate;
                        });

                        if (!string.IsNullOrWhiteSpace(newPlate) && newPlate != row.Plate)
                            await vm.ApplyPlateCorrectionAsync(row, newPlate);
                    };
                }
            };
        }

        private void Start()
        {
            Directory.CreateDirectory(EntryCaptureFolder);
            Directory.CreateDirectory(ExitCaptureFolder);
            Directory.CreateDirectory(EntryShotsFolder);
            Directory.CreateDirectory(ExitShotsFolder);

            // Kamera IP'lerini ONCE DB'den (API) yukle; yoksa appsettings'e dusulur (CameraConfigService).
            _ = StartCamerasAsync();
            StartUiTimer();
            StartWatchers();
        }

        private async Task StartCamerasAsync()
        {
            try
            {
                // ONEMLI: Kamera tanimi GIRIS YAPILAN bolgeye gore cekilir.
                // Eskiden appsettings'teki sabit Parking:BolgeId kullaniliyordu; o deger
                // gercek bolge ile ayni olmadigi icin (ornegin config 342, secilen bolge 1350)
                // web'deki kamera tanimi HIC BULUNAMIYORDU ve goruntu gelmiyordu.
                long zoneId = 0;
                if (DataContext is PersonnelDashboardViewModel vmZone && vmZone.BolgeId > 0)
                {
                    zoneId = vmZone.BolgeId;
                }
                else
                {
                    // Bolge SECILMEDI (yonetici "Tum Bolgeler" modu). Tek bir bolge olmadigi
                    // icin kamera tanimi belirsizdir; appsettings'teki yedek deger denenir.
                    // ESKIDEN: bu dusus SESSIZ yapiliyordu. Yedek deger (342) gercek bolgeyle
                    // (orn. 1350) uyusmadigi icin kamera bulunamiyor, log'da sadece anlamsiz
                    // bir "zoneId=342" kaliyor ve arizanin sebebi anlasilamiyordu.
                    zoneId = Services.AppConfigHelper.BolgeId;
                    if (zoneId > 0)
                    {
                        Log($"Bolge secilmedi (yonetici modu). Kamera icin appsettings.json > " +
                            $"Parking:BolgeId yedek degeri kullaniliyor: {zoneId}. " +
                            $"Bu numara bu otoparkin gercek bolgesi degilse kamera GELMEZ; " +
                            $"giris ekranindan bolge secin veya bu degeri duzeltin.");
                    }
                    else
                    {
                        Log("Bolge secilmedi ve appsettings.json > Parking:BolgeId de okunamadi. " +
                            "Kamera baslatilamiyor. Giris ekranindan bolge secin.");
                    }
                }

                // Gecersiz bolge ile sunucuya sormanin anlami yok; bos sonuc doner ve
                // hata mesaji yaniltici olur.
                if (zoneId > 0)
                    await Services.CameraConfigService.LoadAsync(Otopark.Core.Session.UserSession.CompanyId, zoneId);
            }
            catch { }

            // ACILIS TEMIZLIGI - kamera dongusu BASLAMADAN once.
            // 1) Bugunun klasorunde MaxFiles=0 doneminden kalma on binlerce dosya olabilir.
            //    Bu supurme olmadan ilk CleanupOldFiles cagrisi (SaveFrame icinden) hepsini
            //    tek seferde silmeye calisir ve kamera dongusunu dakikalarca tikar.
            // 2) Gun klasorleri (Entry\yyyy\MM\dd) hic silinmiyordu - Nisan'dan beri
            //    birikiyorlardi. DiskBakim 14 gunden eskileri temizler.
            // Beklenmez (await yok) - kamera hemen baslasin, bakim arkada yurusun.
            _ = Services.CameraSnapshotService.TemizleAsync(EntryCaptureFolder, ExitCaptureFolder);
            _ = Helpers.DiskBakim.TumBakimAsync();

            Services.CameraSnapshotService.Start(EntryCaptureFolder, ExitCaptureFolder, _cameraCts.Token);

            // Kamera cozulemediyse kullaniciya SEBEBINI bildir (eskiden sessizce goruntu gelmiyordu).
            if (!string.IsNullOrWhiteSpace(Services.CameraDiag.LastError) && DataContext is PersonnelDashboardViewModel vmErr)
                vmErr.ShowBarrierToast(false, "Kamera: " + Services.CameraDiag.LastError);
        }

        private void Stop()
        {
            // FAZ 5: biriken gunluk ozet kaybolmasin (gun icinde kapanirsa da yazilir)
            try { Otopark.Client.Helpers.PlakaIstatistik.Bitir(); } catch { }

            try
            {
                _uiTimer.Stop();
                _detectTimer.Stop();
                _entryWatcher?.Dispose();
                _exitWatcher?.Dispose();
                _cameraCts.Cancel();
                _cts.Cancel();
            }
            catch { }
        }

        // ===== KAMERA AC/KAPA (giris-cikis engelle) =====

        private void ToggleCameras_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as PersonnelDashboardViewModel;
            if (_camerasPaused)
            {
                // Yeniden baslat
                _cameraCts = new CancellationTokenSource();
                Services.CameraSnapshotService.Start(EntryCaptureFolder, ExitCaptureFolder, _cameraCts.Token);
                _camerasPaused = false;
                BtnToggleCameras.Content = "Kameralar: ACIK";
                BtnToggleCameras.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5ACF90"));
                vm?.ShowBarrierToast(true, "Kameralar baslatildi. Giris/cikis acik.");
            }
            else
            {
                // Durdur: kamera yakalama iptal + OCR/otomatik isleme duraklat
                _cameraCts.Cancel();
                _camerasPaused = true;
                BtnToggleCameras.Content = "Kameralar: KAPALI";
                BtnToggleCameras.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5A5A"));
                vm?.ShowBarrierToast(false, "Kameralar durduruldu. Giris/cikis engellendi.");
            }
        }

        // ===== TIMER =====

        private void StartUiTimer()
        {
            // Canli kamera UI timer: 400ms - akici goruntu
            _uiTimer.Interval = TimeSpan.FromMilliseconds(400);
            _uiTimer.Tick += (_, __) =>
            {
                try { LoadLatestImages(); } catch { }
            };
            _uiTimer.Start();

            // OCR detect timer: 1500ms - kota tasarrufu
            _detectTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _detectTimer.Tick += async (_, __) =>
            {
                if (_camerasPaused) return;   // Kameralar durdurulduysa OCR/otomatik giris-cikis isleme yok
                if (_tickBusy) return;
                _tickBusy = true;
                try
                {
                    await DetectFromFolderAsync(EntryCaptureFolder, isEntry: true, _cts.Token);
                    await DetectFromFolderAsync(ExitCaptureFolder, isEntry: false, _cts.Token);
                }
                finally { _tickBusy = false; }
            };
            _detectTimer.Start();
        }

        // ===== WATCHER =====

        private void StartWatchers()
        {
            _entryWatcher = CreateWatcher(EntryCaptureFolder, true);
            _exitWatcher = CreateWatcher(ExitCaptureFolder, false);
        }

        private FileSystemWatcher? CreateWatcher(string folder, bool isEntry)
        {
            try
            {
                var w = new FileSystemWatcher(folder, "*.*")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };
                w.Created += async (_, e) => await OnNewImageAsync(e.FullPath, isEntry);
                w.Changed += async (_, e) => await OnNewImageAsync(e.FullPath, isEntry);
                return w;
            }
            catch (Exception ex)
            {
                Log($"Watcher hata ({folder}): {ex.Message}");
                return null;
            }
        }

        private async Task OnNewImageAsync(string path, bool isEntry)
        {
            if (!IsImageFile(path)) return;

            // FIX 5 — Snapshot throttling: son islemden N ms gecmediyse skip
            int minMs = SnapshotMinIntervalMs;
            if (minMs > 0)
            {
                var nowUtc = DateTime.UtcNow;
                var last = isEntry ? _lastEntryProcessedUtc : _lastExitProcessedUtc;
                if ((nowUtc - last).TotalMilliseconds < minMs)
                    return; // sessiz skip — log yapma (bombardiman olmasin)
                if (isEntry) _lastEntryProcessedUtc = nowUtc;
                else _lastExitProcessedUtc = nowUtc;
            }

            if (!await WaitUntilFileReady(path, _cts.Token)) return;

            var gate = isEntry ? _entryGate : _exitGate;
            // Bekle (max 500ms): mesgulse pas gec - timer zaten son dosyayi yakalar.
            // 2000ms cok uzun, kuyruk birikiyordu.
            if (!await gate.WaitAsync(500, _cts.Token)) return;

            try
            {
                await TryDetectAndSetAsync(path, isEntry, _cts.Token);
            }
            catch (Exception ex) { Log($"OnNewImage hata: {ex.Message}"); }
            finally { gate.Release(); }
        }

        // ===== DETECTION =====

        private async Task DetectFromFolderAsync(string folder, bool isEntry, CancellationToken ct)
        {
            if (!Directory.Exists(folder)) return;

            // Tek tarama, dosya adina gore (stat cagrisi yok) - bkz. SonGorseller.
            var latestPath = GetLatestImagePath(folder);

            // CIKIS TARAFI ARTIK GIRIS KLASORUNE DUSMEZ (18.08.2026).
            //
            // Burada su mantik vardi: cikis klasorundeki en yeni kare 5 dakikadan
            // eskiyse ("bayat"), GIRIS klasorundeki kare alinip isEntry:false ile
            // CIKIS olarak isleniyordu:
            //
            //     bool bayat = latestPath == null ||
            //                  (DateTime.UtcNow - File.GetLastWriteTimeUtc(latestPath)).TotalMinutes > 5;
            //     if (bayat) { ... latestPath = entryLatest; }
            //
            // Sessiz bir otoparkta cikis kamerasi uzun sure kare uretmez; bu
            // durumda YENI PARK EDEN bir aracin GIRIS fotografi dogrudan cikis
            // akisini tetikliyordu. AutoApprove:Exit varsayilan acik oldugu icin
            // arac icerideyken cikis islemi baslatilabiliyor, hatta "girisi
            // bulunamadi" dalina dusup geriye donuk giris + borc olusabiliyordu.
            //
            // Cikis yalnizca CIKIS kamerasinin karesiyle degerlendirilir. Cikis
            // kamerasi calismiyorsa dogru cozum kamerayi duzeltmektir; giris
            // karesini cikis sanmak degil.

            if (latestPath == null) return;

            DateTime latestWriteUtc;
            try { latestWriteUtc = File.GetLastWriteTimeUtc(latestPath); }
            catch { return; }   // dosya bu arada silindi (temizlik) - sonraki tick'te bakilir

            var lastFile = isEntry ? _lastEntryFile : _lastExitFile;
            var lastWrite = isEntry ? _lastEntryWriteUtc : _lastExitWriteUtc;

            if (latestPath == lastFile && latestWriteUtc == lastWrite)
                return;

            if (!await WaitUntilFileReady(latestPath, ct)) return;

            if (isEntry) { _lastEntryFile = latestPath; _lastEntryWriteUtc = latestWriteUtc; }
            else { _lastExitFile = latestPath; _lastExitWriteUtc = latestWriteUtc; }

            // Watcher yolu (OnNewImageAsync) bu gate'i aliyordu ama timer yolu ALMIYORDU.
            // Sonuc: ayni stabilizer'a iki thread'den es zamanli Push -> buffer bozulmasi
            // ve yanlis/null "en net kare" secimi. Artik iki yol da ayni gate'ten geciyor.
            var gate = isEntry ? _entryGate : _exitGate;
            if (!await gate.WaitAsync(500, ct)) return;   // mesgulse pas gec, sonraki tick yakalar

            try
            {
                await TryDetectAndSetAsync(latestPath, isEntry, ct);
            }
            finally { gate.Release(); }
        }

        private async Task TryDetectAndSetAsync(string imagePath, bool isEntry, CancellationToken ct)
        {
            string side = isEntry ? "giris" : "cikis";
            try
            {
                // OLCUM: hangi asamanin ne kadar surdugu log'a yazilir. Kare basi sure
                // 4 sn'ye ciktiginda darbogazi tahminle degil OLCUMLE bulmak icin.
                var _swTam = System.Diagnostics.Stopwatch.StartNew();

                // 1) Tam goruntu - TR bolge ipucu ile (Turk plakalarinda %90+ dogruluk)
                var best = await RecognizeWithScoreAsync(imagePath, ct);
                long msTam = _swTam.ElapsedMilliseconds;
                long msRoi = 0;

                // 2) ROI kirpma - IKINCI TAM GECIS. Pahali oldugu icin kosulu dar tutulur.
                //
                // ROI'nin ise yaradigi TEK durum: plaka uzak/kucuk oldugu icin tam karede
                // hic bulunamadi; kirpip buyutunce bulunabilir.
                //
                // Ise YARAMADIGI durumlar (06.08.2026 olcumu):
                //   - Plaka kare KENARINDA kirpik kalmis  -> ROI daha da kirpar, kotulestirir
                //   - Kutu bulundu ama guven dusuk        -> ayni goruntu, ayni model,
                //                                            genelde ayni sonuc; sadece 2 kat maliyet
                //
                // Kosul genis birakildiginda (her supheli okumada ROI) kare suresi
                // 121 ms -> 3-5 SANIYE'ye cikti; kamera 500 ms'de kare urettigi icin
                // 10 karenin 9'u atlandi ve gercek araclar kacti (log: roi=4199ms).
                bool roiDene = best == null
                    || (best.Value.Supheli && !best.Value.Kenarda && best.Value.MinChar < 0.60);
                if (roiDene)
                {
                    var _swRoi = System.Diagnostics.Stopwatch.StartNew();
                    double xp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:XPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0.10;
                    double yp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:YPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : 0.25;
                    double wp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:WidthPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0.80;
                    double hp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:HeightPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 0.75;

                    var cropped = ImageCropHelper.CropToRoi(imagePath, xp, yp, wp, hp);
                    if (!string.Equals(cropped, imagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var roiBest = await RecognizeWithScoreAsync(cropped, ct);
                        // ROI sonucu ancak DAHA GUVENILIR ise tercih edilir:
                        // once "supheli degil" ustunlugu, esitse daha yuksek min-karakter guveni.
                        bool roiDahaIyi = roiBest != null && (
                            best == null
                            || (best.Value.Supheli && !roiBest.Value.Supheli)
                            || (best.Value.Supheli == roiBest.Value.Supheli
                                && roiBest.Value.MinChar > best.Value.MinChar));
                        if (roiDahaIyi)
                        {
                            Log($"[{side}] ROI daha iyi: minKar {(best?.MinChar ?? 0):F2} -> {roiBest!.Value.MinChar:F2} " +
                                $"(supheli {(best?.Supheli ?? true)} -> {roiBest.Value.Supheli})");
                            best = roiBest;
                        }
                    }
                    msRoi = _swRoi.ElapsedMilliseconds;
                }

                if (best == null)
                {
                    Log($"[{side}] Plaka yok: {Path.GetFileName(imagePath)} (tam={msTam}ms roi={msRoi}ms)");
                    return;
                }

                string plate = best.Value.Plate;
                double score = best.Value.Score;
                bool supheli = best.Value.Supheli;

                // FORMAT KONTROLU ARTIK RED SEBEBI DEGIL — YALNIZCA BILGI.
                //
                // Onceden bilinen formata uymayan her okuma ATILIYORDU. Bu, YABANCI PLAKALI
                // araclarin girisinin TAMAMEN KACMASINA yol aciyordu (DA587AP, H776XL,
                // W73706E gibi okumalar cope gidiyordu).
                //
                // Sahte okuma riski KAYNAKTA kesildi: bolgeler yalnizca gercek dedektorden
                // (ONNX/Haar) geliyor; kenar/renk sezgiseli devre disi. 250 gercek kare
                // uzerinde olculdu -> bos koridor karelerinde SIFIR kutu uretildi.
                //
                // Ilke: yanlis bir harf, aracin tamamen kacmasindan iyidir. Personel plakayi
                // Plaka Revizyon ekranindan duzeltebilir; kacan arac ise geri gelmez.
                bool bilinenFormat = Otopark.Client.Helpers.Plate.PlateFormatLibrary.IsKnownFormat(plate);
                if (!bilinenFormat)
                    Log($"[{side}] Bilinmeyen format (KABUL EDILDI - yabanci plaka olabilir): '{plate}' skor={score:F2}");

                // Cok kisa okumalar hala reddedilir (tek/iki karakter plaka olamaz)
                if (plate.Length < 5)
                {
                    Log($"[{side}] Red (cok kisa): '{plate}' skor={score:F2}");
                    return;
                }

                if (score < 0.40)
                {
                    Log($"[{side}] Red (skor dusuk): '{plate}' skor={score:F2}");
                    return;
                }

                var stabilizer = isEntry ? _entryStabilizer : _exitStabilizer;
                // imagePath = TAM KARE. (ROI kirpmasindan gelen 'cropped' gecici dosyasi
                // BILEREK verilmez; verilirse fotograf olarak kirpilmis goruntu kaydedilir.)
                var stable = stabilizer.Push(plate, score, DateTime.UtcNow, imagePath);
                if (stable == null)
                {
                    Log($"[{side}] Bekleme (stabilizer): '{plate}' skor={score:F2}");
                    return;
                }

                var suppressor = isEntry ? _entrySuppressor : _exitSuppressor;
                if (suppressor.ShouldSuppress(stable.Plate, DateTime.UtcNow))
                {
                    Log($"[{side}] Suppress (8sn): '{stable.Plate}'");
                    return;
                }

                var captureFolder = isEntry ? EntryCaptureFolder : ExitCaptureFolder;

                // ===== EN NET KARE =====
                // Fotograf olarak, plakanin EN IYI okundugu kareyi kullan (stabilizer secer).
                // Dosya bu arada silinmisse tetikleyen kareye geri don.
                string fotoKare = imagePath;
                if (!string.IsNullOrWhiteSpace(stable.BestFramePath) &&
                    !string.Equals(stable.BestFramePath, imagePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(stable.BestFramePath))
                    {
                        Log($"[{side}] EN NET KARE secildi: {Path.GetFileName(imagePath)} (skor={score:F2}) " +
                            $"-> {Path.GetFileName(stable.BestFramePath)} (skor={stable.BestFrameScore:F2}, " +
                            $"fark=+{(stable.BestFrameScore - score):F2})");
                        fotoKare = stable.BestFramePath!;
                    }
                    else
                    {
                        Log($"[{side}] En net kare dosyasi bulunamadi ({Path.GetFileName(stable.BestFramePath)}), " +
                            $"tetikleyen kare kullanilacak.");
                    }
                }
                else
                {
                    Log($"[{side}] Foto = tetikleyen kare (skor={score:F2}); daha iyi kare yok.");
                }

                var savedSnapshots = SavePlateSnapshots(stable.Plate, captureFolder, fotoKare, isEntry);

                // Default: AUTO-APPROVE her zaman acik. Sadece "false" yazilirsa kapanir.
                bool autoApprove = isEntry
                    ? !string.Equals(Otopark.Core.Services.AppConfig.Configuration["AutoApprove:Entry"], "false", StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(Otopark.Core.Services.AppConfig.Configuration["AutoApprove:Exit"], "false", StringComparison.OrdinalIgnoreCase);

                // ===== FAZ 2: GUVEN KAPISI =====
                // SUPHELI okuma otomatik onaya GIRMEZ. Plaka ve fotograf ekrana yine gelir,
                // personel gozuyle dogrulayip onaylar (ya da Plaka Duzelt ile duzeltir).
                //
                // Neden: 51 arac gecisi uzerinde olculdu ->
                //   model emin oldugunda (minKarakter >= 0.90) 46/46 DOGRU,
                //   emin olmadiginda (0.23-0.70) okumalarin cogu YANLIS.
                // Yani "supheli" isareti hatanin neredeyse tamamini yakaliyor.
                // Arac KACMIYOR - sadece otomatik degil, personel onayli giriyor.
                if (supheli && autoApprove)
                {
                    autoApprove = false;
                    Log($"[{side}] OTO-ONAY KAPATILDI (supheli okuma): '{stable.Plate}' " +
                        $"minKarakter={best.Value.MinChar:F2} kenarda={best.Value.Kenarda} " +
                        $"-> personel dogrulamasi bekleniyor");
                }

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (DataContext is not PersonnelDashboardViewModel vm)
                    {
                        Log($"[{side}] OTO-ONAY iptal: DataContext bos");
                        return;
                    }

                    // Personel supheli okumayi FARK ETMELI: plaka ekranda ama otomatik
                    // onaylanmadi. Sebebini de yaz ki ne yapacagini bilsin.
                    if (supheli)
                    {
                        string sebep = best.Value.Kenarda
                            ? "plaka kare kenarinda yarim kalmis"
                            : $"okuma guveni dusuk ({best.Value.MinChar:F2})";
                        vm.ShowBarrierToast(false,
                            $"{(isEntry ? "GIRIS" : "CIKIS")} - PLAKAYI DOGRULAYIN: {stable.Plate}  ({sebep})");
                    }

                    if (isEntry)
                    {
                        vm.EntryDetectedPlate = stable.Plate;
                        vm.EntryPlateSnapshotPaths = savedSnapshots;
                        // Resim arada silinmis olabilir (kamera servisi rotasyonu) - try/catch ile koru
                        string base64 = "";
                        try
                        {
                            // Once kayitli snapshot'i kullan (silinme riskine karsi), yoksa orjinali oku
                            var srcPath = (savedSnapshots.Length > 0 && File.Exists(savedSnapshots[0]))
                                ? savedSnapshots[0]
                                : (File.Exists(imagePath) ? imagePath : "");
                            if (!string.IsNullOrEmpty(srcPath))
                                base64 = Convert.ToBase64String(File.ReadAllBytes(srcPath));
                        }
                        catch (Exception ex) { Log($"[{side}] Base64 okuma hatasi: {ex.Message}"); }
                        vm.SetPendingEntry(stable.Plate, base64);

                        if (autoApprove)
                        {
                            // Cikis tarafiyla ayni duzeltme: komut yerine kuyruklu metot.
                            // Onceki arac islenirken CanExecute false donuyor ve plaka
                            // SESSIZCE dusuruluyordu; giriste bunun bedeli daha agir
                            // (kayit yok -> borc yok -> arac bedava kaliyor).
                            Log($"[{side}] OTO-ONAY (kuyruklu) tetikleniyor: '{stable.Plate}'");
                            await vm.GirisiSirayaAlAsync(stable.Plate, base64);
                        }
                    }
                    else
                    {
                        vm.ExitDetectedPlate = stable.Plate;
                        vm.ExitPlateSnapshotPaths = savedSnapshots;
                        vm.ExitEntryImagePath = vm.GetEntryImageForPlate(stable.Plate);

                        if (autoApprove)
                        {
                            // ART ARDA GELEN ARACLAR (25.08.2026 - saha videosu).
                            //
                            // ONCEDEN: ApproveExitCommand.CanExecute(null) false ise plaka
                            // SESSIZCE dusuruluyordu. AsyncRelayCommand es zamanli calismaya
                            // izin vermedigi icin, onceki aracin cikisi islenirken bu KOSUL
                            // HER ZAMAN false oluyordu. Bariyerde ust uste gelen 2. arac icin
                            // ne cikis kaydi olusuyor ne bariyer aciliyor ne de uyari cikiyordu.
                            //
                            // ARTIK: komut yerine kuyruklu metot cagriliyor; islem dusurulmuyor,
                            // sirasi gelince yapiliyor.
                            Log($"[{side}] OTO-ONAY (kuyruklu) tetikleniyor: '{stable.Plate}'");
                            await vm.CikisiSirayaAlAsync(stable.Plate, savedSnapshots,
                                                         vm.GetEntryImageForPlate(stable.Plate));
                        }
                    }
                });

                Log($"[{side}] KABUL{(autoApprove ? " + OTO-ONAY" : "")}: '{stable.Plate}' skor={stable.Score:F2}");

                ImageCropHelper.CleanupTempRoi();
            }
            catch (Exception ex) { Log($"[{side}] Detect hata: {ex.Message}"); }
        }

        /// <summary>
        /// Tek kareden plaka okuma sonucu.
        /// Supheli=true ise okuma OTOMATIK ONAYA GIRMEZ; personel dogrular.
        /// </summary>
        private readonly record struct OkumaSonucu(
            string Plate, double Score, bool Supheli, double MinChar, bool Kenarda);

        /// <summary>
        /// Lokal motor SUPHELI bir okuma verdiginde buluta ikinci gorus sorar.
        /// null donerse bulut bir sey soyleyemedi (kota/hata/okuyamadi) -> lokal sonuc gecerli.
        ///
        /// Birlestirme kurali:
        ///   bulut = lokal        -> iki bagimsiz motor uzlasti, SUPHE KALKAR (otomatik onay)
        ///   bulut != lokal, guclu-> BULUTUN okumasi kullanilir, supphe SURER (personel dogrular)
        ///   bulut zayif/yok      -> lokal sonuc, supphe surer
        /// </summary>
        private async Task<OkumaSonucu?> BulutIkinciGorusAsync(
            string imagePath, string lokalPlaka, CancellationToken ct)
        {
            if (!BulutKotaBekcisi.IzinVar(lokalPlaka)) return null;

            try
            {
                var r = await _client.RecognizeAsync(imagePath, null, ct);
                if (r == null || string.IsNullOrWhiteSpace(r.Plate))
                {
                    Log($"Bulut ikinci gorus: okuyamadi (lokal '{lokalPlaka}' supheli kaliyor)");
                    return null;
                }

                var bulutPlaka = PlateRules.Normalize(r.Plate);
                if (bulutPlaka.Length < 5) return null;

                if (string.Equals(bulutPlaka, lokalPlaka, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Bulut ikinci gorus: UZLASMA '{lokalPlaka}' (bulut skor={r.Score:F2}) " +
                        $"-> supphe kalkti, otomatik onaya uygun");
                    return new OkumaSonucu(lokalPlaka, Math.Max(0.90, r.Score), false, r.Score, false);
                }

                // Bulut farkli okudu. Yalnizca KENDINDEN EMINSE onun okumasini al.
                if (r.Score >= 0.90)
                {
                    Log($"Bulut ikinci gorus: FARKLI okuma - lokal '{lokalPlaka}' vs bulut " +
                        $"'{bulutPlaka}' (skor={r.Score:F2}) -> BULUT kullanildi, personel dogrulamali");
                    return new OkumaSonucu(bulutPlaka, 0.90, true, r.Score, false);
                }

                Log($"Bulut ikinci gorus: zayif ('{bulutPlaka}' skor={r.Score:F2}) -> lokal '{lokalPlaka}' kaldi");
                return null;
            }
            catch (Exception ex)
            {
                Log($"Bulut ikinci gorus hatasi: {ex.Message}");
                return null;
            }
        }

        // LOCAL-FIRST mod: Once lokal OCR'i dene, yetmezse API'ye gec (kota tasarrufu)
        private async Task<OkumaSonucu?> RecognizeWithScoreAsync(string imagePath, CancellationToken ct)
        {
            // 1) Lokal multi-engine motor (ONNX YOLO + ONNX OCR + Tesseract+Haar) - tamamen ucretsiz.
            //    LocalPlateRecognizer 70 ulke format kutuphanesine karsi dogrulanmis sonuc dondurur.
            PlateRecognitionResult? localResult = null;
            bool kutuBulundu = false;
            if (_recognizer != null)
            {
                var outcome = await _recognizer.RecognizeDetailedAsync(imagePath, ct);
                kutuBulundu = outcome.KutuBulundu;
                localResult = outcome.Sonuc;
                if (localResult != null && !string.IsNullOrWhiteSpace(localResult.Plate))
                {
                    var normalized = PlateRules.Normalize(localResult.Plate);
                    // Bilinen formata uymasa da KABUL (yabanci plaka). Onceden burada
                    // eleniyor ve bulut API'ye dusuluyordu; bulut da kapaliysa arac kaciyordu.
                    if (normalized.Length >= 5)
                    {
                        // EMIN okuma -> buluta hic gitme, dogrudan kabul.
                        if (!localResult.Supheli)
                        {
                            return new OkumaSonucu(normalized, localResult.Score,
                                false, localResult.MinCharProb, localResult.Kenarda);
                        }

                        // SUPHELI okuma -> bulut IKINCI GORUS olarak sorulur.
                        //
                        // Olculdu: sorunlu 5 karenin 1'ini bulut dogru okudu
                        // (bizim '2CSR324' -> bulut '2CSR322' skor 0.94 = dogrusu),
                        // 4'unu bulut da okuyamadi. Yani bedava bir duzeltme sansi.
                        //
                        // Kota: supheli kareler ayda ~2950 sorgu ederdi (kota 2500/ay).
                        // BulutKotaBekcisi ayni plakayi 60 sn icinde tekrar sormaz ve
                        // saatlik tavan uygular -> ayda ~500 sorguya iner.
                        var ikinciGorus = await BulutIkinciGorusAsync(imagePath, normalized, ct);
                        if (ikinciGorus != null)
                            return ikinciGorus.Value;

                        return new OkumaSonucu(normalized, localResult.Score,
                            true, localResult.MinCharProb, localResult.Kenarda);
                    }
                }
            }

            // 2) Lokal yetmedi - API yedek (kota varsa)
            //
            // KOTA KORUMASI (kritik): buluta SADECE "plaka kutusu bulundu ama okunamadi"
            // durumunda gidilir. Bos koridor karesinde gidilmez.
            // Olcum (1388 gercek kare): 1209 kare bos (%87), 179 karede kutu var,
            // bunlarin sadece 4'u okunamiyor. Bu kontrol olmadan kamera 0.5 sn'de bir
            // kare urettigi icin gunde ~75.000 bosa sorgu yapilir; Plate Recognizer
            // ucretsiz kotasi 2.500/AY, en buyuk plan 500.000/ay. Yani kota saatler
            // icinde tukenir. Bu kontrolle gunluk sorgu birkac yuzu gecmez.
            if (!kutuBulundu)
                return null;

            // Ayni kota bekcisi burada da gecerli (plaka metni yok -> "?" anahtari)
            if (!BulutKotaBekcisi.IzinVar(null))
                return null;

            try
            {
                // Bolge ipucu VERILMIYOR (eskiden "tr" gonderiliyordu).
                // Buraya zaten lokal motorun okuyamadigi kareler dusuyor; bunlarin
                // onemli kismi yabanci plaka. "tr" ipucu yabanci plakayi bozar.
                var r = await _client.RecognizeAsync(imagePath, null, ct);
                if (r != null && !string.IsNullOrWhiteSpace(r.Plate))
                {
                    var plate = PlateRules.Normalize(r.Plate);
                    // Buluttan gelen okuma: buraya zaten lokal motorun okuyamadigi zor
                    // kareler dusuyor, o yuzden DAIMA supheli isaretlenir (personel dogrular).
                    return new OkumaSonucu(plate, r.Score, true, r.Score, false);
                }
            }
            catch (Exception ex)
            {
                Log($"API hata: {ex.Message}");
            }

            // 3) API de yetmedi - lokal sonucu varsa onu dondur (eski 0.70 esiginin altinda kalsa bile)
            if (localResult != null && !string.IsNullOrWhiteSpace(localResult.Plate))
            {
                var normalized = PlateRules.Normalize(localResult.Plate);
                return new OkumaSonucu(normalized, localResult.Score,
                    localResult.Supheli, localResult.MinCharProb, localResult.Kenarda);
            }

            return null;
        }

        /// <summary>
        /// "KAPAT" butonu: kullanici programi dogrudan kapatabilir (yalnizca onay sorulur).
        /// Sifreli kapatma icin kurum logosuna cift tiklama yolu da durmaktadir.
        /// </summary>
        private void CloseApp_Click(object sender, RoutedEventArgs e)
        {
            var cevap = MessageBox.Show(
                "Program kapatılacak. Emin misiniz?",
                "Programdan Çıkış",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (cevap == MessageBoxResult.Yes)
                Application.Current.Shutdown();
        }

        // ===== KURUM LOGOSU -> PROGRAMDAN CIKIS =====

        /// <summary>
        /// Kurum (Kayseri Ulasim) logosuna CIFT TIKLAMA -> yonetici sifresi sorulur,
        /// dogruysa program kapatilir. Uygulama tam ekran (baslik cubugu yok) calistigi
        /// icin kapatmanin tek yolu budur.
        /// </summary>
        private void KurumLogo_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;   // sadece CIFT tiklama

            var dlg = new ExitPasswordWindow { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
                Application.Current.Shutdown();
        }

        // ===== TABLO RESME TIKLAMA -> PLAKA DUZENLEME =====
        // Gridde "Duzelt" butonu KALDIRILDI. Plaka duzeltmek icin satirdaki (giris/cikis)
        // arac fotografina tiklanir: acilan pencerede fotograf + ESKI PLAKA gorunur,
        // YENI PLAKA girilip kaydedilir.

        private async void PlateImage_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border border) return;
            if (border.DataContext is not Otopark.Core.PersonnelDashboardViewModel.VehicleRow row) return;
            if (DataContext is not PersonnelDashboardViewModel vm) return;

            // Kara liste (borc kaydi) ve iptal edilmis girislerde plaka duzeltme yapilamaz.
            if (row.EntryId <= 0 || row.ParkType == "Borclu")
            {
                vm.ShowBarrierToast(false, "Bu kayit icin plaka duzeltme yapilamaz.");
                return;
            }
            if (row.ParkType == "Iptal")
            {
                vm.ShowBarrierToast(false, "Iptal edilmis kaydin plakasi duzeltilemez.");
                return;
            }

            string side = (border.Tag as string) ?? "entry";
            bool isEntry = side == "entry";
            string imgPath = isEntry ? row.EntryPlateImagePath : row.ExitPlateImagePath;

            // Gorsel yoksa da plaka duzeltilebilsin (fotograf paneli bos kalir).
            // http(s) adresleri File.Exists ile kontrol edilemez -> onlari eleme.
            bool imgIsUrl = !string.IsNullOrWhiteSpace(imgPath) &&
                            (imgPath.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
                             imgPath.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(imgPath) && !imgIsUrl && !File.Exists(imgPath))
                imgPath = "";

            string? newPlate = null;
            var popup = new CorrectPlateWindow(row.Plate, imgPath)
            {
                Owner = Window.GetWindow(this)
            };
            if (popup.ShowDialog() == true)
                newPlate = popup.NewPlate;

            if (!string.IsNullOrWhiteSpace(newPlate) && newPlate != row.Plate)
                await vm.ApplyPlateCorrectionAsync(row, newPlate);
        }

        // ===== BARIYER =====

        /// <summary>
        /// GIRIS BARIYERI: otopark doluysa (icerideki arac sayisi >= kapasite) bariyer ACILMAZ,
        /// "Boş yer bulunmamaktadır" uyarisi verilir. Kapasite tanimsizsa (0) kontrol uygulanmaz.
        /// </summary>
        private async void BarrierEntry_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PersonnelDashboardViewModel vm)
            {
                if (vm.TotalCapacity > 0 && vm.CurrentVehicleCount >= vm.TotalCapacity)
                {
                    vm.ShowBarrierToast(false,
                        $"Boş yer bulunmamaktadır ({vm.CurrentVehicleCount}/{vm.TotalCapacity}). Giriş bariyeri açılmaz.");
                    return;
                }
            }

            var result = await Services.BarrierService.OpenEntryGateAsync(beklemeyiAtla: true);
            if (DataContext is PersonnelDashboardViewModel vm2)
                vm2.ShowBarrierToast(result.Success, result.Message);
        }

        /// <summary>
        /// CIKIS BARIYERI: listeden secili aracin (SelectedVehicle) Kapali Otopark ESKI borcu
        /// (OldDebt - VEHICLE_CREDIT'ten gelen, kara liste ile ayni kaynak) varsa bariyer ACILMAZ,
        /// "Borç ödenmeden bariyer açılmaz" uyarisi verilir. Secili arac yoksa (manuel/kamera
        /// tabanli akis) kontrol atlanir - mevcut davranis korunur.
        ///
        /// YIKAMA ISTISNASI: secili aracin aktif yikama fisi varsa VE ucretsiz sure DOLMAMISSA,
        /// borc olsa bile bariyer acilir (otopark ucreti yikama ile karsilanir). Sure DOLMUSSA
        /// normal borc mesaji yerine "yikama ucretsiz sureniz bitti, kiosktan odeyin" denir.
        /// </summary>
        /// <summary>
        /// BORCLU CIKISI YAP: secili araci BORCLANDIRARAK cikarir ve bariyeri acar.
        /// Kuyrukta borcu tahsil edilemeyen arac bedava cikmasin diye kullanilir:
        /// borc kayitli degilse yazilir, cikis islenir, ACIKLAMA'ya personel notu dusulur.
        /// Borc ACIK kalir; arac bir sonraki gelisinde borclu gorunur.
        /// </summary>
        private async void BorcluCikis_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PersonnelDashboardViewModel vm) return;

            if (vm.SelectedVehicle == null)
            {
                vm.ShowBarrierToast(false, "Once listeden arac seciniz.");
                return;
            }

            var sonuc = await vm.BorcluCikisYapAsync(vm.SelectedVehicle);
            if (!string.IsNullOrEmpty(sonuc.mesaj))
                vm.ShowBarrierToast(sonuc.basarili, sonuc.mesaj);
            if (!sonuc.acilsin) return;

            var res = await Services.BarrierService.OpenExitGateAsync(vm.SelectedVehicle?.Plate, beklemeyiAtla: true);
            vm.ShowBarrierToast(res.Success, res.Message);
        }

        /// <summary>
        /// MISAFIR ARAC (24.08.2026): secili araci "misafir" olarak isaretler ve
        /// personelden ACIKLAMA alir.
        ///
        /// UCRET/BORC/CIKIS AKISINA DOKUNMAZ. Kullanici "ucretsiz ciksin" demedi;
        /// isaretlenmesini ve aciklama yazilmasini istedi. Isaret sunucuda
        /// VEHICLE_PLATE_REVISION'a "MISAFIR" olarak loglanir ve web'deki
        /// "Iptal / Plaka Islem Raporu" ekraninda listelenir.
        /// </summary>
        private async void MisafirArac_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PersonnelDashboardViewModel vm) return;

            if (vm.SelectedVehicle == null)
            {
                vm.ShowBarrierToast(false, "Once listeden arac seciniz.");
                return;
            }

            var plaka = vm.SelectedVehicle.Plate ?? "";
            var entryId = vm.SelectedVehicle.EntryId;

            if (entryId <= 0)
            {
                vm.ShowBarrierToast(false, $"{plaka}: giris kaydi bulunamadi, isaretlenemedi.");
                return;
            }

            var pencere = new GuestNoteWindow(plaka) { Owner = Window.GetWindow(this) };
            if (pencere.ShowDialog() != true) return;

            var basarili = await vm.MisafirAracIsaretleAsync(entryId, pencere.Note);

            vm.ShowBarrierToast(basarili,
                basarili
                    ? $"{plaka}: misafir arac olarak isaretlendi."
                    : $"{plaka}: misafir arac isareti KAYDEDILEMEDI. Sunucu guncel mi kontrol ediniz.");
        }

        private async void BarrierExit_Click(object sender, RoutedEventArgs e)
        {
            // MANUEL CIKIS BARIYERI - 20.08.2026'da talep uzerine YENIDEN ACILDI.
            //
            // Personelin kontrolunde, SORGUSUZ acilir; kuyrukta hizli kalmasi icin
            // burada borc kontrolu YAPILMAZ.
            //
            // 18.08'de gecici olarak kapatilmisti. Kapatma gerekcesi hala gecerli,
            // kullanan bilsin diye buraya yaziliyor:
            //   - bariyer acilir ama VEHICLE_PARK_EXIT OLUSMAZ,
            //   - arac sistemde "iceride" kalir; gunluk tahakkuk servisi 24 saatte
            //     bir yeni gun borcu yazmaya devam eder ("cikmis aracin borcu
            //     artiyor" sikayetinin kaynagi budur),
            //   - borclu arac hicbir iz birakmadan cikabilir.
            //
            // Kayit ureterek cikarmak icin "Borclu Cikisi Yap" (BorcluCikis_Click)
            // kullanilmalidir: borc yazilir, cikis islenir, bariyer yine acilir.

            var result = await Services.BarrierService.OpenExitGateAsync(beklemeyiAtla: true);
            if (DataContext is PersonnelDashboardViewModel vm3)
                vm3.ShowBarrierToast(result.Success, result.Message);
        }

        // ===== MANUEL YAKALAMA =====

        private async void EntryCapture_Click(object sender, RoutedEventArgs e)
        {
            await ManualCaptureAsync(EntryCaptureFolder, EntryShotsFolder, true);
        }

        private async void ExitCapture_Click(object sender, RoutedEventArgs e)
        {
            await ManualCaptureAsync(ExitCaptureFolder, ExitShotsFolder, false);
        }

        // Yikama ekranini ayri pencerede ac (VehicleParkApiService VM'den alinir).
        private void Wash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not Otopark.Core.PersonnelDashboardViewModel vm)
                {
                    MessageBox.Show("Oturum bilgisi bulunamadı.", "Yıkama",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var win = new WashWindow(vm.ApiService) { Owner = Window.GetWindow(this) };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yıkama ekranı açılamadı: " + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task ManualCaptureAsync(string captureFolder, string saveDir, bool isEntry)
        {
            try
            {
                Directory.CreateDirectory(saveDir);
                var first = GetLatestImagePath(captureFolder);
                if (first == null) return;

                string prefix = isEntry ? "entry" : "exit";
                string photo1 = Path.Combine(saveDir, $"{prefix}1_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                File.Copy(first, photo1, true);

                await Task.Delay(250);

                var second = GetLatestImagePath(captureFolder);
                if (second == null) return;

                string photo2 = Path.Combine(saveDir, $"{prefix}2_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                File.Copy(second, photo2, true);

                var best = await RecognizeBestOfAsync(new[] { photo1, photo2 }, _cts.Token);
                if (best == null)
                {
                    MessageBox.Show("Plaka tanınamadı.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (DataContext is not PersonnelDashboardViewModel vm) return;

                if (isEntry)
                {
                    vm.EntryPhoto1 = photo1;
                    vm.EntryPhoto2 = photo2;
                    vm.EntryDetectedPlate = best.Value.Plate;
                    string base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(best.Value.UsedImagePath, _cts.Token));
                    vm.SetPendingEntry(best.Value.Plate, base64);
                }
                else
                {
                    vm.ExitPhoto1 = photo1;
                    vm.ExitPhoto2 = photo2;
                    vm.ExitDetectedPlate = best.Value.Plate;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Manuel yakalama hatası:\n" + ex.Message);
            }
        }

        private async Task<(string Plate, double Score, string UsedImagePath)?> RecognizeBestOfAsync(
            string[] images, CancellationToken ct)
        {
            (string Plate, double Score, string UsedImagePath)? best = null;
            foreach (var img in images)
            {
                var r = await RecognizeWithScoreAsync(img, ct);
                if (r == null) continue;
                if (!PlateRules.IsLikelyPlate(r.Value.Plate)) continue;
                if (r.Value.Score < 0.40) continue;
                if (best == null || r.Value.Score > best.Value.Score)
                    best = (r.Value.Plate, r.Value.Score, img);
            }
            return best;
        }

        // ===== GORSEL YUKLEME =====

        private DateTime _lastFolderLog = DateTime.MinValue;

        /// <summary>UI timer'i yeniden girmesin (tarama diskten donmeden yeni tick baslamasin).</summary>
        private bool _uiBusy;

        /// <summary>
        /// Kamera gorsellerini tazeler.
        ///
        /// ESKI HALI (yavaslik kaynagi): 400 ms'de bir, UI THREAD'inde, klasor basina IKI
        /// ayri tarama (buyuk gorsel icin 1 + kucukler icin 1) x 2 klasor = tick basina
        /// DORT tam tarama. Her tarama tum klasoru FileInfo'ya cevirip LastWriteTimeUtc'ye
        /// gore siraliyordu. 20.000 dosyada tick basina ~4,3 sn -> arayuz doniyordu.
        ///
        /// YENI HALI:
        ///   - Klasor basina TEK tarama, ikisi de tek Task.Run icinde (UI thread'de is yok)
        ///   - Siralama dosya ADINA gore (snap_yyyyMMdd_HHmmss_fff zaten kronolojik) ->
        ///     dosya basina stat cagrisi YOK
        ///   - Yalnizca son 3 dosya secilir; tam siralama yerine tek gecis
        ///   - Tani logunun dosya sayisi ayni taramadan gelir (ekstra GetFiles YOK)
        /// </summary>
        private void LoadLatestImages()
        {
            if (_uiBusy) return;
            if (DataContext is not PersonnelDashboardViewModel) return;

            _uiBusy = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    // --- DISK ISI: UI thread'in disinda ---
                    var entry = SonGorseller(EntryCaptureFolder, 3);
                    var exit = SonGorseller(ExitCaptureFolder, 3);

                    bool logZamani = (DateTime.Now - _lastFolderLog).TotalSeconds >= 30;
                    if (logZamani) _lastFolderLog = DateTime.Now;

                    // --- YALNIZCA ATAMALAR UI thread'de ---
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (DataContext is not PersonnelDashboardViewModel vm) return;

                        if (entry.Dosyalar.Length > 0)
                        {
                            vm.EntryCameraImagePath = entry.Dosyalar[0];
                            vm.EntryPhoto1 = entry.Dosyalar[0];
                            if (entry.Dosyalar.Length > 1) vm.EntryPhoto2 = entry.Dosyalar[1];
                        }

                        if (exit.Dosyalar.Length > 0)
                        {
                            vm.ExitCameraImagePath = exit.Dosyalar[0];
                            vm.ExitPhoto1 = exit.Dosyalar[0];
                            if (exit.Dosyalar.Length > 1) vm.ExitPhoto2 = exit.Dosyalar[1];
                        }

                        if (logZamani)
                        {
                            Log($"UI: Entry={EntryCaptureFolder} ({entry.ToplamDosya} dosya) | " +
                                $"Exit={ExitCaptureFolder} ({exit.ToplamDosya} dosya)");
                            Log($"UI: EntryCam='{vm.EntryCameraImagePath}' | ExitCam='{vm.ExitCameraImagePath}'");
                        }
                    });
                }
                catch (Exception ex) { Log($"LoadLatestImages hata: {ex.Message}"); }
                finally { _uiBusy = false; }
            });
        }

        // ===== YARDIMCI =====

        /// <summary>
        /// Klasordeki EN YENI n gorseli TEK taramada dondurur (yeniden eskiye).
        ///
        /// Siralama dosya ADINA gore yapilir: kamera dosyalari
        /// "snap_yyyyMMdd_HHmmss_fff.jpg" formatinda oldugu icin metin sirasi = zaman sirasi.
        /// Boylece dosya basina FileInfo/stat cagrisi tamamen ortadan kalkar (asil maliyet oydu).
        ///
        /// Tam siralama da yapilmaz; tek gecisle yalnizca en buyuk n ad tutulur.
        /// </summary>
        private static (string[] Dosyalar, int ToplamDosya) SonGorseller(string folder, int n)
        {
            if (!Directory.Exists(folder)) return (Array.Empty<string>(), 0);

            string[] hepsi;
            try { hepsi = Directory.GetFiles(folder, "*.*"); }
            catch { return (Array.Empty<string>(), 0); }

            // En buyuk n adi tutan kucuk liste (n=3, yani siralama maliyeti onemsiz)
            var enYeniler = new List<string>(n + 1);
            int gorselSayisi = 0;

            foreach (var yol in hepsi)
            {
                if (!IsImageFile(yol)) continue;
                gorselSayisi++;

                var ad = Path.GetFileName(yol);
                int i = 0;
                while (i < enYeniler.Count &&
                       string.CompareOrdinal(Path.GetFileName(enYeniler[i]), ad) > 0)
                {
                    i++;
                }
                if (i < n)
                {
                    enYeniler.Insert(i, yol);
                    if (enYeniler.Count > n) enYeniler.RemoveAt(n);
                }
            }

            return (enYeniler.ToArray(), gorselSayisi);
        }

        /// <summary>Klasordeki en yeni gorsel (tek tarama, ada gore).</summary>
        private static string? GetLatestImagePath(string folder)
        {
            var (dosyalar, _) = SonGorseller(folder, 1);
            return dosyalar.Length > 0 ? dosyalar[0] : null;
        }

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp";
        }

        /// <summary>
        /// Plaka okundugu anda resmi kaydeder.
        /// Dosya adi: C:\Otopark\ImageCache\{PLAKA}_E_{yyyyMMddHHmmss}.jpg (giris)
        ///        veya C:\Otopark\ImageCache\{PLAKA}_X_{yyyyMMddHHmmss}.jpg (cikis)
        /// Sunucudan veri cekildiginde plaka + timestamp eslesmesiyle bulunabilir.
        /// </summary>
        private static string[] SavePlateSnapshots(string plate, string captureFolder, string recognizedImagePath, bool isEntry)
        {
            try
            {
                var safePlate = string.Concat(plate.Split(Path.GetInvalidFileNameChars()));
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var prefix = isEntry ? "E" : "X";
                var cacheDir = @"C:\Otopark\ImageCache\";
                Directory.CreateDirectory(cacheDir);

                // Kaynak resmi sec: oncelik recognizedImagePath, yoksa klasordeki son resim
                string? source = null;
                if (!string.IsNullOrEmpty(recognizedImagePath) && File.Exists(recognizedImagePath))
                    source = recognizedImagePath;
                else
                {
                    source = GetLatestImagePath(captureFolder);
                }

                if (source == null) return Array.Empty<string>();

                var dest = Path.Combine(cacheDir, $"{safePlate}_{prefix}_{timestamp}.jpg");
                File.Copy(source, dest, true);
                return new[] { dest };
            }
            catch { return Array.Empty<string>(); }
        }

        private static async Task<bool> WaitUntilFileReady(string path, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                try
                {
                    using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (s.Length > 0) return true;
                }
                catch { }
                await Task.Delay(100, ct);
            }
            return false;
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
