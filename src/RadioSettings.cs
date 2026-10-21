// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RealisticRadio
{
    // Настройки мода (общие для всех сохранений): PluginData/Settings.cfg. Окна запоминают в них своё положение и вкладку,
    // дополнение - свои флаги (перед сохранением дописывает их через BeforeSave)
    public static class RadioSettings
    {
        private const string NODE = "REALISTICRADIO", OLD_NODE = "KERBALRADIO";

        private static readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        private static bool loaded;

        public static event Action BeforeSave;

        public static string Path
        {
            get { return System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/RealisticRadio/PluginData/Settings.cfg"); }
        }

        public static string Get(string key, string fallback = "")
        {
            Load();
            string value;
            return settings.TryGetValue(key, out value) ? value : fallback;
        }

        public static void Set(string key, string value)
        {
            Load();
            string old;
            if (settings.TryGetValue(key, out old) && old == value) return;
            settings[key] = value ?? "";
            Save();
        }

        // Записать значение без сохранения файла (для BeforeSave)
        public static void Put(string key, string value)
        {
            Load();
            settings[key] = value ?? "";
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(Path)) return;
                ConfigNode node = ConfigNode.Load(Path);
                // Раньше мод назывался KerbalRadio: его настройки тоже читаются, при сохранении узел получит новое имя
                ConfigNode saved = node != null ? node.GetNode(NODE) ?? node.GetNode(OLD_NODE) : null;
                if (saved == null) return;
                for (int i = 0; i < saved.values.Count; i++) settings[saved.values[i].name] = saved.values[i].value;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealisticRadio] Настройки не прочитались: {ex.Message}");
            }
        }

        public static void Save()
        {
            Load();
            try
            {
                if (BeforeSave != null) BeforeSave();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var root = new ConfigNode();
                ConfigNode saved = root.AddNode(NODE);
                var keys = new List<string>(settings.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys) saved.AddValue(key, settings[key]);
                root.Save(Path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealisticRadio] Настройки не сохранились: {ex.Message}");
            }
        }
    }
}
