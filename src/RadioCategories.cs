// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Localization;
using KSP.UI.Screens;
using UnityEngine;
using SelectableIcon = RUI.Icons.Selectable.Icon;

namespace RealisticRadio
{
    // Две вкладки в списке деталей редактора (VAB/SPH):
    //  - «Антенны: диапазоны частот» - антенны по полосам согласования (антенна шире одной полосы - в каждой своей),
    //    плюс радиоблоки и ответчики;
    //  - «Антенны: дальность связи» - для тех, кому частоты ни о чём не говорят: откуда антенна достанет до дома -
    //    околоземная орбита, Луна, Меркурий, Венера, Марс... Дальность - рабочей связи, 1 кбит/с (научные данные уходят
    //    за разумное время; управлять аппаратом можно и дальше, на меньшей скорости - это в описании детали), расстояние
    //    до тела - наибольшее возможное (оба тела в апоцентрах по разные стороны), из самой игры: Sol, сток или другая
    //    система - всё равно. Тело в списке - если антенна дотянется до него из любой точки орбиты
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RadioCategories : MonoBehaviour
    {
        private const string ICONS = "RealisticRadio/Icons/";
        private const double SMALL_BODY_SHARE = 0.07; // тела меньше 7 % радиуса дома (астероиды) - не отдельная цель

        private class Band
        {
            public readonly string id, title;
            public readonly double minMHz, maxMHz;

            public Band(string id, string title, double minMHz, double maxMHz)
            {
                this.id = id;
                this.title = title;
                this.minMHz = minMHz;
                this.maxMHz = maxMHz;
            }
        }

        // Полосы радиолюбителей и космической связи (IEEE для СВЧ)
        private static readonly Band[] bands =
        {
            new Band("HF", "#rr_cat_hf", 0, 30),
            new Band("VHF", "#rr_cat_vhf", 30, 300),
            new Band("UHF", "#rr_cat_uhf", 300, 1000),
            new Band("L", "#rr_cat_l", 1000, 2000),
            new Band("S", "#rr_cat_s", 2000, 4000),
            new Band("C", "#rr_cat_c", 4000, 8000),
            new Band("X", "#rr_cat_x", 8000, 12000),
            new Band("Ku", "#rr_cat_ku", 12000, 18000),
            new Band("Ka", "#rr_cat_ka", 18000, 40000)
        };

        private static readonly Dictionary<string, string> russian = new Dictionary<string, string>
        {
            { "Sun", "Солнце" }, { "Mercury", "Меркурий" }, { "Venus", "Венера" }, { "Earth", "Земля" }, { "Moon", "Луна" },
            { "Mars", "Марс" }, { "Phobos", "Фобос" }, { "Deimos", "Деймос" }, { "Ceres", "Церера" }, { "Vesta", "Веста" },
            { "Pallas", "Паллада" }, { "Jupiter", "Юпитер" }, { "Saturn", "Сатурн" }, { "Uranus", "Уран" }, { "Neptune", "Нептун" },
            { "Pluto", "Плутон" }, { "Eris", "Эрида" }, { "Makemake", "Макемаке" }, { "Haumea", "Хаумеа" },
            { "Kerbol", "Кербол" }, { "Moho", "Мохо" }, { "Eve", "Ева" }, { "Gilly", "Гилли" }, { "Kerbin", "Кербин" },
            { "Mun", "Муна" }, { "Minmus", "Минмус" }, { "Duna", "Дюна" }, { "Ike", "Айк" }, { "Dres", "Дрес" },
            { "Jool", "Джул" }, { "Laythe", "Лейт" }, { "Vall", "Валл" }, { "Tylo", "Тило" }, { "Bop", "Боп" }, { "Pol", "Пол" },
            { "Eeloo", "Илу" }
        };

        // Значки тел: астрономические знаки и буквы, остальным - общий
        private static readonly HashSet<string> bodyIcons = new HashSet<string>
        {
            "Moon", "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto", "Ceres", "Eris",
            "Moho", "Eve", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Eeloo"
        };

        // Что известно о детали: полосы её антенн, радиоблок ли она, дальность рабочей связи с DSN (0 - не передаёт домой)
        private class PartRadio
        {
            public readonly HashSet<string> bands = new HashSet<string>();
            public bool unit;
            public double reach;
        }

        private class Target
        {
            public string id, title, icon;
            public double distance;
        }

        private readonly Dictionary<string, PartRadio> parts = new Dictionary<string, PartRadio>();
        private bool scanned;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onGUIEditorToolbarReady.Add(AddFilters);
        }

