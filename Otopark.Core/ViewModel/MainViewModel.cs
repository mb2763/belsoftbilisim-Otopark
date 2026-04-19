using CommunityToolkit.Mvvm.ComponentModel;

namespace Otopark.Core;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object currentView;

    public MainViewModel()
    {
        // Başlangıç boş
        CurrentView = null!;
    }

    public void Navigate(object vm)
    {
        CurrentView = vm;
    }
}
