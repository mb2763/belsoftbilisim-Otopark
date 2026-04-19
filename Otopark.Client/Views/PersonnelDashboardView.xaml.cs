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
        private readonly DispatcherTimer _uiTimer = new();
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

        private const string PlateRecognizerToken = "2059e14b4a694207a913240af6da257abd38092e";

        // Daha dusuk minScore + gevsek stabilizer: hareket halindeki aracin bulanik
        // frame'lerini de yakala, yuksek skorlu (>=0.85) frame varsa tek hit'de kabul et.
        private readonly PlateStabilizer _entryStabilizer = new(minScore: 0.55, windowSeconds: 4.0, neededHits: 1);
        private readonly PlateStabilizer _exitStabilizer = new(minScore: 0.55, windowSeconds: 4.0, neededHits: 1);
        private readonly DuplicateSuppressor _entrySuppressor = new(suppressSeconds: 8.0);
        private readonly DuplicateSuppressor _exitSuppressor = new(suppressSeconds: 8.0);

        private readonly PlateRecognizerClient _client = new(PlateRecognizerToken);

        public PersonnelDashboardView()
        {
            InitializeComponent();
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
                _entryWatcher?.Dispose();
                _exitWatcher?.Dispose();
                _cts.Cancel();
            }
            catch { }
        }

        // ===== TIMER =====

        private void StartUiTimer()
        {
            _uiTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _uiTimer.Tick += async (_, __) =>
            {
                if (_tickBusy) return;
                _tickBusy = true;
                try
                {
                    LoadLatestImages();
                    await DetectFromFolderAsync(EntryCaptureFolder, isEntry: true, _cts.Token);
                    await DetectFromFolderAsync(ExitCaptureFolder, isEntry: false, _cts.Token);
                }
                finally { _tickBusy = false; }
            };
            _uiTimer.Start();
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
                // 1) Once tum goruntuyle dene
                var best = await RecognizeWithScoreAsync(imagePath, ct);

                // 2) ROI zoom etkinse ve ilk deneme zayifsa, kirpilmis goruntuyle de dene; en iyi secilir
                var roiEnabled = bool.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:Enabled"], out var re) && re;
                if (roiEnabled && (best == null || best.Value.Score < 0.90))
                {
                    double xp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:XPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0.15;
                    double yp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:YPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : 0.30;
                    double wp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:WidthPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0.70;
                    double hp = double.TryParse(Otopark.Core.Services.AppConfig.Configuration["DetectionRoi:HeightPercent"],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 0.65;

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

                // Genel plaka formati (Turk + yabanci, tabela kelimeleri haric)
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

                // Stabilizer: yuksek skorlu ilk hit direkt gecer
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

                // Plaka okundugu anda snapshot'lari kaydet
                var captureFolder = isEntry ? EntryCaptureFolder : ExitCaptureFolder;
                var savedSnapshots = SavePlateSnapshots(stable.Plate, captureFolder, imagePath);

                bool autoApprove = isEntry
                    ? (bool.TryParse(Otopark.Core.Services.AppConfig.Configuration["AutoApprove:Entry"], out var ae) && ae)
                    : (bool.TryParse(Otopark.Core.Services.AppConfig.Configuration["AutoApprove:Exit"], out var ax) && ax);

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (DataContext is not PersonnelDashboardViewModel vm) return;

                    if (isEntry)
                    {
                        vm.EntryDetectedPlate = stable.Plate;
                        vm.EntryPlateSnapshotPaths = savedSnapshots;
                        var base64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
                        vm.SetPendingEntry(stable.Plate, base64);

                        if (autoApprove && vm.ApproveEntryCommand.CanExecute(null))
                            await vm.ApproveEntryCommand.ExecuteAsync(null);
                    }
                    else
                    {
                        vm.ExitDetectedPlate = stable.Plate;
                        vm.ExitPlateSnapshotPaths = savedSnapshots;

                        if (autoApprove && vm.ApproveExitCommand.CanExecute(null))
                            await vm.ApproveExitCommand.ExecuteAsync(null);
                    }
                });

                Log($"[{side}] KABUL{(autoApprove ? " + OTO-ONAY" : "")}: '{stable.Plate}' skor={stable.Score:F2}");

                // Temp ROI dosyalarini ara sira temizle
                ImageCropHelper.CleanupTempRoi();
            }
            catch (Exception ex) { Log($"[{side}] Detect hata: {ex.Message}"); }
        }

        /// <summary>
        /// Bir goruntuden plaka okur. Format gecen en yuksek skorlu adayi dondurur.
        /// </summary>
        private async Task<(string Plate, double Score)?> RecognizeWithScoreAsync(string imagePath, CancellationToken ct)
        {
            var r = await _client.RecognizeAsync(imagePath, null, ct);
            if (r == null || string.IsNullOrWhiteSpace(r.Plate)) return null;
            var plate = PlateRules.Normalize(r.Plate);
            return (plate, r.Score);
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
                var r = await _client.RecognizeAsync(img, null, ct);
                if (r == null) continue;
                var plate = PlateRules.Normalize(r.Plate);
                if (!PlateRules.IsLikelyPlate(plate)) continue;
                if (r.Score < 0.55) continue;
                if (best == null || r.Score > best.Value.Score)
                    best = (plate, r.Score, img);
            }
            return best;
        }

        // ===== GORSEL YUKLEME =====

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

            // Cikis buyuk gorsel (ExitCaptures yoksa EntryCaptures'tan al)
            var exitImg = GetLatestImageFile(ExitCaptureFolder);
            if (exitImg == null || exitImg.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-5))
            {
                var fallback = GetLatestImageFile(EntryCaptureFolder);
                if (fallback != null && (exitImg == null || fallback.LastWriteTimeUtc > exitImg.LastWriteTimeUtc))
                    exitImg = fallback;
            }
            if (exitImg != null) vm.ExitCameraImagePath = exitImg.FullName;

            // Cikis son 2 kucuk gorsel
            var exitFiles = GetLatestImageFiles(ExitCaptureFolder, 2);
            if (exitFiles.Length < 2)
                exitFiles = GetLatestImageFiles(EntryCaptureFolder, 2);
            if (exitFiles.Length > 0) vm.ExitPhoto1 = exitFiles[0];
            if (exitFiles.Length > 1) vm.ExitPhoto2 = exitFiles[1];
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
        /// Plaka okundugu anda snapshot'lari kaydeder.
        /// 1. resim: plakanin tam taninan dosyasi (recognizedImagePath)
        /// 2-3. resim: klasordeki son snapshot'lar (ayni dosya hariç)
        /// Dosya adi: C:\Otopark\ImageCache\PLAKA_yyyyMMdd_HHmmss_1.jpg
        /// </summary>
        private static string[] SavePlateSnapshots(string plate, string captureFolder, string recognizedImagePath)
        {
            try
            {
                var safePlate = string.Concat(plate.Split(Path.GetInvalidFileNameChars()));
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var cacheDir = @"C:\Otopark\ImageCache\";
                Directory.CreateDirectory(cacheDir);

                var sources = new System.Collections.Generic.List<string>();

                // 1. Taninan resim her zaman ilk sirada
                if (!string.IsNullOrEmpty(recognizedImagePath) && File.Exists(recognizedImagePath))
                    sources.Add(recognizedImagePath);

                // 2-3. Klasordeki son snapshot'lardan taninan disindakileri ekle
                foreach (var f in GetLatestImageFiles(captureFolder, 4))
                {
                    if (sources.Count >= 3) break;
                    if (!string.Equals(f, recognizedImagePath, StringComparison.OrdinalIgnoreCase))
                        sources.Add(f);
                }

                if (sources.Count == 0) return Array.Empty<string>();

                var saved = new System.Collections.Generic.List<string>();
                for (int i = 0; i < sources.Count; i++)
                {
                    var dest = Path.Combine(cacheDir, $"{safePlate}_{timestamp}_{i + 1}.jpg");
                    File.Copy(sources[i], dest, true);
                    saved.Add(dest);
                }
                return saved.ToArray();
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
