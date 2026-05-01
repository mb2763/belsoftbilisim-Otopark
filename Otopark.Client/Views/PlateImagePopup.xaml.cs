using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Otopark.Client.Views
{
    public partial class PlateImagePopup : Window, INotifyPropertyChanged
    {
        private string _plate = "";
        private string _imagePath = "";
        private string _titleText = "";

        public string Plate
        {
            get => _plate;
            set { _plate = value; OnPropertyChanged(); }
        }

        public string ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropertyChanged(); }
        }

        public string TitleText
        {
            get => _titleText;
            set { _titleText = value; OnPropertyChanged(); }
        }

        public PlateImagePopup(string plate, string imagePath, string titleText)
        {
            InitializeComponent();
            Plate = plate ?? "";
            ImagePath = imagePath ?? "";
            TitleText = titleText ?? "";
            DataContext = this;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
