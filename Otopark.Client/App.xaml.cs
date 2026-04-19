using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Otopark.Api;
using Otopark.Api.Services;
using Otopark.Client.Views;
using Otopark.Core;
using System.Windows;

namespace Otopark.Client;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
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
}
