using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SailwindRadio.Input
{
    internal static class ShortcutParser
    {
        private static readonly Dictionary<string, KeyCode> Names = Enum.GetNames(typeof(KeyCode))
            .ToDictionary(name => name, name => (KeyCode)Enum.Parse(typeof(KeyCode), name), StringComparer.OrdinalIgnoreCase);

        internal static bool TryParse(string raw, out KeyboardShortcut shortcut, out string error)
        {
            shortcut = KeyboardShortcut.Empty;
            error = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var keys = new List<KeyCode>();
            foreach (string segment in raw.Split(new[] { '+', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;
                // Resolve spaced names before falling back to the old space-separated list.
                if (TryKey(segment, out KeyCode key)) keys.Add(key);
                else
                {
                    foreach (string token in segment.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!TryKey(token, out key))
                        {
                            error = "Unknown key name '" + token + "'. Use names such as O, LeftShift or Mouse4. Separate keys with +.";
                            return false;
                        }
                        keys.Add(key);
                    }
                }
            }
            if (keys.Count == 0)
            {
                error = "No key name was provided. Leave the value blank to disable this shortcut.";
                return false;
            }
            if (keys.Contains(KeyCode.None))
            {
                if (keys.Count == 1) return true;
                error = "None must be used alone to disable a shortcut.";
                return false;
            }
            keys = keys.Distinct().ToList();
            var mainKeys = keys.Where(key => !IsModifier(key)).ToArray();
            // A conventional modifier chord fires on its nonmodifier key in either order.
            // Multiple nonmodifier keys and modifier-only chords keep the legacy first key.
            KeyCode main = mainKeys.Length == 1 ? mainKeys[0] : keys[0];
            shortcut = new KeyboardShortcut(main, keys.Where(key => key != main).ToArray());
            return true;
        }

        private static bool TryKey(string text, out KeyCode key) =>
            Names.TryGetValue(string.Concat(text.Where(character => !char.IsWhiteSpace(character))), out key);

        private static bool IsModifier(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift: case KeyCode.RightShift:
                case KeyCode.LeftControl: case KeyCode.RightControl:
                case KeyCode.LeftAlt: case KeyCode.RightAlt:
                case KeyCode.LeftCommand: case KeyCode.RightCommand:
                case KeyCode.LeftWindows: case KeyCode.RightWindows:
                case KeyCode.AltGr:
                    return true;
                default:
                    return false;
            }
        }
    }
}
