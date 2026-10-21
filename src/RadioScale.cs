// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using UnityEngine;

namespace RealisticRadio
{
    // Масштаб планетной системы: во сколько раз она меньше настоящей Солнечной. Антенны и передатчики мода - настоящие,
    // а в уменьшенной системе они дотягивались бы в разы дальше, чем в жизни. Поэтому потери на трассе считаются по
    // расстоянию, умноженному на коэффициент: сигнал от «Марса» слабеет так же, как от настоящего Марса, и скорости с
    // запасами - как у настоящих АМС. Геометрия (горизонт, тела на пути, наведение), доплер и задержка сигнала - по
    // настоящим расстояниям игры: в уменьшенной системе короче и орбитальные периоды, растянутая задержка была бы длиннее,
    // чем в жизни, относительно самого полёта.
    // Sol - масштаб из его конфига (SystemScale = Real / Quarter / Stock), Real Solar System - настоящий, стоковая система
    // и неизвестные пакеты планет - масштаб Кербола (Кербин в 10.6 раза меньше Земли)
    public static class RadioScale
    {
        public const double KERBOL_FACTOR = 10.6;   // 6371 км / 600 км
        public const double SOL_QUARTER = 4.0, SOL_STOCK = 9.0;

        private static bool detected;
        private static double factor = KERBOL_FACTOR;
        private static string name = "масштаб Кербола";
        private static string unknown = "";

        public static double Factor { get { Detect(); return factor; } }

        // Пакет планет есть, но мод его не знает (или не понял его настройку) - считаем как сток. Пусто - всё узнано
        public static string Unknown { get { Detect(); return unknown; } }

        // Как называется система для окон: «Sol (Quarter)», «Real Solar System», «масштаб Кербола»
        public static string Name { get { Detect(); return name; } }

        // Потери в свободном пространстве по расстоянию игры, дБ: как на настоящем расстоянии
        public static double PathLossDb(double gameDistanceM, double frequencyMHz)
        {
            return RadioMath.FreeSpaceLoss(gameDistanceM * Factor / 1000.0, frequencyMHz);
        }

        // Настоящее расстояние -> расстояние игры (дальности антенн в окнах и для CommNet)
        public static double GameDistance(double realDistanceM)
        {
            return realDistanceM / Factor;
        }

        private static void Detect()
        {
            if (detected) return;
            if (GameDatabase.Instance == null) return;   // игра ещё грузится - пока масштаб Кербола, спросим позже
            detected = true;
            try
            {
                string scale = SolScale();
                if (scale != null)
                {
                    switch (scale.ToLowerInvariant())
                    {
                        case "real": factor = 1.0; break;
                        case "quarter": factor = SOL_QUARTER; break;
                        case "stock": factor = SOL_STOCK; break;
                        default:
                            factor = KERBOL_FACTOR;
                            unknown = scale.Length > 0 ? $"у Sol неизвестный масштаб SystemScale = {scale}" : "у Sol не указан масштаб SystemScale";
                            break;
                    }
                    name = $"Sol ({(scale.Length > 0 ? scale : "?")})";
                }
                else if (RealSolarSystem())
                {
                    factor = 1.0;
                    name = "Real Solar System";
                }
                else
                {
                    ConfigNode[] kopernicus = GameDatabase.Instance.GetConfigNodes("Kopernicus");
                    factor = KERBOL_FACTOR;
                    bool pack = kopernicus != null && kopernicus.Length > 0;
                    name = pack ? "неизвестный пакет планет - как Кербол" : "масштаб Кербола";
                    if (pack) unknown = "стоит Kopernicus с пакетом планет, который мод не знает";
                }
            }
            catch (Exception ex)
            {
                factor = KERBOL_FACTOR;
                name = "масштаб Кербола";
                unknown = $"ошибка при чтении настроек: {ex.Message}";
                Debug.LogWarning($"[RealisticRadio] Масштаб системы не определился, считаю как Кербол: {ex.Message}");
            }
            Debug.Log($"[RealisticRadio] Масштаб системы: {name}, коэффициент {factor:0.##} - потери на трассе как на настоящих расстояниях");
        }

        // SystemScale из настроек Sol: свой узел Sol_Configuration, а если его нет - копии, которые Sol кладёт в
        // PHYSICSGLOBALS и Kopernicus. null - Sol не стоит. Sol делит на масштаб и радиусы, и орбиты тел
        private static string SolScale()
        {
            ConfigNode[] own = GameDatabase.Instance.GetConfigNodes("Sol_Configuration");
            if (own != null)
            {
                foreach (ConfigNode node in own)
                {
                    if (node.HasValue("SystemScale")) return node.GetValue("SystemScale").Trim();
                }
            }
            foreach (string parent in new[] { "PHYSICSGLOBALS", "Kopernicus" })
            {
                ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes(parent);
                if (nodes == null) continue;
                foreach (ConfigNode node in nodes)
                {
                    foreach (ConfigNode sol in node.GetNodes("Sol_Configuration"))
                    {
                        if (sol.HasValue("SystemScale")) return sol.GetValue("SystemScale").Trim();
                    }
                }
            }
            return own != null && own.Length > 0 ? "" : null;
        }

        private static bool RealSolarSystem()
        {
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                if (a != null && a.name == "RealSolarSystem") return true;
            }
            ConfigNode[] rss = GameDatabase.Instance.GetConfigNodes("REALSOLARSYSTEM");
            return rss != null && rss.Length > 0;
        }
    }

    // После загрузки игры: если пакет планет не узнан, окно - расчёты связи пойдут по масштабу стоковой системы
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RadioScaleWarning : MonoBehaviour
    {
        public System.Collections.IEnumerator Start()
        {
            // Пара кадров, чтобы главное меню успело построиться и окно не спряталось под ним
            yield return null;
            yield return null;
            string unknown = RadioScale.Unknown;
            if (unknown.Length == 0) yield break;
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "RealisticRadioScale",
                "RealisticRadio: пакет планет не определён",
                $"Не удалось определить масштаб планетной системы: {unknown}.\n\n" +
                $"Все расчёты связи будут идти по стоку - как в системе Кербола (потери на трассе как на расстоянии в " +
                $"{RadioScale.KERBOL_FACTOR:0.#} раза больше игрового). Дальности антенн могут не совпадать с размерами вашей системы.\n\n" +
                "Сейчас мод узнаёт: стоковую систему, Sol (Real, Quarter, Stock) и Real Solar System.",
                "Понятно", false, HighLogic.UISkin);
        }
    }
}
