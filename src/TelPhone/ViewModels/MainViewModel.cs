using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using TelPhone.Core;

namespace TelPhone.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DialerDatabase database;
    private readonly string settingsPath;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken cancellation;
    private readonly RelayCommand call;
    private readonly RelayCommand hangUp;
    private bool ready;
    private bool busy;
    private bool discovering;
    private bool adbInitialized;
    private AdbService? adb;
    private IReadOnlyList<AndroidDevice> devices = [];
    private AndroidDevice? selectedDevice;
    private string number = "";
    private string connection = "Проверяем подключение…";
    private string feedback = "";
    private IReadOnlyList<Contact> contacts = [];
    private IReadOnlyList<HistoryEntry> history = [];

    public MainViewModel(string dataDirectory)
    {
        database = new DialerDatabase(Path.Combine(dataDirectory, "telphone.db"));
        settingsPath = Path.Combine(dataDirectory, "settings.json");
        cancellation = lifetime.Token;
        call = new(async _ => await RunAsync(CallAsync), _ => !busy && ready && SelectedDevice?.IsReady == true && PhoneNumber.Normalize(Number) != null);
        hangUp = new(async _ => await RunAsync(HangUpAsync), _ => !busy && SelectedDevice?.IsReady == true);
        Import = new RelayCommand(async _ => await RunAsync(ImportAsync), _ => !busy && ready);
        ChooseAdb = new RelayCommand(async _ => await RunAsync(ChooseAdbAsync), _ => adbInitialized && !busy && !discovering);
        Append = new RelayCommand(value => Number += value as string);
        Clear = new RelayCommand(_ => Number = "");
        Backspace = new RelayCommand(_ => Number = Number.Length == 0 ? "" : Number[..^1]);
        UseContact = new RelayCommand(value => { if (value is Contact contact) Number = contact.Phone; }, value => value is Contact);
        UseHistory = new RelayCommand(value => { if (value is HistoryEntry entry) Number = entry.Phone; }, value => value is HistoryEntry);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand Call => call;
    public ICommand HangUp => hangUp;
    public ICommand Import { get; }
    public ICommand ChooseAdb { get; }
    public ICommand Append { get; }
    public ICommand Clear { get; }
    public ICommand Backspace { get; }
    public ICommand UseContact { get; }
    public ICommand UseHistory { get; }

    public string Number { get => number; set => Set(ref number, value ?? ""); }
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string Feedback { get => feedback; private set => Set(ref feedback, value); }
    public IReadOnlyList<AndroidDevice> Devices { get => devices; private set => Set(ref devices, value); }
    public AndroidDevice? SelectedDevice { get => selectedDevice; set => Set(ref selectedDevice, value); }
    public IReadOnlyList<Contact> Contacts { get => contacts; private set => Set(ref contacts, value); }
    public IReadOnlyList<HistoryEntry> History { get => history; private set => Set(ref history, value); }

    public async Task StartAsync()
    {
        await RunAsync(async () =>
        {
            await Task.Run(database.Initialize, cancellation);
            await RefreshDataAsync();
            ready = true;
        });

        var bundledPath = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        string? configuredPath = null;
        try
        {
            if (File.Exists(settingsPath))
                configuredPath = JsonSerializer.Deserialize<AdbSettings>(await File.ReadAllTextAsync(settingsPath, cancellation))?.AdbPath;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Feedback = "Не удалось прочитать настройки ADB. Выберите adb.exe вручную.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        if (File.Exists(configuredPath))
            adb = new AdbService(new AdbProcess(configuredPath));
        else if (File.Exists(bundledPath))
            adb = new AdbService(new AdbProcess(bundledPath));

        adbInitialized = true;
        CommandManager.InvalidateRequerySuggested();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                if (!busy)
                    await DiscoverAsync();
            } while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task DiscoverAsync()
    {
        if (discovering)
            return;
        if (adb is null)
        {
            Connection = "ADB отсутствует. Укажите путь к adb.exe.";
            return;
        }

        discovering = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var found = await adb.GetDevicesAsync(cancellation);
            if (!Devices.SequenceEqual(found))
            {
                string? previous = SelectedDevice?.Serial;
                Devices = found;
                SelectedDevice = found.FirstOrDefault(device => device.Serial == previous)
                    ?? (found.Count == 1 ? found[0] : null);
            }

            Connection = found.Count switch
            {
                0 => "Подключите Android по USB и включите отладку.",
                > 1 => "Подключено несколько телефонов. Выберите устройство.",
                _ when found[0].IsReady => "Телефон подключён по USB",
                _ when found[0].State == "unauthorized" => "Разрешите отладку по USB на экране телефона.",
                _ => "Телефон недоступен. Переподключите кабель."
            };
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            Devices = [];
            SelectedDevice = null;
            Connection = exception.Message;
        }
        finally
        {
            discovering = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ChooseAdbAsync()
    {
        var picker = new OpenFileDialog { Title = "Выберите adb.exe", Filter = "Android Debug Bridge (adb.exe)|adb.exe" };
        if (picker.ShowDialog() != true)
            return;

        adb = new AdbService(new AdbProcess(picker.FileName));
        Devices = [];
        SelectedDevice = null;
        await DiscoverAsync();
        var temporaryFile = settingsPath + ".tmp";
        await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(new AdbSettings(picker.FileName)), cancellation);
        File.Move(temporaryFile, settingsPath, overwrite: true);
    }

    private async Task CallAsync()
    {
        if (adb is null || SelectedDevice is not { IsReady: true } device ||
            PhoneNumber.Normalize(Number) is not { } normalized)
            return;

        long id = await Task.Run(() => database.RecordAttempt(normalized, device.Serial), cancellation);
        CallResult result;
        try
        {
            result = await adb.CallAsync(device.Serial, normalized, cancellation);
        }
        catch (TimeoutException)
        {
            result = new(CommandStatus.Uncertain, "ADB не ответил вовремя. Проверьте, начался ли вызов на телефоне.");
        }
        catch (IOException exception)
        {
            result = new(CommandStatus.Rejected, exception.Message);
        }

        try
        {
            await Task.Run(() => database.FinishAttempt(id, result.Status));
            History = await Task.Run(database.GetHistory, cancellation);
            Feedback = result.Message;
        }
        catch (SqliteException)
        {
            Feedback = result.Message + " Не удалось обновить запись в истории.";
        }
    }

    private async Task HangUpAsync()
    {
        if (adb is not null && SelectedDevice is { IsReady: true } device)
            Feedback = await adb.HangUpAsync(device.Serial, cancellation)
                ? "Команда завершения отправлена."
                : "ADB сообщил об ошибке завершения. При необходимости завершите вызов на телефоне.";
    }

    private async Task ImportAsync()
    {
        var picker = new OpenFileDialog { Title = "Импорт контактов", Filter = "Контакты CSV и JSON|*.csv;*.json" };
        if (picker.ShowDialog() != true)
            return;

        var result = await Task.Run(() => database.Import(ContactImport.Read(picker.FileName)), cancellation);
        Contacts = await Task.Run(database.GetContacts, cancellation);
        Feedback = $"Добавлено {result.Added} · Дубликаты {result.Duplicates} · Некорректные строки {result.Invalid}";
    }

    private async Task RefreshDataAsync()
    {
        Contacts = await Task.Run(database.GetContacts, cancellation);
        History = await Task.Run(database.GetHistory, cancellation);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (busy || cancellation.IsCancellationRequested)
            return;
        busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is FormatException or JsonException or MalformedLineException or System.Text.DecoderFallbackException)
        {
            Feedback = exception is FormatException ? exception.Message : "Не удалось прочитать файл контактов. Проверьте формат и кодировку UTF-8.";
        }
        catch (SqliteException)
        {
            Feedback = "Не удалось сохранить или прочитать данные. Проверьте доступ к папке данных.";
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Feedback = exception is UnauthorizedAccessException ? "Нет доступа к файлу или папке." : exception.Message;
        }
        finally
        {
            busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Set<T>(ref T current, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        current = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private sealed record AdbSettings(string AdbPath);
}
