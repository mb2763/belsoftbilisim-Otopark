using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Otopark.Api;
using Otopark.Api.Services;
using Otopark.Client.Views;
using Otopark.Core;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Otopark.Client;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ===== CLI TEST MODU =====
        // Kullanim: Otopark.Client.exe --test-plate <resim_yolu> [--no-ui]
        // Lokal motoru calistirir, sonucu log + (varsayilan) MessageBox ile gosterir.
        if (e.Args.Length >= 2 && e.Args[0] == "--test-plate")
        {
            bool noUi = e.Args.Length >= 3 && e.Args[2] == "--no-ui";
            RunPlateTest(e.Args[1], noUi);
            Shutdown();
            return;
        }

        // Otopark.Client.exe --download-models
        if (e.Args.Length >= 1 && e.Args[0] == "--download-models")
        {
            try
            {
                Otopark.Client.Helpers.PlateModelDownloader.EnsureModelsAsync(default).Wait();
                MessageBox.Show("Model indirme tamamlandi. Log: C:\\Otopark\\log.txt",
                    "Model Indirme", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Hata: " + ex.Message, "Model Indirme",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

        // Yakalanamamis exception'lari logla (uygulama crash olmasin, log dosyasina yazsin).
        // OperationCanceledException uygulama kapanirken normal - log'lama.
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            if (ex.ExceptionObject is OperationCanceledException) return;
            LogCrash("AppDomain", ex.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, ex) =>
        {
            LogCrash("Dispatcher", ex.Exception);
            ex.Handled = true; // crash etme
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            LogCrash("Task", ex.Exception);
            ex.SetObserved();
        };

        // OpenCvSharp native DLL (OpenCvSharpExtern.dll + VC++ runtime) self-test.
        // Native DLL yoksa lokal OCR motoru calisamaz; kullaniciya net mesaj gosterip
        // uygulamayi sadece API ile baslat. Crash etmesini onlerizki finalizer thread'de
        // GC.Collect() patlamasin.
        if (!OpenCvNativeSelfTest(out var nativeError))
        {
            MessageBox.Show(
                "OpenCvSharp native kutuphanesi yuklenemedi:\n\n" + nativeError +
                "\n\nGerekli adimlar:\n" +
                "1) Visual C++ Redistributable x64 yukleyin:\n" +
                "   https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                "2) EXE'nin yaninda OpenCvSharpExtern.dll oldugundan emin olun.\n" +
                "   (publish veya debug klasoru kopyalanirken runtimes/win-x64/native\n" +
                "    alt klasoru atlanmis olabilir.)\n\n" +
                "Uygulama lokal plaka tanima motoru olmadan calisacak.",
                "Plaka tanima motoru baslatilmadi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // API Base URL
                var baseUrl = "http://web.belsoft.com.tr:221/";

                // HttpClient
                services.AddSingleton(new ApiOptions { BaseUrl = baseUrl });
                services.AddSingleton(sp =>
                    ApiClientFactory.Create(
                        sp.GetRequiredService<ApiOptions>().BaseUrl
                    )
                );

                // API Services
                services.AddSingleton<AuthApiService>();
                services.AddSingleton<ZoneApiService>();

                // Main Navigation VM
                services.AddSingleton<MainViewModel>();

                // Login VM
                services.AddSingleton<LoginViewModel>();

                // Views
                services.AddSingleton<LoginView>();
                services.AddSingleton<PersonnelDashboardView>();

                // MainWindow
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // ✅ MainWindow aç
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();

        // ✅ Login ekranını başlangıçta yükle
        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        var loginVm = _host.Services.GetRequiredService<LoginViewModel>();

        mainVm.Navigate(loginVm);

        mainWindow.Show();

        base.OnStartup(e);
    }

    /// <summary>
    /// CLI test modu: bir resimde plaka tanimaya calisir, sonucu MessageBox + log'a yazar.
    /// </summary>
    private static void RunPlateTest(string imagePath, bool noUi = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plate Test - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Resim: {imagePath}");
        sb.AppendLine();

        try
        {
            if (!File.Exists(imagePath))
            {
                sb.AppendLine("HATA: Dosya bulunamadi.");
                ShowTestResult(sb.ToString(), noUi, imagePath);
                return;
            }

            // OpenCv native test
            if (!OpenCvNativeSelfTest(out var ncErr))
            {
                sb.AppendLine($"HATA: OpenCvSharp native yuklenemedi:\n{ncErr}");
                ShowTestResult(sb.ToString(), noUi, imagePath);
                return;
            }

            sb.AppendLine("OpenCvSharp: OK");

            using var rec = new Otopark.Client.Helpers.LocalPlateRecognizer();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = rec.RecognizeAsync(imagePath, default).GetAwaiter().GetResult();
            sw.Stop();

            sb.AppendLine($"Sure: {sw.ElapsedMilliseconds} ms");
            sb.AppendLine();

            if (result == null || string.IsNullOrWhiteSpace(result.Plate))
            {
                sb.AppendLine("SONUC: Plaka tanimadi (null/bos).");
                sb.AppendLine();
                sb.AppendLine("Olasi sebepler:");
                sb.AppendLine("- Plaka net gorunmuyor / cok kucuk");
                sb.AppendLine("- ONNX modeli henuz indirilmedi (ilk acilista internet ile inilir)");
                sb.AppendLine("- Haar cascade plaka bolgesini bulamadi");
                sb.AppendLine("- Tesseract karakter okuyamadi");
            }
            else
            {
                sb.AppendLine($"PLAKA: {result.Plate}");
                sb.AppendLine($"SKOR:  {result.Score:F2}");

                var match = Otopark.Client.Helpers.Plate.PlateFormatLibrary.Match(result.Plate);
                if (match != null)
                    sb.AppendLine($"FORMAT: ({match.CountryCode}) {match.Pattern}");
                else
                    sb.AppendLine("FORMAT: Bilinmeyen");
            }

            sb.AppendLine();
            sb.AppendLine($"Format kutuphanesi: {Otopark.Client.Helpers.Plate.PlateFormatLibrary.All.Count} pattern, {Otopark.Client.Helpers.Plate.PlateFormatLibrary.ByCountry.Count} ulke");
            sb.AppendLine($"ONNX detector: {(File.Exists(@"C:\Otopark\models\plate_detector.onnx") ? "var" : "yok")}");
            sb.AppendLine($"ONNX OCR: {(File.Exists(@"C:\Otopark\models\plate_ocr.onnx") ? "var" : "yok")}");
            sb.AppendLine($"Haar cascade: {(File.Exists(@"C:\Otopark\models\haarcascade_russian_plate_number.xml") ? "var" : "yok")}");
            sb.AppendLine($"Tesseract data: {(File.Exists(@"C:\Otopark\tessdata\eng.traineddata") ? "var" : "yok")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("HATA: " + ex.Message);
            sb.AppendLine(ex.StackTrace);
        }

        ShowTestResult(sb.ToString(), noUi, imagePath);
    }

    private static void ShowTestResult(string text, bool noUi, string imagePath)
    {
        try
        {
            Directory.CreateDirectory(@"C:\Otopark");
            // Her test ayri dosyaya yazilsin (ust uste yazma)
            var fname = "test_" + System.IO.Path.GetFileNameWithoutExtension(imagePath) + ".log";
            // Geçersiz karakterleri temizle
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) fname = fname.Replace(c, '_');
            File.WriteAllText(@"C:\Otopark\" + fname, text);
            File.WriteAllText(@"C:\Otopark\test_results.log", text);
        }
        catch { }

        if (!noUi)
            MessageBox.Show(text, "Plaka Tanima Test Sonucu",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// OpenCvSharp native DLL'i (OpenCvSharpExtern.dll) yuklenebiliyor mu test eder.
    /// 1x1 piksellik bir Mat olusturup serbest birakir; native call patlarsa false doner.
    /// </summary>
    private static bool OpenCvNativeSelfTest(out string error)
    {
        try
        {
            using var m = new OpenCvSharp.Mat(1, 1, OpenCvSharp.MatType.CV_8UC1);
            error = "";
            return !m.Empty();
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            try
            {
                Directory.CreateDirectory(@"C:\Otopark");
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | OpenCv self-test FAILED: {error}{Environment.NewLine}");
            }
            catch { }
            return false;
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(@"C:\Otopark");
            var msg = ex == null
                ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | CRASH [{source}]: (null exception){Environment.NewLine}"
                : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | CRASH [{source}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";
            File.AppendAllText(@"C:\Otopark\log.txt", msg);
        }
        catch { }
    }
}
