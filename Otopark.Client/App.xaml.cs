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
        // Yakalanamamis exception'lari logla (uygulama crash olmasin, log dosyasina yazsin)
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            LogCrash("AppDomain", ex.ExceptionObject as Exception);
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
