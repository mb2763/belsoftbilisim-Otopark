using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Otopark.Api;
using Otopark.Api.Services;
using Otopark.Client.Views;
using Otopark.Core;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Otopark.Client;

public partial class App : Application
{
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
