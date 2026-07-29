using Otopark.Core;
using System.ComponentModel;
using System.Windows;

namespace Otopark.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentView))
        {
            // TAM EKRAN: uygulama her ekranda (login dahil) ekrani tamamen kaplar.
            // Eskiden login ekraninda WindowState.Normal'a dusuluyordu; kaldirildi.
            WindowState = WindowState.Maximized;
        }
    }
}
