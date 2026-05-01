using Otopark.Client.Helpers;
using Otopark.Core;
using System;
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
        private readonly SemaphoreSlim _entryGate = new(1, 1);
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

        // PlateRecognizer Cloud API token - calisan eski versiyondaki ayni
        private const string PlateRecognizerToken = "2059e14b4a694207a913240af6da257abd38092e";

        private readonly PlateStabilizer _entryStabilizer = new(minScore: 0.55, windowSeconds: 4.0, neededHits: 1);
        private readonly PlateStabilizer _exitStabilizer = new(minScore: 0.55, windowSeconds: 4.0, neededHits: 1);
        private readonly DuplicateSuppressor _entrySuppressor = new(suppressSeconds: 8.0);
        private readonly DuplicateSuppressor _exitSuppressor = new(suppressSeconds: 8.0);

        // Birincil: PlateRecognizer Cloud API (calisan eski versiyon)
        private readonly PlateRecognizerClient _client = new(PlateRecognizerToken);
        // Yedek: lokal OCR (API kota/network sorunu olursa devreye girer)
        private LocalPlateRecognizer? _recognizer;

        public PersonnelDashboardView()
        {
            InitializeComponent();

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

            Start();
            Loaded += (_, __) => Start();
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

                    vm.OnCorrectPlateRequested += async (row) =>
                    {
                        string? newPlate = null;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var popup = new CorrectPlateWindow(row.Plate);
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

            Services.CameraSnapshotService.Start(EntryCaptureFolder, ExitCaptureFolder, _cts.Token);
            StartUiTimer();
            StartWatchers();
        }

        private void Stop()
        {
            try
            {
                _uiTimer.Stop();
                _detectTimer.Stop();
                _entryWatcher?.Dispose();
                _exitWatcher?.Dispose();
                _cts.Cancel();
            }
            catch { }
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
            if (!await WaitUntilFileReady(path, _cts.Token)) return;

            var gate = isEntry ? _entryGate : _exitGate;
            if (!await gate.WaitAsync(0)) return;

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

            await TryDetectAndSetAsync(latest.FullName, isEntry, ct);
        }

        private async Task TryDetectAndSetAsync(string imagePath, bool isEntry, CancellationToken ct)
        {
            string side = isEntry ? "giris" : "cikis";
            try
            {
                // 1) Tam goruntu - TR bolge ipucu ile (Turk plakalarinda %90+ dogruluk)
                var best = await RecognizeWithScoreAsync(imagePath, ct);

                // 2) ROI kirpma: skor < 0.80 veya sonuc yoksa her zaman dene
                if (best == null || best.Value.Score < 0.80)
                {
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
                }

                if (best == null)
                {
                    Log($"[{side}] Plaka yok: {Path.GetFileName(imagePath)}");
                    return;
                }

                string plate = best.Value.Plate;
                double score = best.Value.Score;

                if (!PlateRules.IsLikelyPlate(plate))
                {
                    Log($"[{side}] Red (format/kelime): '{plate}' skor={score:F2}");
                    return;
                }

                if (score < 0.55)
                {
                    Log($"[{side}] Red (skor dusuk): '{plate}' skor={score:F2}");
                    return;
                }

                var stabilizer = isEntry ? _entryStabilizer : _exitStabilizer;
                var stable = stabilizer.Push(plate, score, DateTime.UtcNow);
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
                var savedSnapshots = SavePlateSnapshots(stable.Plate, captureFolder, imagePath, isEntry);

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
                        var base64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
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

        // Once API'yi dene; basarisizsa (timeout/kota/network) yedek olarak lokal OCR
        private async Task<(string Plate, double Score)?> RecognizeWithScoreAsync(string imagePath, CancellationToken ct)
        {
            try
            {
                var r = await _client.RecognizeAsync(imagePath, null, ct);
                if (r != null && !string.IsNullOrWhiteSpace(r.Plate))
                {
                    var plate = PlateRules.Normalize(r.Plate);
                    return (plate, r.Score);
                }
            }
            catch (Exception ex)
            {
                Log($"API hata, lokal yedege geciliyor: {ex.Message}");
            }

            // Yedek: lokal OCR
            if (_recognizer != null)
            {
                var local = await _recognizer.RecognizeAsync(imagePath, ct);
                if (local != null && !string.IsNullOrWhiteSpace(local.Plate))
                    return (local.Plate, local.Score);
            }

            return null;
        }

        // ===== TABLO RESIM CIFT TIKLAMA =====

        private void PlateImage_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return; // sadece cift tiklama
            if (sender is not System.Windows.Controls.Border border) return;
            if (border.DataContext is not Otopark.Core.PersonnelDashboardViewModel.VehicleRow row) return;

            string side = (border.Tag as string) ?? "entry";
            bool isEntry = side == "entry";
            string imgPath = isEntry ? row.EntryPlateImagePath : row.ExitPlateImagePath;
            string title = isEntry ? "Giris Plaka Goruntusu" : "Cikis Plaka Goruntusu";

            if (string.IsNullOrWhiteSpace(imgPath) || !File.Exists(imgPath))
            {
                if (DataContext is PersonnelDashboardViewModel vm)
                    vm.ShowBarrierToast(false, "Bu kayit icin gorsel yok.");
                return;
            }

            var popup = new PlateImagePopup(row.Plate, imgPath, title)
            {
                Owner = Window.GetWindow(this)
            };
            popup.ShowDialog();
        }

        // ===== BARIYER =====

        private async void BarrierEntry_Click(object sender, RoutedEventArgs e)
        {
            var result = await Services.BarrierService.OpenEntryGateAsync();
            if (DataContext is PersonnelDashboardViewModel vm)
                vm.ShowBarrierToast(result.Success, result.Message);
        }

        private async void BarrierExit_Click(object sender, RoutedEventArgs e)
        {
            var result = await Services.BarrierService.OpenExitGateAsync();
            if (DataContext is PersonnelDashboardViewModel vm)
                vm.ShowBarrierToast(result.Success, result.Message);
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
                if (r.Value.Score < 0.55) continue;
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
