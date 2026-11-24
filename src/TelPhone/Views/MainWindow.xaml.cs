using System.Windows;
using TelPhone.ViewModels;

namespace TelPhone.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        NumberInput.Focus();
        await ((MainViewModel)DataContext).StartAsync();
    }

    private void ShowPhoneTab(object sender, RoutedEventArgs e)
    {
        // Let the button command read the selection before unloading the current tab.
        Dispatcher.InvokeAsync(() =>
        {
            Tabs.SelectedItem = PhoneTab;
            NumberInput.Focus();
        });
    }
}
