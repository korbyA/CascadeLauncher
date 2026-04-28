using System.Windows;
using System.Windows.Input;
using CascadeLauncher.ViewModels;

namespace CascadeLauncher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.LaunchAsync();
    }
}
