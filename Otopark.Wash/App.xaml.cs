using System.Windows;
using Otopark.Api;
using Otopark.Api.Services;

namespace Otopark.Wash;

/// <summary>
/// Otopark.Wash — Kapali otopark YIKAMA ekrani (ayri exe, kullanici girisli).
/// Otopark.Client'tan bagimsiz, hafif (OpenCV/ALPR yok). Akis: Login -> WashWindow.
/// Ucretsiz dakika EKRANDA YOK; sunucu (WASH_SETTING) uygular.
/// </summary>
public partial class App : Application
{
    public const string BaseUrl = "http://web.belsoft.com.tr:221/";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Login penceresi kapaninca (ShowDialog doner) uygulama OTOMATIK KAPANMASIN.
        // Aksi halde login basarili olunca WashWindow acilmadan uygulama sonlaniyor ("ekran gitti").
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var http = ApiClientFactory.Create(BaseUrl);
        var auth = new AuthApiService(http);
        var washApi = new VehicleParkApiService(http);
        var zoneApi = new ZoneApiService(http);

        var login = new LoginWindow(auth, zoneApi);
        bool? ok = login.ShowDialog();
        if (ok == true)
        {
            var wash = new WashWindow(washApi);
            MainWindow = wash;
            // Artik normal davranis: yikama (ana) penceresi kapaninca uygulama kapansin.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            wash.Show();
        }
        else
        {
            Shutdown();
        }
    }
}
