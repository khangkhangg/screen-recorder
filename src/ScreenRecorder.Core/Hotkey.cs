namespace ScreenRecorder.Core;

public record Hotkey(bool Ctrl, bool Shift, bool Alt, bool Win, uint VirtualKey)
{
    public static bool TryParse(string text, out Hotkey h)
    {
        h = default!;
        bool ctrl = false, shift = false, alt = false, win = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                default: return false;
            }
        }
        if (!ctrl && !shift && !alt && !win) return false;
        var k = parts[^1].ToUpperInvariant();
        uint vk;
        if (k.Length == 1 && (k[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) vk = k[0];
        else if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 12) vk = (uint)(0x6F + n);
        else return false;
        h = new(ctrl, shift, alt, win, vk);
        return true;
    }

    public override string ToString()
    {
        var key = VirtualKey is >= 0x70 and <= 0x7B ? $"F{VirtualKey - 0x6F}" : ((char)VirtualKey).ToString();
        return string.Concat(Ctrl ? "Ctrl+" : "", Shift ? "Shift+" : "", Alt ? "Alt+" : "", Win ? "Win+" : "", key);
    }
}
