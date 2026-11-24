using System.IO;
using System.Windows;
using TelPhone.ViewModels;
using TelPhone.Views;

namespace TelPhone;

public partial class App : Application
{
    private MainViewModel? viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataDirectory = ResolveDataDirectory();
            viewModel = new MainViewModel(dataDirectory);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Не удалось открыть папку данных TelPhone. Проверьте доступ на запись.",
                "TelPhone", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string ResolveDataDirectory()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TelPhone");
        if (CanWriteTo(portable))
            return portable;

        if (File.Exists(Path.Combine(portable, "telphone.db")))
            throw new IOException("Портативная база недоступна для записи.");

        Directory.CreateDirectory(local);
        return local;
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var probe = new FileStream(Path.Combine(directory, Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        viewModel?.Dispose();
        base.OnExit(e);
    }
}
