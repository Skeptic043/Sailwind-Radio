using System;
using BepInEx.Configuration;

namespace SailwindRadio.Input
{
    // Bind text from the outset. KeyboardShortcut's converter turns invalid input
    // into Empty before a change handler can recover the original user value.
    internal sealed class ShortcutSetting
    {
        private readonly ConfigEntry<string> entry;
        private readonly Action<string> warning;
        private KeyboardShortcut parsed;
        private string lastRaw;
        private bool initialized;

        internal KeyboardShortcut Value
        {
            get => parsed;
            set => entry.Value = value.Serialize();
        }

        internal ShortcutSetting(ConfigFile config, string section, string key, KeyboardShortcut defaultValue,
            string description, Action<string> warning)
        {
            this.warning = warning;
            entry = config.Bind(section, key, defaultValue.Serialize(), description);
            entry.SettingChanged += (_, __) => Refresh();
            Refresh();
        }

        private void Refresh()
        {
            string raw = entry.Value;
            if (initialized && string.Equals(raw, lastRaw, StringComparison.Ordinal)) return;
            initialized = true;
            lastRaw = raw;
            if (!ShortcutParser.TryParse(raw, out parsed, out string error))
                warning?.Invoke("[" + entry.Definition.Section + "] " + entry.Definition.Key +
                    " is disabled because its shortcut is invalid. The value '" + raw + "' was kept unchanged. " + error);
        }
    }
}