        public void OnDestroy()
        {
            GameEvents.onGUIEditorToolbarReady.Remove(AddFilters);
        }

        // Редактор собрал свои вкладки - добавляем наши (при каждом входе в VAB/SPH)
        private void AddFilters()
        {
            try
            {
                if (!scanned) Scan();
                AddBandFilter();
                AddReachFilter();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RealisticRadio] Editor antenna tabs: {ex}");
            }
        }

        private void Scan()
        {
            scanned = true;
            DsnManager.LoadStations();
            foreach (AvailablePart available in PartLoader.LoadedPartsList)
            {
                Part prefab = available != null ? available.partPrefab : null;
                if (prefab == null) continue;
                try
                {
                    PartRadio radio = Classify(prefab);
                    if (radio.bands.Count > 0 || radio.unit || radio.reach > 0) parts[available.name] = radio;
                }
                catch (Exception ex)
                {
                    // Одна странная деталь не должна оставить редактор без вкладок
                    Debug.LogWarning($"[RealisticRadio] Antenna tabs: part {available.name} skipped: {ex.Message}");
                }
            }
            Debug.Log($"[RealisticRadio] Antenna tabs: {parts.Count} parts with radio");
        }

        private static PartRadio Classify(Part prefab)
        {
            double useful = Math.Pow(10.0, -(RadioMath.Cn0ForRate(RadioMath.USEFUL_DATA_RATE_BPS) - RadioMath.MIN_LINK_CN0) / 20.0);
            var radio = new PartRadio();
            foreach (PartModule module in prefab.Modules)
            {
                var antenna = module as ModuleRadioAntenna;
                if (antenna != null)
                {
                    antenna.Resolve();
                    foreach (Band band in bands)
                    {
                        if (antenna.minFrequencyMHz < band.maxMHz && antenna.maxFrequencyMHz > band.minMHz) radio.bands.Add(band.id);
                    }
                }
                if (module is IRadioUnitPart) radio.unit = true;

                var link = module as ModuleRadioLink;
                if (link != null)
                {
                    link.AutoConfigure();
                    radio.reach = Math.Max(radio.reach, link.UsefulRangeMeters);
                    continue;
                }
                // Особые передатчики других модов (облучатели, научные тарелки): дальность связи с домом по CommNet
                // (самая медленная линия) и рабочая - на 20 дБ ближе
                var transmitter = module as ModuleDataTransmitter;
                if (transmitter != null && transmitter.antennaType != AntennaType.INTERNAL && transmitter.antennaPower > 0)
                {
                    radio.reach = Math.Max(radio.reach, Math.Sqrt(transmitter.antennaPower * RadioRangeModel.RELAY_SCALE) * useful);
                }
            }
            return radio;
        }

        private PartRadio RadioOf(AvailablePart available)
        {
            PartRadio radio;
            return available != null && parts.TryGetValue(available.name, out radio) ? radio : null;
        }

        // --- Вкладка 1: диапазоны частот ---

        private void AddBandFilter()
        {
            PartCategorizer.Category filter = PartCategorizer.AddCustomFilter("RealisticRadioBands", Loc.T("#rr_cat_bands"),
                Icon("filter-bands"), new Color(0.24f, 0.52f, 0.86f));
            foreach (Band band in bands)
            {
                string id = band.id;
                if (!HasAny(r => r.bands.Contains(id))) continue;
                PartCategorizer.AddCustomSubcategoryFilter(filter, "band-" + id, Loc.T(band.title), Icon("band-" + id),
                    p => { PartRadio r = RadioOf(p); return r != null && r.bands.Contains(id); });
            }
            if (HasAny(r => r.unit))
            {
                PartCategorizer.AddCustomSubcategoryFilter(filter, "band-units", Loc.T("#rr_cat_units"), Icon("band-units"),
                    p => { PartRadio r = RadioOf(p); return r != null && r.unit; });
            }
        }

        // --- Вкладка 2: дальность связи для новичков ---

        private void AddReachFilter()
        {
            List<Target> targets = Targets();
            if (targets.Count == 0) return;
            PartCategorizer.Category filter = PartCategorizer.AddCustomFilter("RealisticRadioReach", Loc.T("#rr_cat_reach"),
                Icon("filter-reach"), new Color(0.88f, 0.6f, 0.2f));
            foreach (Target target in targets)
            {
                double distance = target.distance;
                PartCategorizer.AddCustomSubcategoryFilter(filter, "reach-" + target.id, Loc.T("#rr_cat_reach_item", target.title, Distance(distance)), Icon(target.icon),
                    p => { PartRadio r = RadioOf(p); return r != null && r.reach >= distance; });
            }
        }

        // Цели по дальности: орбита дома (до синхронной), его спутники, остальные крупные тела вокруг звезды
        private static List<Target> Targets()
        {
            var targets = new List<Target>();
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || FlightGlobals.Bodies == null) return targets;

            targets.Add(new Target
            {
                id = "orbit",
                title = home.bodyName == "Earth" ? Loc.T("#rr_cat_orbit_earth") : home.bodyName == "Kerbin" ? Loc.T("#rr_cat_orbit_kerbin") : Loc.T("#rr_cat_orbit", NameOf(home)),
                icon = "body-orbit",
                distance = OrbitReach(home)
            });

            CelestialBody root = home;
            while (root.referenceBody != null && root.referenceBody != root) root = root.referenceBody;
            var candidates = new List<Target>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body == home || body == root || body.orbit == null) continue;
                bool moonOfHome = body.referenceBody == home;
                bool planet = body.referenceBody == root && body.Radius >= home.Radius * SMALL_BODY_SHARE;
                if (!moonOfHome && !planet) continue;
                candidates.Add(new Target
                {
                    id = body.bodyName.Replace(' ', '-'),
                    title = NameOf(body),
                    icon = bodyIcons.Contains(body.bodyName) ? "body-" + body.bodyName : "body-other",
                    distance = MaxDistance(home, body)
                });
            }
            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            targets.AddRange(candidates);
            return targets;
        }

        // Околоорбитальная связь: до синхронной орбиты (у Земли - геостационарная), но не дальше половины сферы действия
        private static double OrbitReach(CelestialBody home)
        {
            double radius = home.Radius * 10.0;
            if (home.rotationPeriod > 0 && home.gravParameter > 0)
            {
                double t = home.rotationPeriod;
                radius = Math.Pow(home.gravParameter * t * t / (4.0 * Math.PI * Math.PI), 1.0 / 3.0);
            }
            if (home.sphereOfInfluence > 0 && !double.IsInfinity(home.sphereOfInfluence)) radius = Math.Min(radius, home.sphereOfInfluence * 0.5);
            return Math.Max(radius, home.Radius * 1.1);
        }

        // Наибольшее расстояние от дома до тела: оба в апоцентрах по разные стороны общего центра
        private static double MaxDistance(CelestialBody home, CelestialBody body)
        {
            var homeChain = new List<CelestialBody>();
            for (CelestialBody b = home; b != null; b = b.referenceBody == b ? null : b.referenceBody) homeChain.Add(b);
            double distance = body.Radius;
            CelestialBody common = null;
            for (CelestialBody b = body; b != null; b = b.referenceBody == b ? null : b.referenceBody)
            {
                if (homeChain.Contains(b))
                {
                    common = b;
                    break;
                }
                if (b.orbit != null) distance += b.orbit.ApR;
            }
            foreach (CelestialBody b in homeChain)
            {
                if (b == common) break;
                if (b.orbit != null) distance += b.orbit.ApR;
            }
            return distance;
        }

        private static string NameOf(CelestialBody body)
        {
            string name;
            if (Loc.IsRussian && russian.TryGetValue(body.bodyName, out name)) return name;
            try
            {
                return body.displayName.LocalizeRemoveGender();
            }
            catch (Exception)
            {
                return body.bodyName;
            }
        }

        private static string Distance(double meters)
        {
            CultureInfo culture = Loc.NumberCulture;
            double km = meters / 1000.0;
            if (km < 1e6) return Loc.T("#rr_dist_thousand", (km / 1e3).ToString(km < 1e5 ? "0.#" : "0", culture));
            if (km < 1e9) return Loc.T("#rr_dist_million", (km / 1e6).ToString(km < 1e8 ? "0.#" : "0", culture));
            return Loc.T("#rr_dist_billion", (km / 1e9).ToString("0.##", culture));
        }

        private bool HasAny(Predicate<PartRadio> match)
        {
            foreach (PartRadio radio in parts.Values)
            {
                if (match(radio)) return true;
            }
            return false;
        }

        // Значок из GameData/RealisticRadio/Icons: обычный (тёмный) и выбранный (светлый); нет файла - значок игры
        private static SelectableIcon Icon(string name)
        {
            Texture2D normal = GameDatabase.Instance.GetTexture(ICONS + name, false);
            Texture2D selected = GameDatabase.Instance.GetTexture(ICONS + name + "-sel", false);
            if (normal != null && selected != null) return new SelectableIcon("RealisticRadio-" + name, normal, selected, false);
            return PartCategorizer.Instance.iconLoader.GetIcon("R&D_node_icon_advelectrics");
        }
    }
}
