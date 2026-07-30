using Otopark.Api.Services;
using Otopark.Core.Session;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Otopark.Wash
{
    public partial class WashWindow : Window
    {
        private readonly VehicleParkApiService _api;
        public ObservableCollection<WashRow> Rows { get; } = new();
        private WashRow? _selected;

        /// <summary>Geri sayimi her saniye tazeleyen zamanlayici.</summary>
        private System.Windows.Threading.DispatcherTimer? _tick;
        /// <summary>Son sunucu yenilemesi (ucret guncel kalsin diye periyodik yenilenir).</summary>
        private DateTime _lastServerRefresh = DateTime.MinValue;

        public WashWindow(VehicleParkApiService api)
        {
            InitializeComponent();
            _api = api;
            EntryList.ItemsSource = Rows;
            Loaded += async (_, __) =>
            {
                await LoadAsync();
                StartCountdownTimer();
            };
            Closed += (_, __) => _tick?.Stop();
        }

        /// <summary>
        /// Saniyede bir tum satirlarin geri sayimini tazeler. Ayrica sunucudan gelen
        /// ucret bilgisi guncel kalsin diye 60 sn'de bir liste yeniden yuklenir.
        /// </summary>
        private void StartCountdownTimer()
        {
            _tick = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _tick.Tick += async (_, __) =>
            {
                foreach (var r in Rows) r.RefreshCountdown();

                // Sure dolan arac varsa ucret sunucudan gelir; dakikada bir tazele.
                if ((DateTime.Now - _lastServerRefresh).TotalSeconds >= 60)
                    await LoadAsync(keepSelection: true);
            };
            _tick.Start();
        }

        private async System.Threading.Tasks.Task LoadAsync(bool keepSelection = false)
        {
            try
            {
                long onceSecili = keepSelection ? (_selected?.EntryId ?? 0) : 0;

                Rows.Clear();
                if (!keepSelection)
                {
                    _selected = null;
                    SelectedPlateText.Text = "—";
                    SelectedInfoText.Text = "Sağdaki listeden bir araç seçiniz.";
                    PrintBtn.IsEnabled = false;
                    ResultBox.Visibility = Visibility.Collapsed;
                }

                var list = await _api.GetWashRecentEntriesAsync(UserSession.CompanyId, 15);
                _lastServerRefresh = DateTime.Now;

                foreach (var e in list)
                {
                    Rows.Add(new WashRow
                    {
                        EntryId = e.EntryId,
                        Plate = e.Plate ?? "",
                        MinutesIn = e.MinutesIn,
                        EntryTime = e.EntryTime,
                        AlreadyWashed = e.AlreadyWashed,
                        FreeMinutes = e.FreeMinutes > 0 ? e.FreeMinutes : 120,
                        Fee = e.Fee
                    });
                }

                // Otomatik yenilemede secili arac korunur (kullanicinin secimi kaybolmasin).
                if (onceSecili > 0)
                {
                    var yeni = Rows.FirstOrDefault(r => r.EntryId == onceSecili);
                    if (yeni != null) EntryList.SelectedItem = yeni;
                }

                if (Rows.Count == 0 && !keepSelection)
                    SelectedInfoText.Text = "Otoparkta araç bulunmuyor.";
            }
            catch (Exception ex)
            {
                if (!keepSelection)
                    MessageBox.Show("Liste alınamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = EntryList.SelectedItem as WashRow;
            ResultBox.Visibility = Visibility.Collapsed;

            if (_selected == null)
            {
                SelectedPlateText.Text = "—";
                SelectedInfoText.Text = "Sağdaki listeden bir araç seçiniz.";
                PrintBtn.IsEnabled = false;
                return;
            }

            SelectedPlateText.Text = _selected.Plate;
            SelectedInfoText.Text = $"{_selected.MinutesIn} dakikadır otoparkta. " +
                (_selected.AlreadyWashed
                    ? "⚠ Bu araca zaten yıkama fişi basılmış (araç çıkmadan tekrar basılamaz)."
                    : "Fiş basmak için aşağıdaki butona basınız.");
            PrintBtn.IsEnabled = !_selected.AlreadyWashed;
        }

        private async void PrintBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (_selected.AlreadyWashed)
            {
                MessageBox.Show("Bu araca zaten yıkama fişi basılmış. Araç çıkmadan tekrar basılamaz.",
                    "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PrintBtn.IsEnabled = false;
            try
            {
                // Serbest sure SUNUCUDA tanimli (WASH_SETTING); buradan gonderilen deger yok sayilir (0 gec).
                var res = await _api.PrintWashReceiptAsync(
                    UserSession.CompanyId, UserSession.UserId, _selected.EntryId, 0);

                if (!res.Success)
                {
                    MessageBox.Show(res.Message ?? "Fiş basılamadı.", "Hata",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    PrintBtn.IsEnabled = true;
                    return;
                }

                ResultBox.Visibility = Visibility.Visible;
                if (res.IsFree)
                {
                    ResultTitle.Text = "✓ Yıkama Fişi Basıldı — ÜCRETSİZ";
                    ResultBox.Background = (Brush)new BrushConverter().ConvertFrom("#F0FFF4")!;
                    ResultTitle.Foreground = (Brush)new BrushConverter().ConvertFrom("#059669")!;
                }
                else
                {
                    ResultTitle.Text = $"✓ Yıkama Fişi Basıldı — {res.ChargedAmount:0.##} TL";
                    ResultBox.Background = (Brush)new BrushConverter().ConvertFrom("#FFF7ED")!;
                    ResultTitle.Foreground = (Brush)new BrushConverter().ConvertFrom("#C2410C")!;
                }
                ResultDetail.Text =
                    $"Plaka: {res.Plate}\n" +
                    $"Giriş: {res.EntryTime:dd.MM.yyyy HH:mm}\n" +
                    $"İçeride: {res.MinutesIn} dk\n" +
                    $"Fiş Saati: {res.ReceiptTime:dd.MM.yyyy HH:mm}\n" +
                    $"Tutar: {res.ChargedAmount:0.##} TL";

                PrintReceiptDocument(res);

                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fiş basma hatası: " + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PrintBtn.IsEnabled = true;
            }
        }

        /// <summary>Basit yikama fisi yazdirma (varsayilan yazici).</summary>
        private static void PrintReceiptDocument(WashReceiptResult res)
        {
            try
            {
                var doc = new System.Windows.Documents.FlowDocument
                {
                    PagePadding = new Thickness(20),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 14,
                    ColumnWidth = 300
                };
                void Line(string t, bool bold = false, double size = 14)
                {
                    var p = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(t))
                    { Margin = new Thickness(0), FontSize = size };
                    if (bold) p.FontWeight = FontWeights.Bold;
                    doc.Blocks.Add(p);
                }
                Line("YIKAMA FİŞİ", true, 18);
                Line("Kapalı Otopark");
                Line("--------------------------------");
                Line($"Plaka      : {res.Plate}", true);
                Line($"Giriş      : {res.EntryTime:dd.MM.yyyy HH:mm}");
                Line($"İçeride    : {res.MinutesIn} dk");
                Line($"Fiş Saati  : {res.ReceiptTime:dd.MM.yyyy HH:mm}");
                Line("--------------------------------");
                Line($"TUTAR      : {res.ChargedAmount:0.##} TL", true, 16);
                Line("");
                Line(res.IsFree ? "(Ücretsiz süre içinde)" : "(Ücretsiz süre aşıldı)");

                var pd = new System.Windows.Controls.PrintDialog();
                System.Windows.Documents.IDocumentPaginatorSource src = doc;
                pd.PrintDocument(src.DocumentPaginator, "Yikama Fisi - " + res.Plate);
            }
            catch
            {
                // Yazici yoksa sessiz gec — fis zaten DB'ye kaydedildi.
            }
        }
    }

    public sealed class WashRow : System.ComponentModel.INotifyPropertyChanged
    {
        public long EntryId { get; set; }
        public string Plate { get; set; } = "";
        public int MinutesIn { get; set; }
        public DateTime EntryTime { get; set; }
        public bool AlreadyWashed { get; set; }

        /// <summary>Tanimli ucretsiz sure (dk) — WASH_SETTING'ten gelir.</summary>
        public int FreeMinutes { get; set; }
        /// <summary>Sunucunun bildirdigi ucret (sure dolduysa dolu).</summary>
        public decimal Fee { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Ucretsiz surenin bitimine kalan sure. Giris zamani + serbest sureden ANLIK hesaplanir,
        /// bu yuzden ekranda gercek zamanli olarak geriye sayar (159, 158, ...).
        /// </summary>
        public TimeSpan Remaining
        {
            get
            {
                var bitis = EntryTime.AddMinutes(FreeMinutes);
                var kalan = bitis - DateTime.Now;
                return kalan > TimeSpan.Zero ? kalan : TimeSpan.Zero;
            }
        }

        public bool IsExpired => Remaining <= TimeSpan.Zero;

        /// <summary>Listede gosterilen sure/ucret metni.</summary>
        public string CountdownText => IsExpired
            ? (Fee > 0 ? $"Süre doldu — Ücret: {Fee:0.##} ₺" : "Süre doldu — ücretli")
            : $"Kalan: {(int)Remaining.TotalMinutes} dk {Remaining.Seconds:D2} sn";

        public Brush CountdownBrush => IsExpired
            ? (Brush)new BrushConverter().ConvertFrom("#DC2626")!   // kirmizi: sure doldu
            : (Brush)new BrushConverter().ConvertFrom("#059669")!;  // yesil: devam ediyor

        /// <summary>Zamanlayici her saniye cagirir: geri sayim alanlarini tazeler.</summary>
        public void RefreshCountdown()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Remaining)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpired)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountdownText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountdownBrush)));
        }

        // YIKANACAK: fis basilmis, arac cikista yikama ile karsilanacak (WASH_RECEIPT'e kalici
        // kaydedildi - raporlarda kullanilir). Geri sayim bu isaretlemeden sonra da DEVAM eder.
        public string WashedText => AlreadyWashed ? "YIKANACAK" : "BEKLİYOR";
        public Brush WashedBrush => AlreadyWashed
            ? (Brush)new BrushConverter().ConvertFrom("#2563EB")!   // mavi: yikanacak (dikkat cekici)
            : (Brush)new BrushConverter().ConvertFrom("#5ACF90")!;
    }
}
