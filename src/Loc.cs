// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Localization;
using UnityEngine;

namespace RealisticRadio
{
    // Тексты интерфейса: таблицы Localization из GameData/RealisticRadio/Localization. Язык - как у игры, можно задать свой:
    // language = en-us в PluginData/Settings.cfg. Для языка без таблицы - английский, а не сырые теги, как было бы через
    // Localizer. Подстановки <<1>>, <<2>>... - простой заменой: окна пересчитывают строки каждый кадр
    public static class Loc
    {
        private const string FALLBACK = "en-us";
        private static Dictionary<string, string> table;
        private static string language = FALLBACK;

        public static bool IsRussian { get { Load(); return language.StartsWith("ru", StringComparison.OrdinalIgnoreCase); } }

        // Числа в окнах редактора: у русского - запятая
        public static CultureInfo NumberCulture { get { return IsRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture; } }

        // Текст по тегу; свободный текст (название из чужого конфига) и неизвестный тег возвращаются как есть
        public static string T(string key)
        {
            return Find(key) ?? key;
        }

        public static string T(string key, params object[] args)
        {
            string text = T(key);
            if (args == null) return text;
            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i] is IFormattable ? ((IFormattable)args[i]).ToString(null, CultureInfo.InvariantCulture) : Convert.ToString(args[i]);
                text = text.Replace("<<" + (i + 1) + ">>", value);
            }
            return text;
        }

        // null - тега нет
        public static string Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Load();
            string text;
            return table != null && table.TryGetValue(key, out text) ? text : null;
        }

        private static void Load()
        {
            if (table != null || GameDatabase.Instance == null) return;
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("Localization");
            if (nodes == null || nodes.Length == 0) return;   // игра ещё грузит конфиги - спросим позже

            language = ChosenLanguage();
            var loaded = new Dictionary<string, string>();
            Read(nodes, FALLBACK, loaded);
            if (!string.Equals(language, FALLBACK, StringComparison.OrdinalIgnoreCase)) Read(nodes, language, loaded);
            if (loaded.Count == 0) return;
            table = loaded;
            Debug.Log($"[RealisticRadio] Interface language: {language}, {table.Count} strings");
        }

        private static string ChosenLanguage()
        {
            string chosen = RadioSettings.Get("language", "auto").Trim();
            if (chosen.Length > 0 && !string.Equals(chosen, "auto", StringComparison.OrdinalIgnoreCase)) return chosen;
            try
            {
                return string.IsNullOrEmpty(Localizer.CurrentLanguage) ? FALLBACK : Localizer.CurrentLanguage;
            }
            catch (Exception)
            {
                return FALLBACK;
            }
        }

        private static void Read(ConfigNode[] nodes, string lang, Dictionary<string, string> into)
        {
            foreach (ConfigNode node in nodes)
            {
                foreach (ConfigNode block in node.GetNodes())
                {
                    if (!string.Equals(block.name, lang, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ConfigNode.Value value in block.values)
                    {
                        if (value.name.StartsWith("#rr_", StringComparison.Ordinal)) into[value.name] = value.value.Replace("\\n", "\n");
                    }
                }
            }
        }
    }

    // RadioMath не зависит от игры: тексты для него подключаются здесь, до загрузки деталей
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LocBoot : MonoBehaviour
    {
        public void Awake()
        {
            RadioMath.Translate = Loc.Find;
        }
    }
}
