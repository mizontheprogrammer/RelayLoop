using RelayLoop.App.Native;

namespace RelayLoop.App.Services;

public static class HotKeyParser
{
    public static HotKeyGesture Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new FormatException("Use at least one modifier and one key, for example Ctrl+Shift+R.");
        }

        var modifiers = HotKeyModifiers.NoRepeat;
        uint virtualKey = 0;
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Control;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Shift;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Alt;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Windows;
            }
            else
            {
                if (virtualKey != 0)
                {
                    throw new FormatException("A hotkey must contain exactly one non-modifier key.");
                }

                virtualKey = ParseVirtualKey(token);
            }
        }

        if (virtualKey == 0)
        {
            throw new FormatException("A letter, number, or function key is required.");
        }

        if ((modifiers & ~HotKeyModifiers.NoRepeat) == HotKeyModifiers.None)
        {
            throw new FormatException("At least one modifier key is required.");
        }

        return new HotKeyGesture(modifiers, virtualKey);
    }

    public static string Format(HotKeyGesture gesture) => gesture.ToString();

    private static uint ParseVirtualKey(string token)
    {
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }

        if (token.Length is >= 2 and <= 3 && token[0] is 'F' or 'f' &&
            int.TryParse(token.AsSpan(1), out var functionNumber) && functionNumber is >= 1 and <= 24)
        {
            return unchecked((uint)(0x70 + functionNumber - 1));
        }

        if (token.StartsWith("VK 0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(token.AsSpan(5), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var rawVirtualKey) &&
            rawVirtualKey is >= 1 and <= 0xFF)
        {
            return rawVirtualKey;
        }

        return token.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "HOME" => 0x24,
            "END" => 0x23,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => throw new FormatException($"'{token}' is not a supported hotkey key.")
        };
    }
}
