using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SailwindRadio.Input
{
    internal static class HotkeyInput
    {
        // Adapted from Fast Forward's matcher. Unrelated held sailing controls must not block a shortcut.
        internal static bool IsDown(KeyboardShortcut shortcut, bool focused,
            Func<KeyCode, bool> getKeyDown, Func<KeyCode, bool> getKey)
        {
            if (!focused || shortcut.MainKey == KeyCode.None || !getKeyDown(shortcut.MainKey)) return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
                if (!getKey(modifier)) return false;
            return true;
        }
    }
}
