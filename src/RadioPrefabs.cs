// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RealisticRadio
{
    // Когда игра загрузилась: образцы деталей пересчитываются уже с известной сетью DSN (стоковый CommNet берёт
    // дальность выгруженных аппаратов из образцов), а в PluginData пишется сводка по всем антеннам - что задано
    // в конфиге, а что подобрано по названию. По сводке удобно поправить конфиг конкретной антенны
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RadioPrefabs : MonoBehaviour
    {
        public void Start()
        {
            try
            {
                RadioRangeModel.Install();
                Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RealisticRadio] Пересчёт антенн: {ex}");
            }
        }

        private static void Refresh()
        {
            var inv = CultureInfo.InvariantCulture;
            var report = new StringBuilder();
            report.AppendLine("# RealisticRadio: антенны этой игры. Формат: деталь | название | вид | полоса | усиление | конвертер | откуда | линия DSN");
            report.AppendLine($"# Мощность DSN высшего уровня: {ModuleRadioLink.DsnPower().ToString("E2", inv)}");
            int fromConfig = 0, matched = 0, links = 0;
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                Part prefab = part.partPrefab;
                if (prefab == null) continue;
                foreach (ModuleRadioLink link in prefab.FindModulesImplementing<ModuleRadioLink>())
                {
                    link.RefreshPrefab();
                    links++;
                }

                ModuleRadioAntenna antenna = prefab.FindModuleImplementing<ModuleRadioAntenna>();
                if (antenna == null) continue;
                antenna.Resolve();
                if (antenna.IsAutomatic) matched++;
                else fromConfig++;

                ModuleRadioLink dsn = prefab.FindModuleImplementing<ModuleRadioLink>();
                string gain = antenna.IsDirectional
                    ? $"{antenna.GainAt(antenna.BandCenterMHz).ToString("0.#", inv)} dBi, луч {antenna.BeamwidthAt(antenna.BandCenterMHz).ToString("0.#", inv)}°"
                    : $"{antenna.antennaGain.ToString("0.#", inv)} dBi";
                string lnb = antenna.LnbMHz > 0 ? antenna.LnbMHz.ToString("0.#", inv) + " МГц" : "-";
                string line = dsn != null
                    ? $"{dsn.frequencyMHz.ToString("0.###", inv)} МГц, {dsn.transmitPower.ToString("0.#", inv)} dBm, до {KSPUtil.PrintSI(dsn.DirectRangeMeters, "м")} (1 кбит/с - до {KSPUtil.PrintSI(dsn.UsefulRangeMeters, "м")})"
                    : "-";
                report.AppendLine($"{part.name} | {part.title} | {antenna.kind} | {antenna.Band()} | {gain} | {lnb} | {(antenna.IsAutomatic ? "подобрана" : "конфиг")} | {line}");
            }

            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RealisticRadio", "PluginData");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "AntennaReport.txt");
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[RealisticRadio] Антенны: {fromConfig} из конфига, {matched} подобраны по названию, линий DSN {links}; сводка - {path}");
        }
    }
}
