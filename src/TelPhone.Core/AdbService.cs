using System.ComponentModel;
using System.Diagnostics;

namespace TelPhone.Core;

public sealed record AndroidDevice(string Serial, string State, string Model)
{
    public bool IsReady => State == "device";
    public string Label => $"{Model} · {Serial}" + (State switch
    {
        "device" => "",
        "unauthorized" => " · Разрешите отладку на телефоне",
        _ => " · Устройство недоступно"
    });
}

public enum CommandStatus { Pending, Sent, DialOpened, Rejected, Uncertain }
public sealed record AdbResult(int ExitCode, string Output);
public sealed record CallResult(CommandStatus Status, string Message);

public interface IAdbRunner
{
    Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class AdbProcess(string executable) : IAdbRunner
{
    public async Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var options = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            options.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = options };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new IOException("Не удалось запустить ADB. Проверьте путь к adb.exe.", exception);
        }

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new AdbResult(process.ExitCode, output.Result + "\n" + error.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ADB не ответил вовремя. Проверьте подключение телефона.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
    }
}

public sealed class AdbService(IAdbRunner runner)
{
    public async Task<IReadOnlyList<AndroidDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(["devices", "-l"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !result.Output.Contains("List of devices attached", StringComparison.Ordinal))
            throw new IOException("Не удалось получить список устройств ADB. Проверьте подключение и путь к ADB.");

        return ParseDevices(result.Output);
    }

    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidDevice>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1] is not ("device" or "unauthorized" or "offline" or "authorizing" or "recovery" or "sideload"))
                continue;

            var serial = fields[0];
            if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) ||
                serial.EndsWith("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
                serial.Contains(':') || serial.Contains('.'))
                continue;

            // The Windows USB backend can omit the usb: field, even for physical devices.
            string model = fields.Skip(2).FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal))?[6..]
                .Replace('_', ' ') ?? "Android";
            devices.Add(new AndroidDevice(serial, fields[1], model));
        }
        return devices;
    }

    public async Task<CallResult> CallAsync(string serial, string number, CancellationToken cancellationToken)
    {
        var normalized = PhoneNumber.Normalize(number) ?? throw new ArgumentException("Введите корректный номер.", nameof(number));
        var call = await StartDialerAsync(serial, normalized, "CALL", cancellationToken).ConfigureAwait(false);
        if (IsAccepted(call))
            return new(CommandStatus.Sent, "Команда вызова принята телефоном.");

        if (call.Output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase) ||
            call.Output.Contains("SecurityException", StringComparison.OrdinalIgnoreCase) ||
            call.Output.Contains("unable to resolve Intent", StringComparison.OrdinalIgnoreCase))
        {
            var dial = await StartDialerAsync(serial, normalized, "DIAL", cancellationToken).ConfigureAwait(false);
            return IsAccepted(dial)
                ? new(CommandStatus.DialOpened, "Звонилка открыта. Проверьте номер и подтвердите вызов на телефоне.")
                : new(CommandStatus.Rejected, "Телефон отклонил вызов, открыть звонилку тоже не удалось.");
        }

        return new(CommandStatus.Rejected, "ADB сообщил об ошибке команды вызова. Проверьте телефон и подключение.");
    }

    public async Task<bool> HangUpAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ["-s", serial, "shell", "input", "keyevent", "KEYCODE_ENDCALL"], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 && !HasError(result.Output);
    }

    private Task<AdbResult> StartDialerAsync(string serial, string number, string action, CancellationToken cancellationToken)
        => runner.RunAsync(
            ["-s", serial, "shell", "am", "start", "-a", $"android.intent.action.{action}", "-d", $"tel:{number}"],
            cancellationToken);

    private static bool IsAccepted(AdbResult result)
        => result.ExitCode == 0 && result.Output.Contains("Starting: Intent", StringComparison.Ordinal) && !HasError(result.Output);

    private static bool HasError(string output)
        => output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase);
}
