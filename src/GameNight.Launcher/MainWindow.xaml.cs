using System.Windows;
using GameNight.Launcher.ViewModels;

namespace GameNight.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
