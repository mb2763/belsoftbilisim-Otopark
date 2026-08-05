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
            Unloaded += (_, __) => Stop();

            DataContextChanged += (_, __) =>
            {
                if (DataContext is PersonnelDashboardViewModel vm)
                {
                    vm.OnOpenEntryGateRequested += async () =>
                    {
                        var r = await Services.BarrierService.OpenEntryGateAsync();
                        Dispatcher.Invoke(() => vm.ShowBarrierToast(r.Success, r.Message));
                    };

                    vm.OnOpenExitGateRequested += async () =>
                    {
                        var r = await Services.BarrierService.OpenExitGateAsync();
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
            Services.CameraSnapshotService.Start(EntryCaptureFolder, ExitCaptureFolder, _cameraCts.Token);

            // Kamera cozulemediyse kullaniciya SEBEBINI bildir (eskiden sessizce goruntu gelmiyordu).
            if (!string.IsNullOrWhiteSpace(Services.CameraDiag.LastError) && DataContext is PersonnelDashboardViewModel vmErr)
                vmErr.ShowBarrierToast(false, "Kamera: " + Services.CameraDiag.LastError);
        }

        private void Stop()
        {
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

            var latest = GetLatestImageFile(folder);

            // Cikis klasorunde yeni dosya yoksa giris klasorunden dene
            if (!isEntry && (latest == null || latest.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-5)))
            {
                var entryLatest = GetLatestImageFile(EntryCaptureFolder);
                if (entryLatest != null && (latest == null || entryLatest.LastWriteTimeUtc > latest.LastWriteTimeUtc))
                    latest = entryLatest;
            }

            if (latest == null) return;

            var lastFile = isEntry ? _lastEntryFile : _lastExitFile;
            var lastWrite = isEntry ? _lastEntryWriteUtc : _lastExitWriteUtc;

            if (latest.FullName == lastFile && latest.LastWriteTimeUtc == lastWrite)
                return;

            if (!await WaitUntilFileReady(latest.FullName, ct)) return;

            if (isEntry) { _lastEntryFile = latest.FullName; _lastEntryWriteUtc = latest.LastWriteTimeUtc; }
            else { _lastExitFile = latest.FullName; _lastExitWriteUtc = latest.LastWriteTimeUtc; }

            // Watcher yolu (OnNewImageAsync) bu gate'i aliyordu ama timer yolu ALMIYORDU.
            // Sonuc: ayni stabilizer'a iki thread'den es zamanli Push -> buffer bozulmasi
            // ve yanlis/null "en net kare" secimi. Artik iki yol da ayni gate'ten geciyor.
            var gate = isEntry ? _entryGate : _exitGate;
            if (!await gate.WaitAsync(500, ct)) return;   // mesgulse pas gec, sonraki tick yakalar

            try
            {
                await TryDetectAndSetAsync(latest.FullName, isEntry, ct);
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

                // 2) ROI kirpma: skor < 0.80 veya sonuc yoksa her zaman dene
                if (best == null || best.Value.Score < 0.80)
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
                        if (roiBest != null && (best == null || roiBest.Value.Score > best.Value.Score))
                        {
                            Log($"[{side}] ROI daha iyi: {(best?.Score ?? 0):F2} -> {roiBest.Value.Score:F2}");
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

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (DataContext is not PersonnelDashboardViewModel vm)
                    {
                        Log($"[{side}] OTO-ONAY iptal: DataContext bos");
                        return;
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
                            if (vm.ApproveEntryCommand.CanExecute(null))
                            {
                                Log($"[{side}] OTO-ONAY tetikleniyor: '{stable.Plate}'");
                                await vm.ApproveEntryCommand.ExecuteAsync(null);
                            }
                            else
                            {
                                Log($"[{side}] OTO-ONAY iptal: ApproveEntryCommand.CanExecute=false");
                            }
                        }
                    }
                    else
                    {
                        vm.ExitDetectedPlate = stable.Plate;
                        vm.ExitPlateSnapshotPaths = savedSnapshots;
                        vm.ExitEntryImagePath = vm.GetEntryImageForPlate(stable.Plate);

                        if (autoApprove)
                        {
                            if (vm.ApproveExitCommand.CanExecute(null))
                            {
                                Log($"[{side}] OTO-ONAY tetikleniyor: '{stable.Plate}'");
                                await vm.ApproveExitCommand.ExecuteAsync(null);
                            }
                            else
                            {
                                Log($"[{side}] OTO-ONAY iptal: ApproveExitCommand.CanExecute=false");
                            }
                        }
                    }
                });

                Log($"[{side}] KABUL{(autoApprove ? " + OTO-ONAY" : "")}: '{stable.Plate}' skor={stable.Score:F2}");

                ImageCropHelper.CleanupTempRoi();
            }
            catch (Exception ex) { Log($"[{side}] Detect hata: {ex.Message}"); }
        }

        // LOCAL-FIRST mod: Once lokal OCR'i dene, yetmezse API'ye gec (kota tasarrufu)
        private async Task<(string Plate, double Score)?> RecognizeWithScoreAsync(string imagePath, CancellationToken ct)
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
                        return (normalized, localResult.Score);
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

            try
            {
                // Bolge ipucu VERILMIYOR (eskiden "tr" gonderiliyordu).
                // Buraya zaten lokal motorun okuyamadigi kareler dusuyor; bunlarin
                // onemli kismi yabanci plaka. "tr" ipucu yabanci plakayi bozar.
                var r = await _client.RecognizeAsync(imagePath, null, ct);
                if (r != null && !string.IsNullOrWhiteSpace(r.Plate))
                {
                    var plate = PlateRules.Normalize(r.Plate);
                    return (plate, r.Score);
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
                return (normalized, localResult.Score);
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

            var result = await Services.BarrierService.OpenEntryGateAsync();
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

            var res = await Services.BarrierService.OpenExitGateAsync();
            vm.ShowBarrierToast(res.Success, res.Message);
        }

        private async void BarrierExit_Click(object sender, RoutedEventArgs e)
        {
            // MANUEL CIKIS BARIYERI: personelin kontrolunde, SORGUSUZ acilir.
            // Kuyrukta hizli kalmasi icin burada borc kontrolu YAPILMAZ.
            // Borclu araci kayit altina alarak cikarmak icin ayri "Borclu Cikisi Yap"
            // butonu vardir (BorcluCikis_Click).

            var result = await Services.BarrierService.OpenExitGateAsync();
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
                var first = GetLatestImageFile(captureFolder)?.FullName;
                if (first == null) return;

                string prefix = isEntry ? "entry" : "exit";
                string photo1 = Path.Combine(saveDir, $"{prefix}1_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                File.Copy(first, photo1, true);

                await Task.Delay(250);

                var second = GetLatestImageFile(captureFolder)?.FullName;
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

        private void LoadLatestImages()
        {
            if (DataContext is not PersonnelDashboardViewModel vm) return;

            // Giris buyuk gorsel
            var entryImg = GetLatestImageFile(EntryCaptureFolder);
            if (entryImg != null) vm.EntryCameraImagePath = entryImg.FullName;

            // Giris son 2 kucuk gorsel
            var entryFiles = GetLatestImageFiles(EntryCaptureFolder, 2);
            if (entryFiles.Length > 0) vm.EntryPhoto1 = entryFiles[0];
            if (entryFiles.Length > 1) vm.EntryPhoto2 = entryFiles[1];

            // Cikis buyuk gorsel
            var exitImg = GetLatestImageFile(ExitCaptureFolder);
            if (exitImg != null) vm.ExitCameraImagePath = exitImg.FullName;

            // Cikis son 2 kucuk gorsel
            var exitFiles = GetLatestImageFiles(ExitCaptureFolder, 2);
            if (exitFiles.Length > 0) vm.ExitPhoto1 = exitFiles[0];
            if (exitFiles.Length > 1) vm.ExitPhoto2 = exitFiles[1];

            // Tani log: 30 saniyede bir, klasor durumunu logla
            if ((DateTime.Now - _lastFolderLog).TotalSeconds >= 30)
            {
                _lastFolderLog = DateTime.Now;
                int eCount = Directory.Exists(EntryCaptureFolder) ? Directory.GetFiles(EntryCaptureFolder, "*.jpg").Length : -1;
                int xCount = Directory.Exists(ExitCaptureFolder) ? Directory.GetFiles(ExitCaptureFolder, "*.jpg").Length : -1;
                Log($"UI: Entry={EntryCaptureFolder} ({eCount} dosya) | Exit={ExitCaptureFolder} ({xCount} dosya)");
                Log($"UI: EntryCam='{vm.EntryCameraImagePath}' | ExitCam='{vm.ExitCameraImagePath}'");
            }
        }

        // ===== YARDIMCI =====

        private static FileInfo? GetLatestImageFile(string folder)
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.GetFiles(folder, "*.*")
                .Where(IsImageFile)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static string[] GetLatestImageFiles(string folder, int count)
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.GetFiles(folder, "*.*")
                .Where(IsImageFile)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(count)
                .Select(f => f.FullName)
                .ToArray();
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
                    var latest = GetLatestImageFiles(captureFolder, 1);
                    if (latest.Length > 0) source = latest[0];
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
