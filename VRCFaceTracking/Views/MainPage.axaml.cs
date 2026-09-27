using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using VRCFaceTracking.Contracts;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public partial class MainPage : UserControl, INotifyNavigated
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    
    public MainPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<MainViewModel>();
        SizeChanged += (_, args) =>
        {
            var spacious = args.NewSize.Width >= 920 && args.NewSize.Height >= 620;
            if (spacious && !Classes.Contains("spacious")) Classes.Add("spacious");
            if (!spacious && Classes.Contains("spacious")) Classes.Remove("spacious");
        };
    }

    public void OnNavigatedTo() => ViewModel.OnNavigatedTo();
}
