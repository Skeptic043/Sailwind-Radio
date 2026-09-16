using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using SailwindRadio.Input;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }

    public static void Main()
    {
        // Initialize the actual loader only against this test project's retained scratch tree.
        string scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".local", "config", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        typeof(Paths).GetMethod("SetExecutablePath", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { Path.Combine(scratch, "Fixture.exe"), Path.Combine(scratch, "BepInEx"),
                Path.Combine(scratch, "Managed"), Array.Empty<string>() });
        Check(Paths.ConfigPath.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "real BepInEx config paths remain inside test scratch");

        foreach (string raw in new[] { "o", "left shift + mouse 4" })
        {
            string path = Path.Combine(scratch, "old-typed-" + Guid.NewGuid().ToString("N") + ".cfg");
            File.WriteAllText(path, "[Development]\nSpawnRadio = " + raw + "\n");
            var config = new ConfigFile(path, false);
            var old = config.Bind("Development", "SpawnRadio", new KeyboardShortcut(KeyCode.F7));
            config.Save();
            Check(old.Value.Equals(KeyboardShortcut.Empty) && File.ReadAllLines(path).Contains("SpawnRadio = "),
                "reproduce original typed loader losing friendly key text: " + raw);
        }

        foreach (string name in Enum.GetNames(typeof(KeyCode)))
        {
            var expected = (KeyCode)Enum.Parse(typeof(KeyCode), name);
            Check(ShortcutParser.TryParse(name.ToLowerInvariant(), out var lower, out _) && lower.MainKey == expected,
                "every installed Unity key accepts lowercase: " + name);
            Check(ShortcutParser.TryParse(String.Join(" ", name.ToCharArray()), out var spaced, out _) && spaced.MainKey == expected,
                "every installed Unity key accepts spaces: " + name);
        }

        foreach (string raw in new[] { "left shift + mouse 4", "Mouse4 + LeftShift", "LEFTSHIFT + MOUSE4", "Mouse4 LeftShift",
            "Mouse4,LeftShift", "LeftShift;Mouse4", "Mouse4|LeftShift", "Mouse4 + LeftShift + LeftShift" })
        {
            Check(ShortcutParser.TryParse(raw, out var shortcut, out _) && shortcut.Equals(new KeyboardShortcut(KeyCode.Mouse4, KeyCode.LeftShift)),
                "friendly chords preserve Fast Forward behavior: " + raw);
            var held = new HashSet<KeyCode> { KeyCode.LeftShift, KeyCode.W, KeyCode.Mouse0 };
            Check(HotkeyInput.IsDown(shortcut, true, k => k == KeyCode.Mouse4, held.Contains), "unrelated movement and mouse controls permit chord");
            held.Remove(KeyCode.LeftShift);
            Check(!HotkeyInput.IsDown(shortcut, true, k => k == KeyCode.Mouse4, held.Contains), "required modifier cannot be omitted");
        }
        foreach (string raw in new[] { "", "  ", "None", "none" })
            Check(ShortcutParser.TryParse(raw, out var disabled, out _) && disabled.MainKey == KeyCode.None,
                "intentional disable: " + raw);
        foreach (string raw in new[] { "shift", "ctrl", "not a real key", "+ ; | ,", "None + F7", "999999" })
            Check(!ShortcutParser.TryParse(raw, out var disabled, out var error) && disabled.MainKey == KeyCode.None && !String.IsNullOrEmpty(error),
                "invalid binding is disabled with a reason: " + raw);

        var key = new KeyboardShortcut(KeyCode.O);
        Check(HotkeyInput.IsDown(key, true, k => k == KeyCode.O, k => k == KeyCode.W), "arbitrary rebound key fires with movement held");
        Check(!HotkeyInput.IsDown(key, true, _ => false, _ => true), "holding main key does not repeatedly spawn");
        Check(!HotkeyInput.IsDown(key, true, k => k == KeyCode.F7, _ => true), "old default no longer fires after rebinding");
        Check(!HotkeyInput.IsDown(key, false, _ => throw new Exception("unfocused input was sampled"), _ => true),
            "background input cannot activate or sample keys");
        Check(!HotkeyInput.IsDown(KeyboardShortcut.Empty, true, _ => throw new Exception("disabled input was sampled"), _ => true),
            "disabled binding cannot activate or sample keys");

        string freshPath = Path.Combine(scratch, "fresh.cfg");
        var fresh = new ShortcutSetting(new ConfigFile(freshPath, false), "Development", "SpawnRadio", new KeyboardShortcut(KeyCode.F7), "Test", null);
        Check(fresh.Value.MainKey == KeyCode.F7, "unchanged F7 default");

        foreach (string raw in new[] { "o", "left shift + mouse 4", "F6 + LeftControl", "bad key", "+ ; | ,", "", "None" })
        {
            string path = Path.Combine(scratch, "friendly-" + Guid.NewGuid().ToString("N") + ".cfg");
            File.WriteAllText(path, "[Development]\nSpawnRadio = " + raw + "\nLegacySetting = retained\n[Music]\nMusicFile = E:/Music/example.ogg\n");
            var warnings = new List<string>();
            var config = new ConfigFile(path, false);
            var setting = new ShortcutSetting(config, "Development", "SpawnRadio", new KeyboardShortcut(KeyCode.F7), "Test", warnings.Add);
            bool valid = ShortcutParser.TryParse(raw, out var expected, out _);
            Check(setting.Value.Equals(expected), "real config bind parses original key without migration: " + raw);
            Check(warnings.Count == (valid ? 0 : 1), "invalid raw text warns exactly once at bind");
            if (!valid) Check(warnings[0].Contains("[Development] SpawnRadio") && warnings[0].Contains(raw) && warnings[0].Contains("kept unchanged"),
                "warning identifies preserved invalid text");
            for (int i = 0; i < 20; i++) { var ignored = setting.Value; }
            config.Save();
            config.Reload();
            Check(warnings.Count == (valid ? 0 : 1), "unchanged reads and reloads do not repeat warning");
            Check(config.TryGetEntry<string>("Development", "SpawnRadio", out var entry) && entry.Value == raw,
                "actual loader preserves original raw text");
            string saved = File.ReadAllText(path);
            Check(saved.Contains("SpawnRadio = " + raw + Environment.NewLine) && saved.Contains("LegacySetting = retained") &&
                saved.Contains("MusicFile = E:/Music/example.ogg"), "saving preserves input and unrelated settings");
            var reloaded = new ShortcutSetting(new ConfigFile(path, false), "Development", "SpawnRadio", new KeyboardShortcut(KeyCode.F7), "Test", null);
            Check(reloaded.Value.Equals(expected), "fresh loader instance preserves effective binding");
            entry.Value = "another invalid key";
            Check(setting.Value.MainKey == KeyCode.None && warnings.Count == (valid ? 1 : 2), "changed invalid text updates cached result and warns");
            entry.Value = "another invalid key";
            Check(warnings.Count == (valid ? 1 : 2), "same invalid assignment stays quiet");
            entry.Value = "left shift + mouse 4";
            Check(setting.Value.Equals(new KeyboardShortcut(KeyCode.Mouse4, KeyCode.LeftShift)), "repairing raw entry activates new chord");
            setting.Value = new KeyboardShortcut(KeyCode.F10, KeyCode.RightControl);
            config.Reload();
            Check(setting.Value.Equals(new KeyboardShortcut(KeyCode.F10, KeyCode.RightControl)), "typed convenience setter survives actual save/reload");
        }
        foreach (string raw in new string[] { null, "F7", "f7", "LeftShift + F7", "o", "Home", "None", "", "not a key" })
        {
            string path = Path.Combine(scratch, "migration-" + Guid.NewGuid().ToString("N") + ".cfg");
            if (raw != null) File.WriteAllText(path, "[Development]\nSpawnRadio = " + raw + "\nLegacySetting = retained\n");
            var config = new ConfigFile(path, false);
            bool autoSave = config.SaveOnConfigSet;
            int saves = 0;
            var setting = SpawnShortcut.Bind(config, _ => { }, file =>
            {
                saves++;
                file.TryGetEntry<int>("Internal", "SpawnDefaultRevision", out var marker);
                Check(marker.Value == (saves == 1 ? 0 : 1), "migration marker follows successfully persisted key");
                file.Save();
            });
            string expectedRaw = raw == null || raw == "F7" ? "Home" : raw;
            config.TryGetEntry<string>("Development", "SpawnRadio", out var entry);
            Check(entry.Value == expectedRaw, "only exact old default migrates: " + (raw ?? "fresh"));
            Check(saves == 2 && config.SaveOnConfigSet == autoSave, "initial migration saves then marks and restores autosave");
            var restartedConfig = new ConfigFile(path, false);
            var restarted = SpawnShortcut.Bind(restartedConfig, _ => { }, _ => throw new Exception("repeat migration"));
            Check(restarted.Value.Equals(setting.Value), "completed migration survives new real config instance");
            if(raw!=null) Check(File.ReadAllText(path).Contains("LegacySetting = retained"), "migration preserves orphan setting");
            restartedConfig.TryGetEntry<string>("Development", "SpawnRadio", out var restartEntry);
            restartEntry.Value = "F7";
            var deliberate = SpawnShortcut.Bind(new ConfigFile(path, false), _ => { }, _ => throw new Exception("custom F7 migrated again"));
            Check(deliberate.Value.MainKey == KeyCode.F7, "later explicit F7 remains user binding");
        }
        for(int failureAt=1;failureAt<=2;failureAt++)
        {
            string path=Path.Combine(scratch,"failed-migration-"+failureAt+".cfg");
            File.WriteAllText(path,"[Development]\nSpawnRadio = F7\n");
            var config=new ConfigFile(path,false);
            int calls=0, warnings=0;
            var setting=SpawnShortcut.Bind(config,_=>warnings++,file=>
            {
                if(++calls==failureAt) throw new IOException("Simulated persistence failure");
                file.Save();
            });
            config.TryGetEntry<int>("Internal","SpawnDefaultRevision",out var marker);
            Check(marker.Value==0 && warnings==1 && setting.Value.MainKey==KeyCode.Home,"failed save leaves retry marker and usable runtime default");
            var restartedConfig=new ConfigFile(path,false);
            var restarted=SpawnShortcut.Bind(restartedConfig,_=>{ });
            restartedConfig.TryGetEntry<int>("Internal","SpawnDefaultRevision",out var restartedMarker);
            Check(restarted.Value.MainKey==KeyCode.Home && restartedMarker.Value==1,"failed migration retries safely next launch");
        }
        Console.WriteLine(checks + " input checks passed using the actual BepInEx config loader and installed Unity key enum. Live key events remain untested.");
    }
}
