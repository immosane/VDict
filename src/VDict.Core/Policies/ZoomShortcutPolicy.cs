namespace VDict.Core.Policies;

public readonly record struct ZoomShortcut(int Step, bool Reset)
{
    public static ZoomShortcut None => new(0, false);
}

public static class ZoomShortcutPolicy
{
    private const uint Key0 = 0x30;
    private const uint Numpad0 = 0x60;
    private const uint NumpadAdd = 0x6B;
    private const uint NumpadSubtract = 0x6D;
    private const uint OemPlus = 0xBB;
    private const uint OemMinus = 0xBD;

    public static ZoomShortcut Resolve(uint virtualKey, bool controlDown)
    {
        if (!controlDown)
        {
            return ZoomShortcut.None;
        }

        return virtualKey switch
        {
            Key0 or Numpad0 => new ZoomShortcut(0, true),
            NumpadAdd or OemPlus => new ZoomShortcut(1, false),
            NumpadSubtract or OemMinus => new ZoomShortcut(-1, false),
            _ => ZoomShortcut.None
        };
    }
}
