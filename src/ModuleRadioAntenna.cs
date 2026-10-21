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
    // Антенна - только железо: полоса согласования, усиление и луч (у тарелки - от частоты), шум приёмного тракта,
    // конвертер (LNB) на приёме и поворотное устройство. Что передавать и что слушать, решают радиоблоки аппарата.
    // У антенн других модов характеристик в конфиге нет - они подбираются по названию детали и стоковым параметрам
    public class ModuleRadioAntenna : PartModule
    {
        private const double DEG2RAD = Math.PI / 180.0;

        // Из cfg; нули - подобрать по названию и параметрам CommNet
        [KSPField] public float minFrequencyMHz = 0f;   // полоса согласования: КСВ не больше 2
        [KSPField] public float maxFrequencyMHz = 0f;
        [KSPField] public float antennaGain = 0f;       // dBi, у штыря и спирали
        [KSPField] public float beamwidth = 360f;       // градусы, 360 - всенаправленная
        [KSPField] public float dishDiameter = 0f;      // м: у тарелки усиление и луч считаются от частоты
        [KSPField] public float noiseTemperature = 0f;  // К: собственный шум приёмного тракта
        [KSPField] public float lnbDefaultMHz = -1f;    // гетеродин конвертера по умолчанию, 0 - без конвертера
        [KSPField] public string kind = "";
        [KSPField] public string hintName = "";         // имя и название детали для подбора (записывает патч MM)
        [KSPField] public string hintTitle = "";
        [KSPField] public bool dedicated = false;       // антенна прибора в той же детали (лопатка ответчика): радиоблоки через неё не передают

        // Игрок
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Сопровождение"),
         UI_Toggle(enabledText = "следит за целью", disabledText = "по оси детали")]
        public bool tracking = false;
        [KSPField(isPersistant = true)] public float lnbFrequencyMHz = -1f; // -1 - как по умолчанию
        [KSPField(isPersistant = true)] public bool lnbEnabled = true;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Антенна")]
        public string status = "";

        // Частоты, на которых показываются потери на приём: APT, UHF, ADS-B, HRPT, S и X
        private static readonly float[] probeFrequencies = { 137.5f, 437f, 1090f, 1698f, 2250f, 8420f };

        private bool resolved = false;
        private bool showWindow = false;
        private Rect windowRect = new Rect(360, 200, 380, 10);
        private string lnbText = "";

        public double BandCenterMHz { get { Resolve(); return 0.5 * (minFrequencyMHz + maxFrequencyMHz); } }

        public bool IsDish { get { Resolve(); return dishDiameter > 0; } }

        public bool IsDirectional { get { return BeamwidthAt(BandCenterMHz) < 180.0; } }

        public double LnbMHz { get { Resolve(); return lnbFrequencyMHz >= 0 ? lnbFrequencyMHz : Math.Max(lnbDefaultMHz, 0f); } }

        public bool LnbActive { get { return lnbEnabled && LnbMHz > 0; } }

        public double LnbHz { get { return LnbActive ? LnbMHz * 1e6 : 0.0; } }

        public string Title { get { return part != null && part.partInfo != null ? part.partInfo.title : part != null ? part.name : "антенна"; } }

        // У тарелки усиление G = 0.55·(π·D/λ)² и луч ≈ 70·λ/D зависят от частоты; у штыря - значения из конфига
        public double GainAt(double frequencyMHz)
        {
            Resolve();
            return dishDiameter > 0 ? RadioMath.DishGain(dishDiameter, frequencyMHz) : antennaGain;
        }

        public double BeamwidthAt(double frequencyMHz)
        {
            Resolve();
            return dishDiameter > 0 ? Math.Min(360.0, 70.0 * RadioMath.Wavelength(frequencyMHz) / dishDiameter) : beamwidth;
        }

        // Потери рассогласования на частоте (на приём и на передачу) и КСВ
        public double MatchingLossDb(double frequencyMHz, out double vswr)
        {
            Resolve();
            return RadioMath.MatchingLoss(frequencyMHz, minFrequencyMHz, maxFrequencyMHz, IsDish, out vswr);
        }

        // Всё, что антенна и конвертер отнимают у сигнала на приёме
        public double ReceiveLossDb(double frequencyMHz)
        {
            double vswr;
            double loss = MatchingLossDb(frequencyMHz, out vswr);
            if (LnbActive) loss += RadioMath.LnbFilterLoss(frequencyMHz, minFrequencyMHz, maxFrequencyMHz);
            return loss;
        }

        // Приёмник связи (командный, ретранслятор) через эту антенну: согласование и входной фильтр приёмника - чужой
        // диапазон он не слышит. SDR-приёмник дополнения слушает без фильтра (ReceiveLossDb)
        public double LinkReceiveLossDb(double frequencyMHz)
        {
            double vswr;
            return MatchingLossDb(frequencyMHz, out vswr) + RadioMath.BandFilterLoss(frequencyMHz, minFrequencyMHz, maxFrequencyMHz);
        }

        // Сколько ватт действительно уходит в эфир: часть отражается от несогласованной антенны,
        // а при большом КСВ защита передатчика снижает мощность
        public double RadiatedPowerW(double transmitterW, double frequencyMHz)
        {
            double vswr;
            double loss = MatchingLossDb(frequencyMHz, out vswr);
            return transmitterW * RadioMath.TransmitterFoldback(vswr) * Math.Pow(10.0, -loss / 10.0);
        }

        // Усиление в сторону direction (мировые оси) за вычетом потерь наведения и согласования
        public double EffectiveGainDb(double frequencyMHz, Vector3d direction, Vector3d? target)
        {
            double gain = GainAt(frequencyMHz);
            double offAxis = Vector3d.Angle(Boresight(target), direction);
            double vswr;
            return gain - RadioMath.PointingLoss(offAxis, BeamwidthAt(frequencyMHz), gain) - MatchingLossDb(frequencyMHz, out vswr);
        }

        // Куда смотрит антенна: с поворотным устройством - на цель, иначе вдоль оси детали
        public Vector3d Boresight(Vector3d? target)
        {
            if (tracking && IsDirectional && target.HasValue && part != null)
            {
                Vector3d toward = target.Value - (Vector3d)part.transform.position;
                if (toward.sqrMagnitude > 1e-6) return toward.normalized;
            }
            return part != null ? (Vector3d)part.transform.up : Vector3d.up;
        }

        public bool IsDeployed()
        {
            var deployable = part.FindModuleImplementing<ModuleDeployablePart>();
            return deployable == null || deployable.deployState == ModuleDeployablePart.DeployState.EXTENDED;
        }

        // Подбор характеристик, если в конфиге их нет: один раз, в том числе у образца детали
        public void Resolve()
        {
            if (resolved) return;
            resolved = true;
            if (minFrequencyMHz > 0 && maxFrequencyMHz > minFrequencyMHz)
            {
                if (noiseTemperature <= 0) noiseTemperature = 150f;
                if (string.IsNullOrEmpty(kind)) kind = dishDiameter > 0 ? $"тарелка ⌀{dishDiameter:0.#} м" : beamwidth >= 180 ? "всенаправленная" : "направленная";
                return;
            }

            string stockType;
            double stockPower;
            StockParameters(out stockType, out stockPower);
            bool deployable = part != null && part.FindModuleImplementing<ModuleDeployableAntenna>() != null;
            AntennaSpec spec = AntennaCatalog.Classify(Hint(), stockType, stockPower, deployable);
            minFrequencyMHz = (float)spec.minMHz;
            maxFrequencyMHz = (float)spec.maxMHz;
            dishDiameter = (float)spec.dishDiameter;
            antennaGain = (float)spec.gainDbi;
            beamwidth = (float)spec.beamwidthDeg;
            noiseTemperature = (float)spec.noiseK;
            if (lnbDefaultMHz < 0) lnbDefaultMHz = (float)spec.lnbMHz;
            if (string.IsNullOrEmpty(kind)) kind = spec.kind;
        }

        public bool IsAutomatic { get { return !string.IsNullOrEmpty(hintName) || !string.IsNullOrEmpty(hintTitle); } }

        private string Hint()
        {
            string title = hintTitle;
            try
            {
                if (!string.IsNullOrEmpty(title) && title.StartsWith("#")) title = Localizer.Format(title);
            }
            catch (Exception)
            {
                // Локализация ещё не готова - хватит имени детали
            }
            string name = !string.IsNullOrEmpty(hintName) ? hintName : part != null ? part.name : "";
            if (part != null && part.partInfo != null) title += " " + part.partInfo.title;
            return name + " " + title;
        }

        // Тип и "мощность" стокового передатчика детали: по ним понятно, штырь это или большая тарелка
        private void StockParameters(out string type, out double power)
        {
            type = AntennaCatalog.DIRECT;
            power = 0;
            if (part == null) return;
            foreach (PartModule module in part.Modules)
            {
                var link = module as ModuleRadioLink;
                if (link != null)
                {
                    type = link.isRelay ? AntennaCatalog.RELAY : AntennaCatalog.DIRECT;
                    power = link.stockPower;
                    return;
                }
                var transmitter = module as ModuleDataTransmitter;
                if (transmitter != null)
                {
                    type = transmitter.antennaType.ToString();
                    power = transmitter.antennaPower;
                    return;
                }
            }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Resolve();
            Fields["tracking"].guiActive = Fields["tracking"].guiActiveEditor = IsDirectional;
            UpdateStatus();
        }

        public override string GetInfo()
        {
            Resolve();
            string info = $"<b>Антенна: {kind}</b>\n";
            info += $"Согласована: {Mhz(minFrequencyMHz)}–{Mhz(maxFrequencyMHz)} МГц (КСВ ≤ 2), другие частоты - с потерями\n";
            info += IsDirectional
                ? $"Усиление {GainAt(BandCenterMHz):0} dBi, луч {BeamwidthAt(BandCenterMHz):0.#}°\n"
                : $"Усиление {antennaGain:0.#} dBi, всенаправленная\n";
            if (LnbMHz > 0) info += $"Конвертер на приём: гетеродин {Mhz(LnbMHz)} МГц\n";
            info += RadioHooks.PlusInstalled ? "Передаёт и принимает то, что задаст радиоблок аппарата" : "Через неё идёт связь с Землёй и ретрансляторами";
            if (IsAutomatic) info += "\n<color=#c3c2b7>Характеристики подобраны по названию детали</color>";
            return info;
        }

        public void UpdateStatus()
        {
            Resolve();
            string gain = IsDirectional ? $"{GainAt(BandCenterMHz):0} dBi" : $"{antennaGain:0.#} dBi";
            status = $"{kind}, {Band()}, {gain}";
        }

        public string Band()
        {
            Resolve();
            return maxFrequencyMHz >= 1000
                ? $"{minFrequencyMHz / 1000.0:0.0##}–{maxFrequencyMHz / 1000.0:0.0##} ГГц"
                : $"{minFrequencyMHz:0.#}–{maxFrequencyMHz:0.#} МГц";
        }

        // Антенны аппарата (у загруженного - по деталям)
        public static List<ModuleRadioAntenna> On(Vessel vessel)
        {
            var list = new List<ModuleRadioAntenna>();
            if (vessel == null) return list;
            foreach (Part p in vessel.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    var antenna = m as ModuleRadioAntenna;
                    if (antenna != null) list.Add(antenna);
                }
            }
            return list;
        }

        public static List<ModuleRadioAntenna> On(List<Part> parts)
        {
            var list = new List<ModuleRadioAntenna>();
            foreach (Part p in parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    var antenna = m as ModuleRadioAntenna;
                    if (antenna != null) list.Add(antenna);
                }
            }
            return list;
        }

        // --- Окно антенны: согласование и конвертер ---

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Антенна: конвертер и согласование")]
        public void ToggleWindow()
        {
            showWindow = !showWindow;
            lnbText = Mhz(LnbMHz);
        }

        public void OnGUI()
        {
            if (!showWindow) return;
            GUI.skin = HighLogic.Skin;
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Антенна: " + Title);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label($"{kind}. Согласована на {Mhz(minFrequencyMHz)}–{Mhz(maxFrequencyMHz)} МГц (КСВ ≤ 2).\n" +
                            "Принимает и передаёт и другие частоты, но с потерями:");
            var losses = new List<string>();
            foreach (float probe in probeFrequencies) losses.Add($"{probe:0.#}: {ReceiveLossDb(probe):0.#} дБ");
            GUILayout.Label(string.Join("   ", losses.ToArray()));
            GUILayout.Label(IsDirectional
                ? $"Направленная: {GainAt(BandCenterMHz):0} dBi, луч {BeamwidthAt(BandCenterMHz):0.#}° на середине полосы"
                : $"Всенаправленная, {antennaGain:0.#} dBi");

            if (IsDirectional)
            {
                bool newTracking = GUILayout.Toggle(tracking, " Поворотное устройство: на приёме смотрит на выбранную цель, на передаче - на приёмник");
                if (newTracking != tracking) Apply(a => a.tracking = newTracking);
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            bool newEnabled = GUILayout.Toggle(lnbEnabled, " Конвертер (LNB) на приёме, гетеродин, МГц:");
            if (newEnabled != lnbEnabled) Apply(a => a.lnbEnabled = newEnabled);
            lnbText = GUILayout.TextField(lnbText, 10, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            float typed;
            if (float.TryParse(lnbText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out typed) &&
                typed >= 0 && typed < 40000f && Math.Abs(typed - LnbMHz) > 1e-5f)
            {
                Apply(a => a.lnbFrequencyMHz = typed);
            }
            GUILayout.Label(LnbActive
                ? $"SDR видит частоту минус гетеродин: {Mhz(minFrequencyMHz - LnbMHz)}–{Mhz(maxFrequencyMHz - LnbMHz)} МГц; фильтр конвертера давит всё вне полосы антенны"
                : "Без конвертера SDR принимает частоту как есть: RTL-SDR до 1766 МГц, Airspy до 1800 МГц");

            if (HighLogic.LoadedSceneIsFlight && !IsDeployed()) GUILayout.Label("<color=orange>Антенна сложена - раскройте её</color>");
            GUILayout.Space(6);
            if (GUILayout.Button("Закрыть")) showWindow = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // Изменения сразу применяются и к симметричным деталям
        private void Apply(Action<ModuleRadioAntenna> change)
        {
            change(this);
            UpdateStatus();
            foreach (Part counterpart in part.symmetryCounterparts)
            {
                var other = counterpart.FindModuleImplementing<ModuleRadioAntenna>();
                if (other == null) continue;
                change(other);
                other.UpdateStatus();
            }
        }

        public static string Mhz(double value)
        {
            return value.ToString("0.000#", CultureInfo.InvariantCulture);
        }
    }
}
