using System.Windows;
using System.Windows.Input;

namespace Otopark.Client.Views
{
    /// <summary>
    /// Programi kapatmak icin yonetici sifresi sorar.
    /// Uygulama TAM EKRAN (WindowStyle=None) calistigi icin kapatma butonu yoktur;
    /// kurum logosuna CIFT TIKLANARAK bu pencere acilir.
    /// </summary>
    public partial class ExitPasswordWindow : Window
    {
        /// <summary>Yonetici cikis sifresi (kiosk "Pavo Ayarlari" PIN'i ile ayni).</summary>
        public const string ExitPassword = "632738";

        public ExitPasswordWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => TxtPassword.Focus();
        }

        private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Dogrula();
            else if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Dogrula();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Dogrula()
        {
            if (TxtPassword.Password == ExitPassword)
            {
                DialogResult = true;
                Close();
                return;
            }

            TxtError.Text = "Şifre hatalı!";
            TxtError.Visibility = Visibility.Visible;
            TxtPassword.Clear();
            TxtPassword.Focus();
        }
    }
}
