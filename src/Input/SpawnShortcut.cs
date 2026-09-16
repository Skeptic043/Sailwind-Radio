using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SailwindRadio.Input
{
    internal static class SpawnShortcut
    {
        // BepInEx configuration-manager recognizes this optional tag without a dependency.
        private sealed class HiddenSetting { public bool Browsable = false; }

        internal static ShortcutSetting Bind(ConfigFile config, Action<string> warn, Action<ConfigFile> persist = null)
        {
            bool autoSave = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                // Binding raw text consumes any orphan entry unchanged. TryGetEntry before Bind
                // cannot distinguish a loaded orphan from a fresh file in BepInEx 5.
                var setting = new ShortcutSetting(config, "Development", "SpawnRadio", new KeyboardShortcut(KeyCode.Home),
                    "Open the radio and speaker spawn menu during normal gameplay. Use key names such as Home, O or Mouse4. " +
                    "Chords such as LeftShift + Mouse4 also work. Key names ignore case and spaces. Leave blank or use None to disable.", warn);
                config.TryGetEntry<string>("Development", "SpawnRadio", out var raw);
                var marker = config.Bind("Internal", "SpawnDefaultRevision", 0,
                    new ConfigDescription("Tracks the completed default spawn key update.", null, new HiddenSetting()));
                if (marker.Value >= 1) return setting;
                var save = persist ?? (file => file.Save());
                try
                {
                    if (string.Equals(raw.Value, "F7", StringComparison.Ordinal)) raw.Value = "Home";
                    // Never persist a completed marker before the migrated key is on disk.
                    save(config);
                    marker.Value = 1;
                    save(config);
                }
                catch (Exception error)
                {
                    marker.Value = 0;
                    warn?.Invoke("Could not save the default radio key update. It will be retried next launch. " + error.Message);
                }
                return setting;
            }
            finally { config.SaveOnConfigSet = autoSave; }
        }
    }
}
