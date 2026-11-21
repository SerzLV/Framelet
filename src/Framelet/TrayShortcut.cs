using System.Windows.Input;

namespace Framelet;

internal sealed record TrayShortcut(Key Key, ModifierKeys Modifiers)
{
    internal static readonly TrayShortcut Default = new(Key.F10, ModifierKeys.Control | ModifierKeys.Alt);

    internal bool IsUsable => (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
        && (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0
        && Key is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or (>= Key.F1 and <= Key.F24) and not Key.F12;

    internal uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);
    internal uint NativeModifiers => (uint)Modifiers | 0x4000;

    internal string Display(string separator = " + ")
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((char)VirtualKey).ToString() : Key.ToString());
        return string.Join(separator, parts);
    }
}
