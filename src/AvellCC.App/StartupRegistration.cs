using Microsoft.Win32;

namespace AvellCC.App;

/// <summary>Start with Windows for the current user (HKCU\...\Run), no admin needed. The service starts on its own.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AvellCC";
    private const string AppKey = @"Software\AvellCC";
    private const string InitializedValue = "StartupInitialized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// First run of an installed copy (under Program Files): turn "start with Windows" on, once per user.
    /// Afterwards the user's choice in Settings wins, also across updates. Development builds are left alone.
    /// </summary>
    public static void InitializeOnFirstRun()
    {
        var exe = Environment.ProcessPath ?? "";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!exe.StartsWith(programFiles + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

        using var app = Registry.CurrentUser.CreateSubKey(AppKey);
        if (app.GetValue(InitializedValue) is not null) return;
        Set(true);
        app.SetValue(InitializedValue, 1, RegistryValueKind.DWord);
    }
}
