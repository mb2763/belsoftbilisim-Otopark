using Otopark.Api.Services;
using Otopark.Core.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Otopark.Client.Views;

public partial class AddVehicleWindow : Window
{
    private readonly LookupApiService _lookup;
    private readonly string _plate;

    private List<CustomerCompanyDto> _companies = new();
    private List<VehicleTypeDto> _vehicleTypes = new();
    private List<TariffDto> _tariffs = new();

    public AddVehicleResultDto? CreatedVehicle { get; private set; }

    public AddVehicleWindow(LookupApiService lookup, string plate)
    {
        InitializeComponent();
        _lookup = lookup;
        _plate = plate;

        TxtPlate.Text = plate;

        Loaded += async (_, __) => await LoadLookupsAsync();
    }

    private async Task LoadLookupsAsync()
    {
        try
        {
            _companies = await _lookup.GetCustomerCompaniesAsync(UserSession.CompanyId);
            _vehicleTypes = await _lookup.GetVehicleTypesAsync(UserSession.CompanyId);

            var allTariffs = await _lookup.GetTariffsAsync(UserSession.CompanyId);
            _tariffs = allTariffs.Where(t =>
                t.TariffName != null && t.TariffName.Contains("Kapalı", StringComparison.OrdinalIgnoreCase)).ToList();
            if (_tariffs.Count == 0)
                _tariffs = allTariffs; // fallback

            _companies.Insert(0, new CustomerCompanyDto { Id = 0, Title = "" });
            CmbCompany.ItemsSource = _companies;
            CmbCompany.SelectedIndex = 0;
            CmbVehicleType.ItemsSource = _vehicleTypes;
            CmbTariff.ItemsSource = _tariffs.Where(x=>x.Id==422).ToList();

            if (_vehicleTypes.Count > 0)
                CmbVehicleType.SelectedIndex = 9;
            if (_tariffs.Count > 0)
                CmbTariff.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            TxtError.Text = "Veriler yuklenemedi: " + ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Text = "";

        if (CmbVehicleType.SelectedItem is not VehicleTypeDto vehicleType)
        {
            TxtError.Text = "Lutfen arac turunu seciniz.";
            return;
        }

        if (CmbTariff.SelectedItem is not TariffDto tariff)
        {
            TxtError.Text = "Lutfen tarife seciniz.";
            return;
        }

        var company = CmbCompany.SelectedItem as CustomerCompanyDto;
        if (company != null && company.Id == 0) company = null;

        try
        {
            var req = new AddVehicleRequest
            {
                CurrentUserId = UserSession.UserId,
                Plate = _plate,
                CompanyId = UserSession.CompanyId,
                CustomerCompanyId = company?.Id,
                VehicleTypeId = vehicleType.Id,
                TariffId = tariff.Id
            };

            var result = await _lookup.AddVehicleAsync(req);

            if (result?.Errors != null && result.Errors.Count > 0)
            {
                var msg = string.Join(", ", result.Errors
                    .Where(err => !string.IsNullOrEmpty(err.Message))
                    .Select(err => err.Message));
                TxtError.Text = string.IsNullOrWhiteSpace(msg) ? "Kayit basarisiz." : msg;
                return;
            }

            CreatedVehicle = result?.Result;
            MessageBox.Show($"{_plate} arac kaydi basariyla olusturuldu.\nGiris kaydina yonlendiriliyorsunuz.",
                "Basarili", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            TxtError.Text = "Hata: " + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
