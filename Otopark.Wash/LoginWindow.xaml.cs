using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Otopark.Api.Services;
using Otopark.Core.Session;

namespace Otopark.Wash;

public partial class LoginWindow : Window
{
    private const long COMPANY_ID = 2;              // Otopark.Client ile ayni (kapali otopark firmasi)
    private const long CLOSED_PARK_ZONE_CLASS_ID = 424;

    private readonly AuthApiService _auth;
    private readonly ZoneApiService _zone;

    public LoginWindow(AuthApiService auth, ZoneApiService zone)
    {
        InitializeComponent();
        _auth = auth;
        _zone = zone;
        Loaded += async (_, __) => await LoadZonesAsync();
    }

    private async System.Threading.Tasks.Task LoadZonesAsync()
    {
        try
        {
            var zones = await _zone.GetZonesAsync(COMPANY_ID, CLOSED_PARK_ZONE_CLASS_ID);
            ZoneCombo.ItemsSource = zones;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Bölge listesi yüklenemedi: " + ex.Message;
        }
    }

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginBtn_Click(sender, e);
    }

    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        string u = (UserBox.Text ?? "").Trim();
        string c = (CompanyBox.Text ?? "").Trim();
        string p = PwdBox.Password ?? "";

        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c) || string.IsNullOrWhiteSpace(p))
        {
            ErrorText.Text = "Lütfen tüm alanları doldurun.";
            return;
        }

        long zoneId = (ZoneCombo.SelectedItem is ZoneDto z) ? z.Id : 0;

        LoginBtn.IsEnabled = false;
        try
        {
            var res = await _auth.LoginAsync(new LoginRequest
            {
                UserNameEmail = u,
                CompanyCode = c,
                Password = p,
                ZoneId = zoneId,
                LoginType = 0
            });

            if (res?.Errors != null && res.Errors.Count > 0)
            {
                var msg = string.Join(", ", res.Errors
                    .Where(x => !string.IsNullOrEmpty(x.Message))
                    .Select(x => x.Message));
                ErrorText.Text = string.IsNullOrWhiteSpace(msg) ? "Giriş başarısız. Bilgileri kontrol edin." : msg;
                LoginBtn.IsEnabled = true;
                return;
            }

            if (res?.Result == null)
            {
                ErrorText.Text = "Giriş başarısız. Bilgileri kontrol edin.";
                LoginBtn.IsEnabled = true;
                return;
            }

            UserSession.UserId = res.Result.Id;
            UserSession.CompanyId = COMPANY_ID;
            UserSession.UserName = res.Result.UserName;
            UserSession.IsAdmin = string.Equals(res.Result.UserType, "Yönetici", StringComparison.OrdinalIgnoreCase);

            DialogResult = true;   // App.OnStartup WashWindow'u acar
        }
        catch (Exception ex)
        {
            ErrorText.Text = "API Hatası: " + ex.Message;
            LoginBtn.IsEnabled = true;
        }
    }
}
