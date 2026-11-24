using TelPhone.Core;

namespace TelPhone.Tests;

public sealed class DialerTests
{
    [Theory]
    [InlineData(" +7 (999) 123-45-67 ", "+79991234567")]
    [InlineData("090", "090")]
    [InlineData("*100#", null)]
    [InlineData("12;whoami", null)]
    [InlineData("1234567890123456", null)]
    [InlineData("123+45", null)]
    [InlineData("＋123", null)]
    [InlineData("+", null)]
    public void NormalizesOnlyDialableNumbers(string input, string? expected)
        => Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Fact]
    public void ParsesSamsungOnWindowsWithoutUsbFieldAndSkipsRemoteDevices()
    {
        var found = AdbService.ParseDevices("""
            List of devices attached
            SAMSUNG1 device product:r12s model:Galaxy_S24_FE transport_id:1
            SAMSUNG2 unauthorized transport_id:2
            SAMSUNG3 offline transport_id:3
            emulator-5554 device transport_id:4
            192.168.1.42:5555 device transport_id:5
            adb-abc._adb-tls-connect._tcp device transport_id:6
            phone.local:5555 device transport_id:7
            """);

        Assert.Collection(found,
            device => { Assert.Equal("Galaxy S24 FE", device.Model); Assert.True(device.IsReady); },
            device => Assert.Equal("unauthorized", device.State),
            device => Assert.Equal("offline", device.State));
    }

    [Fact]
    public async Task UsesSeparateAdbArgumentsAndReportsCommandAcceptance()
    {
        var runner = new StubAdb(new AdbResult(0, "Starting: Intent { act=android.intent.action.CALL }"));
        var result = await new AdbService(runner).CallAsync("SAMSUNG", "+7 (999) 123-45-67", default);
        Assert.Equal(CommandStatus.Sent, result.Status);
        Assert.Equal(
            new[] { "-s", "SAMSUNG", "shell", "am", "start", "-a", "android.intent.action.CALL", "-d", "tel:+79991234567" },
            Assert.Single(runner.Arguments));
    }

    [Fact]
    public async Task OpensDialerWhenCallPermissionIsDenied()
    {
        var runner = new StubAdb(
            new AdbResult(0, "Error: Permission Denial"),
            new AdbResult(0, "Starting: Intent\nWarning: Activity not started, its current task has been brought to the front"));
        var result = await new AdbService(runner).CallAsync("SAMSUNG", "89991234567", default);
        Assert.Equal(CommandStatus.DialOpened, result.Status);
        Assert.Equal("android.intent.action.DIAL", runner.Arguments[1][6]);
    }

    [Fact]
    public async Task DoesNotRepeatCallAfterTimeout()
    {
        var runner = new StubAdb(new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(() => new AdbService(runner).CallAsync("SAMSUNG", "123", default));
        Assert.Single(runner.Arguments);
    }

    [Fact]
    public async Task EndCallUsesSelectedDeviceAndReportsAdbFailure()
    {
        var runner = new StubAdb(new AdbResult(1, "adb.exe: device offline"));
        Assert.False(await new AdbService(runner).HangUpAsync("SAMSUNG", default));
        Assert.Equal(new[] { "-s", "SAMSUNG", "shell", "input", "keyevent", "KEYCODE_ENDCALL" }, Assert.Single(runner.Arguments));
    }

    [Fact]
    public async Task MissingAdbExecutableProducesAnActionableError()
    {
        var runner = new AdbProcess(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "adb.exe"));
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(["devices", "-l"], default));
        Assert.Contains("adb.exe", error.Message);
    }

    private sealed class StubAdb(params object[] responses) : IAdbRunner
    {
        private readonly Queue<object> responses = new(responses);
        public List<IReadOnlyList<string>> Arguments { get; } = [];

        public Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Arguments.Add(arguments);
            return responses.Dequeue() switch
            {
                AdbResult result => Task.FromResult(result),
                Exception error => Task.FromException<AdbResult>(error),
                _ => throw new InvalidOperationException()
            };
        }
    }
}
