namespace TelPhone.Core;

public enum WindowTheme { Light, Dark, Auspex }

public sealed record AppSettings(
    string? AdbPath = null,
    WindowTheme Theme = WindowTheme.Light,
    bool IsTopmost = false,
    bool IsCompact = false);
