using Microsoft.Win32;

namespace Framelet;

internal static class LoginStartup
{
    internal const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Framelet";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command &&
            string.Equals(command, BuildCommand(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var command = BuildCommand();
        if (command.Length > 260)
            throw new IOException("Windows startup requires a shorter command path.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string BuildCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Framelet executable path is unavailable.");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            return $"\"{executable}\" \"{typeof(App).Assembly.Location}\" {TrayArgument}";

        return $"\"{executable}\" {TrayArgument}";
    }
}
