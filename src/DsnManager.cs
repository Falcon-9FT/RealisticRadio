// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RealisticRadio
{
    public enum StationAntennaKind { Dish, Omni, Yagi }

    // Антенна наземной станции: тарелка (усиление и луч считаются от диаметра и частоты) или штырь и антенная решётка
    // с усилением и лучом из конфига. Полоса - облучателя или самой антенны: вне её сигнал ослабляет рассогласование.
    // У облучателей S, X и Ka - конвертер: как у настоящих станций, сигнал переносится вниз, в диапазон SDR
    // (частота в SDR = частота сигнала - гетеродин), иначе RTL-SDR и Airspy его просто не достанут
    public class StationAntenna
    {
        private const double IN_BAND_LOSS_DB = 3.0;   // потери облучателя, до которых частота считается его
        private const double IN_BAND_TOLERANCE = 0.01; // и насколько частота может выйти за края полосы

        public StationAntennaKind kind = StationAntennaKind.Dish;
        public string title = "";
        public double diameter;               // м, у тарелки
        public double gainDbi = 2.0;          // у штыря и решётки
        public double beamwidthDeg = 360.0;
        public double minMHz = 100.0, maxMHz = 40000.0;
        public double noiseTemperature = 20.0; // К: собственный шум тракта без неба
        public double lnbMHz;                  // гетеродин конвертера, 0 - без конвертера
        public double uplinkW;                 // передатчик командной линии в этом диапазоне, Вт (0 - только приём)
        public bool bandFilter;                // входной фильтр облучателя: чужие диапазоны давит, как фильтр конвертера

        public bool IsDish { get { return kind == StationAntennaKind.Dish && diameter > 0; } }
        public double BandCenterMHz { get { return Math.Sqrt(minMHz * maxMHz); } }
        public bool Directional { get { return BeamwidthAt(BandCenterMHz) < 180.0; } }

        // Частота в диапазоне облучателя: команды уходят в парной полосе того же диапазона (у DSN - S 2110 на приём 2290,
        // X 7170 на 8420), поэтому передатчик облучателя достаёт аппарат, который он слышит
        public bool InBand(double frequencyMHz)
        {
            // Передатчик настроен на свой диапазон: гармоники штыря и решётки (437 МГц на УКВ-решётке) ему не полоса
            return frequencyMHz >= minMHz * (1.0 - IN_BAND_TOLERANCE) && frequencyMHz <= maxMHz * (1.0 + IN_BAND_TOLERANCE) &&
                   ReceiveLossDb(frequencyMHz) < IN_BAND_LOSS_DB;
        }

        public StationAntenna Clone()
        {
            return (StationAntenna)MemberwiseClone();
        }

        public double GainAt(double frequencyMHz)
        {
            return IsDish ? RadioMath.DishGain(diameter, frequencyMHz) : gainDbi;
        }

        public double BeamwidthAt(double frequencyMHz)
        {
            return IsDish ? Math.Min(360.0, 70.0 * RadioMath.Wavelength(frequencyMHz) / diameter) : beamwidthDeg;
        }

        public double MatchingLossDb(double frequencyMHz, out double vswr)
        {
            return RadioMath.MatchingLoss(frequencyMHz, minMHz, maxMHz, IsDish, out vswr);
        }

        // Что сигнал на этой частоте теряет в облучателе и конвертере (входной фильтр давит чужие диапазоны: рупор
        // тарелки ниже своей полосы - запредельный волновод, выше - его режет фильтр малошумящего усилителя)
        public double ReceiveLossDb(double frequencyMHz)
        {
            double vswr;
            double loss = MatchingLossDb(frequencyMHz, out vswr);
            if (bandFilter)
            {
                // Полосовой фильтр станции крутой: X 8.4 ГГц на 7.8 ГГц уже не слышит, а чужой диапазон - совсем
                loss += RadioMath.BandFilterLoss(frequencyMHz, minMHz, maxMHz);
            }
            else if (lnbMHz > 0)
            {
                loss += RadioMath.LnbFilterLoss(frequencyMHz, minMHz, maxMHz);
            }
            return loss;
        }

        public double NetGain(double frequencyMHz)
        {
            return GainAt(frequencyMHz) - ReceiveLossDb(frequencyMHz);
        }

        // Облучатель тарелки коротко: «S 2.2–2.3 ГГц (в SDR −2 ГГц; команды 20 кВт)» или «L 1.67–1.71 ГГц напрямую»
        public string Describe()
        {
            string band = Range(minMHz, maxMHz);
            string uplink = uplinkW > 0 ? "команды " + Power(uplinkW) : "";
            string through = lnbMHz > 0 ? $"{band} (в SDR −{Freq(lnbMHz)}" + (uplink.Length > 0 ? "; " + uplink : "") + ")"
                                        : band + " напрямую" + (uplink.Length > 0 ? $" ({uplink})" : "");
            string name = string.IsNullOrEmpty(title) ? "" : title + " ";
            switch (kind)
            {
                case StationAntennaKind.Omni: return $"{name}штырь {gainDbi:0.#} dBi, {through}";
                case StationAntennaKind.Yagi: return $"{name}решётка {gainDbi:0.#} dBi, луч {beamwidthDeg:0}°, {through}";
                default: return name + through;
            }
        }

        // 435 МГц, 2 ГГц, 13.5 ГГц
        public static string Freq(double mhz)
        {
            var inv = CultureInfo.InvariantCulture;
            return mhz >= 1000.0 ? (mhz / 1000.0).ToString("0.###", inv) + " ГГц" : mhz.ToString("0.###", inv) + " МГц";
        }

        // 2.2–2.3 ГГц, 920–932 МГц, 900 МГц–1.8 ГГц
        public static string Range(double minMHz, double maxMHz)
        {
            var inv = CultureInfo.InvariantCulture;
            if (minMHz >= 1000.0) return (minMHz / 1000.0).ToString("0.###", inv) + "–" + Freq(maxMHz);
            if (maxMHz < 1000.0) return minMHz.ToString("0.###", inv) + "–" + Freq(maxMHz);
            return Freq(minMHz) + "–" + Freq(maxMHz);
        }

        // 100 Вт, 20 кВт
        public static string Power(double watts)
        {
            var inv = CultureInfo.InvariantCulture;
            return watts >= 1000.0 ? (watts / 1000.0).ToString("0.#", inv) + " кВт" : watts.ToString("0.#", inv) + " Вт";
        }
    }

    public class DsnStation
    {
        public string name;
        public string bodyName;
        public double lat;
        public double lon;
        public double altitude = 20.0;           // м над уровнем моря: место и опора антенны
        public double diameter;                  // м: самая большая тарелка станции (0 - тарелки нет)
        public double noiseTemperature;          // К: криогенный малошумящий усилитель
        public double slewRate;                  // °/с: как быстро поворачивается антенна (0 - по диаметру)
        public double minElevation = 6.0;        // °: ниже антенну не опускают (принимать у самого горизонта она может)
        public int rtlPort, spyPort;            // порты SDR-сервера (0 - по порядку от портов сети)
        public readonly List<StationAntenna> antennas = new List<StationAntenna>();

        // Антенна (облучатель), через которую сигнал на этой частоте приходит лучше всего: усиление минус
        // рассогласование и фильтр конвертера - у тарелки с несколькими облучателями усиление одно, решает полоса
        public StationAntenna MainAntenna(double frequencyMHz)
        {
            StationAntenna best = null;
            foreach (StationAntenna a in antennas)
            {
                if (best == null || a.NetGain(frequencyMHz) > best.NetGain(frequencyMHz)) best = a;
            }
            return best;
        }

        // Частота сигнала в SDR, подключённом к станции: за вычетом гетеродина облучателя, через который он приходит;
        // NaN - станция эту частоту не слышит (ни один облучатель её не берёт)
        public double SdrHz(double frequencyMHz)
        {
            StationAntenna main = MainAntenna(frequencyMHz);
            if (main == null || main.ReceiveLossDb(frequencyMHz) > HEARD_LOSS_DB) return double.NaN;
            return (frequencyMHz - main.lnbMHz) * 1e6;
        }

        private const double HEARD_LOSS_DB = 20.0; // потери облучателя, при которых станция ещё что-то слышит

        // Усиление на этой частоте за вычетом потерь облучателя: частоту вне всех полос станции давят входные фильтры
        public double NetGainAt(double frequencyMHz)
        {
            StationAntenna main = MainAntenna(frequencyMHz);
            return main != null ? main.NetGain(frequencyMHz) : RadioMath.DishGain(diameter, frequencyMHz) - 50.0;
        }

        public double NoiseTemperatureAt(double frequencyMHz)
        {
            StationAntenna main = MainAntenna(frequencyMHz);
            return (main != null ? main.noiseTemperature : noiseTemperature) + RadioMath.SkyNoise(frequencyMHz);
        }

        // Добротность приёма G/T, дБ/К: чем больше, тем дальше станция слышит аппарат на этой частоте
        public double FigureOfMerit(double frequencyMHz)
        {
            return NetGainAt(frequencyMHz) - 10.0 * Math.Log10(Math.Max(NoiseTemperatureAt(frequencyMHz), 1.0));
        }

        // Облучатель с передатчиком, который достаёт аппарат на этой частоте; null - в этом диапазоне станция только слушает
        public StationAntenna UplinkAntenna(double frequencyMHz)
        {
            StationAntenna best = null;
            foreach (StationAntenna a in antennas)
            {
                if (a.uplinkW <= 0 || !a.InBand(frequencyMHz)) continue;
                if (best == null || a.NetGain(frequencyMHz) > best.NetGain(frequencyMHz)) best = a;
            }
            return best;
        }

        // Диапазоны приёма словами: «S, X, Ka»
        public string ReceiveBands
        {
            get
            {
                var parts = new List<string>();
                foreach (StationAntenna a in antennas) parts.Add(string.IsNullOrEmpty(a.title) ? StationAntenna.Range(a.minMHz, a.maxMHz) : a.title);
                return string.Join(", ", parts.ToArray());
            }
        }

        // Диапазоны передатчиков словами: «S 20 кВт, X 20 кВт» или «нет»
        public string TransmitBands
        {
            get
            {
                var parts = new List<string>();
                foreach (StationAntenna a in antennas)
                {
                    if (a.uplinkW > 0) parts.Add((string.IsNullOrEmpty(a.title) ? StationAntenna.Freq(a.BandCenterMHz) : a.title) + " " + StationAntenna.Power(a.uplinkW));
                }
                return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "нет";
            }
        }

        // Большие тарелки ворочаются медленно: 70 м - четверть градуса в секунду (быстрый низкоорбитальный аппарат над
        // головой они не догонят, для этого есть малые станции), штырю поворачиваться незачем
        public double SlewRate
        {
            get
            {
                if (slewRate > 0) return slewRate;
                if (diameter >= 60) return 0.25;
                if (diameter >= 30) return 0.8;
                if (diameter >= 15) return 2.0;
                return 5.0;
            }
        }

        public bool Steerable
        {
            get
            {
                foreach (StationAntenna a in antennas)
                {
                    if (a.Directional) return true;
                }
                return false;
            }
        }

        public string Describe()
        {
            var parts = new List<string>();
            foreach (StationAntenna a in antennas) parts.Add(a.Describe());
            string feeds = string.Join(", ", parts.ToArray());
            return diameter > 0 && antennas.TrueForAll(a => a.IsDish) ? $"тарелка ⌀{diameter:0.#} м: {feeds}" : feeds;
        }
    }

    public static class DsnManager
    {
        private const double DEFAULT_DIAMETER = 70.0;
        private const double DEFAULT_NOISE = 20.0;
        private const double LEGACY_GAIN_FREQUENCY = 8400.0; // старые конфиги задавали усиление в X-диапазоне

        public const int DEFAULT_RTL_BASE = 1300, DEFAULT_SPY_BASE = 5600;

        public static List<DsnStation> Stations { get; private set; }

        // Порты SDR сети: база - порт «сети» (станцию выбирает сама), станции - следующие по порядку
        public static int RtlPortBase { get; private set; } = DEFAULT_RTL_BASE;
        public static int SpyPortBase { get; private set; } = DEFAULT_SPY_BASE;
        public static bool SdrEnabled { get; private set; } = true;

        private static bool? configuredBodyExists = null;

        // Тело станции - по имени из конфига; станции тел, которых в игре нет, не работают (в конфиге есть и Кербин,
        // и Земля реальной Солнечной системы). Если в игре нет ни одного тела из конфига (планетный мод назвал
        // родную планету иначе), все станции встают на родную планету - иначе без связи осталась бы вся игра
        public static CelestialBody BodyOf(DsnStation station)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || bodies.Count == 0) return null;

            CelestialBody body = bodies.Find(b => b.name == station.bodyName);
            if (body != null) return body;

            if (configuredBodyExists == null)
            {
                LoadStations();
                configuredBodyExists = Stations.Exists(s => bodies.Exists(b => b.name == s.bodyName));
                if (configuredBodyExists == false)
                {
                    Debug.LogWarning($"[RealisticRadio] Тел из DsnStations.cfg в игре нет - станции DSN стоят на родной планете {FlightGlobals.GetHomeBody()?.bodyName}");
                }
            }
            return configuredBodyExists == true ? null : FlightGlobals.GetHomeBody();
        }

        private static double bestForMHz = double.NaN;
        private static bool bestForBodies;
        private static DsnStation bestFor;

        // Станция, которая лучше всех слышит эту частоту (по G/T): по ней считается дальность антенн и приём на стоковых
        // наземных станциях. 70 м DSN на УКВ не слушает - аппарат на 137 МГц ловит радиотелескоп с облучателем УКВ.
        // Берутся станции тел, что есть в игре; пока тела не загружены (детали собираются на загрузке игры) - все
        public static DsnStation BestFor(double frequencyMHz)
        {
            LoadStations();
            bool bodies = BodiesLoaded();
            if (bestFor != null && frequencyMHz == bestForMHz && bodies == bestForBodies) return bestFor;

            DsnStation best = null;
            double bestScore = double.NegativeInfinity;
            foreach (DsnStation st in Stations)
            {
                if (bodies && BodyOf(st) == null) continue;
                double score = st.FigureOfMerit(frequencyMHz);
                if (best == null || score > bestScore)
                {
                    best = st;
                    bestScore = score;
                }
            }
            bestFor = best ?? Stations[0];
            bestForMHz = frequencyMHz;
            bestForBodies = bodies;
            return bestFor;
        }

        private static bool BodiesLoaded()
        {
            try
            {
                return FlightGlobals.fetch != null && FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void LoadStations()
        {
            if (Stations != null) return;

            Stations = new List<DsnStation>();
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALISTIC_RADIO_DSN");

            if (nodes != null)
            {
                foreach (ConfigNode node in nodes)
                {
                    int port;
                    if (int.TryParse(node.GetValue("rtlPortBase"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0) RtlPortBase = port;
                    if (int.TryParse(node.GetValue("spyPortBase"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0) SpyPortBase = port;
                    bool sdr;
                    if (bool.TryParse(node.GetValue("sdr"), out sdr)) SdrEnabled = sdr;

                    foreach (ConfigNode stationNode in node.GetNodes("Station"))
                    {
                        DsnStation st = Read(stationNode);
                        if (Stations.Exists(s => s.name == st.name && s.bodyName == st.bodyName))
                        {
                            Debug.LogWarning($"[RealisticRadio] Станция \"{st.name}\" ({st.bodyName}) объявлена повторно, дубликат пропущен");
                            continue;
                        }
                        Stations.Add(st);
                    }
                }
            }

            if (Stations.Count == 0)
            {
                var fallback = new DsnStation
                {
                    name = "Fallback", bodyName = "Kerbin", lat = 0, lon = -74.5,
                    diameter = DEFAULT_DIAMETER, noiseTemperature = DEFAULT_NOISE
                };
                var dish = new StationAntenna { diameter = DEFAULT_DIAMETER, noiseTemperature = DEFAULT_NOISE };
                foreach (StationAntenna feed in Feeds(dish, DEFAULT_RECEIVE, "Fallback"))
                {
                    feed.uplinkW = DefaultUplink(feed);
                    fallback.antennas.Add(feed);
                }
                Stations.Add(fallback);
            }
        }

        // Станция из конфига: одна антенна полями станции (diameter или gain тарелки; antenna = omni/yagi - штырь или
        // решётка) или несколько узлами ANTENNA
        private static DsnStation Read(ConfigNode node)
        {
            var st = new DsnStation
            {
                name = (node.GetValue("name") ?? "Unknown").Trim(),
                bodyName = (node.GetValue("body") ?? "Kerbin").Trim(),
                lat = ReadDouble(node, "lat", 0.0),
                lon = ReadDouble(node, "lon", 0.0),
                altitude = ReadDouble(node, "altitude", 20.0),
                noiseTemperature = ReadDouble(node, "noiseTemperature", DEFAULT_NOISE),
                slewRate = ReadDouble(node, "slewRate", 0.0),
                minElevation = ReadDouble(node, "minElevation", 6.0)
            };
            st.rtlPort = (int)ReadDouble(node, "rtlPort", 0);
            st.spyPort = (int)ReadDouble(node, "spyPort", 0);

            ConfigNode[] antennaNodes = node.GetNodes("ANTENNA");
            if (antennaNodes.Length > 0)
            {
                foreach (ConfigNode a in antennaNodes) st.antennas.AddRange(ReadAntenna(a, node, st));
            }
            else
            {
                st.antennas.AddRange(ReadAntenna(node, node, st));
            }
            foreach (StationAntenna a in st.antennas)
            {
                if (a.IsDish) st.diameter = Math.Max(st.diameter, a.diameter);
            }
            return st;
        }

        // Диапазоны облучателей: полоса приёма и гетеродин конвертера. SDR настраивается до 2 ГГц: всё ниже идёт как есть,
        // выше - конвертерами с теми же гетеродинами, что у антенн на аппаратах: S 2215 МГц приходит в SDR на 215,
        // X 8420 - на 620, Ka 32 ГГц - на 1000. Полосы приёма - как у настоящих станций (DSN: S 2200-2300, X 8400-8500,
        // Ka 31.8-32.3 ГГц); команды уходят в парных полосах тех же диапазонов (S 2025-2120, X 7145-7190, Ka 34.2-34.7)
        private class Band
        {
            public readonly string name;
            public readonly double min, max, lo;

            public Band(string name, double min, double max, double lo)
            {
                this.name = name;
                this.min = min;
                this.max = max;
                this.lo = lo;
            }
        }

        private static readonly Band[] BANDS =
        {
            new Band("VHF", 136.0, 148.0, 0.0),        // телеметрия первых спутников, NOAA APT, любители 2 м
            new Band("UHF", 400.0, 450.0, 0.0),        // кубсаты, любители 70 см, марсианская связь 401/437 МГц
            new Band("L", 1670.0, 1710.0, 0.0),        // метеоспутники: HRPT 1698-1707 МГц
            new Band("S", 2200.0, 2300.0, 2000.0),
            new Band("C", 3700.0, 4200.0, 3500.0),
            new Band("X", 8400.0, 8500.0, 7800.0),     // дальний космос; ближний (спутники Земли) - 8025-8400
            new Band("Ku", 13700.0, 15100.0, 13500.0),
            new Band("K", 25500.0, 27000.0, 25400.0),  // Ka ближнего космоса (LRO, спутники Земли)
            new Band("Ka", 31800.0, 32300.0, 31000.0)  // Ka дальнего космоса
        };
        private const string DEFAULT_RECEIVE = "S, X";

        private static Band FindBand(string name)
        {
            switch (name.ToUpperInvariant())
            {
                case "УКВ": name = "VHF"; break;
                case "ДМВ": name = "UHF"; break;
            }
            foreach (Band b in BANDS)
            {
                if (string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        // Облучатели по списку диапазонов: «S, X, Ka» или со своей полосой «X 8025-8500», «ДМВ 920-925»
        private static List<StationAntenna> Feeds(StationAntenna template, string list, string where)
        {
            var feeds = new List<StationAntenna>();
            foreach (string raw in list.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                string[] w = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Band band = FindBand(w[0]);
                double min = 0, max = 0;
                bool custom = w.Length > 1 && TryReadRange(w[1], out min, out max);
                if (!custom)
                {
                    if (band == null)
                    {
                        Debug.LogWarning($"[RealisticRadio] {where}: непонятный диапазон «{token}» (нужно {BandNames()} или «имя мин-макс» в МГц)");
                        continue;
                    }
                    min = band.min;
                    max = band.max;
                }
                StationAntenna feed = template.Clone();
                feed.title = band != null ? band.name : w[0];
                feed.minMHz = min;
                feed.maxMHz = max;
                // Гетеродин диапазона, если своя полоса в него влезает (в SDR - от 24 МГц до 2 ГГц), иначе - по полосе
                feed.lnbMHz = band != null && band.lo > 0 && min - band.lo >= 24.0 && max - band.lo <= 2000.0 ? band.lo
                            : band != null && band.lo == 0 && max <= 2000.0 ? 0.0 : AutoLnb(min, max);
                feeds.Add(feed);
            }
            return feeds;
        }

        private static string BandNames()
        {
            var names = new List<string>();
            foreach (Band b in BANDS) names.Add(b.name);
            return string.Join(", ", names.ToArray());
        }

        private static bool TryReadRange(string text, out double min, out double max)
        {
            min = max = 0;
            int dash = text.IndexOf('-', 1);
            return dash > 0 &&
                   double.TryParse(text.Substring(0, dash), NumberStyles.Float, CultureInfo.InvariantCulture, out min) &&
                   double.TryParse(text.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out max) &&
                   max > min && min > 0;
        }

        // Гетеродин по полосе, если его не задали: выше 2 ГГц (предел SDR) сигнал переносится вниз, к 100 МГц от начала полосы
        private static double AutoLnb(double minMHz, double maxMHz)
        {
            return maxMHz > 2000.0 ? Math.Max(0.0, Math.Floor((minMHz - 100.0) / 100.0) * 100.0) : 0.0;
        }

        // Передатчик командной линии, когда в конфиге о нём ничего нет (старые конфиги): большая тарелка - 20 кВт,
        // малая - 2 кВт, штырь и решётка - 100 Вт
        private static double DefaultUplink(StationAntenna a)
        {
            return a.IsDish ? (a.diameter >= 30 ? 20000.0 : 2000.0) : 100.0;
        }

        // Антенна станции и её облучатели. Приём: receive = S, X (у тарелки по умолчанию - S и X) или одна полоса
        // minFrequency/maxFrequency. Передача: transmit = S 20000, X 20000 (Вт по диапазонам), transmit = 100 (все
        // облучатели антенны) или transmit = нет; не задано нигде - как в старых конфигах, передают все облучатели
        // (мощность uplinkPower или по диаметру)
        private static List<StationAntenna> ReadAntenna(ConfigNode node, ConfigNode stationNode, DsnStation st)
        {
            var a = new StationAntenna { noiseTemperature = ReadDouble(node, "noiseTemperature", st.noiseTemperature), title = (node.GetValue("title") ?? "").Trim() };
            string kind = (node.GetValue("antenna") ?? node.GetValue("kind") ?? "dish").Trim().ToLowerInvariant();
            a.kind = kind == "omni" ? StationAntennaKind.Omni : kind == "yagi" ? StationAntennaKind.Yagi : StationAntennaKind.Dish;
            if (a.kind == StationAntennaKind.Dish)
            {
                double gain;
                if (node.HasValue("diameter")) a.diameter = ReadDouble(node, "diameter", DEFAULT_DIAMETER);
                else if (TryReadDouble(node, "gain", out gain)) a.diameter = RadioMath.DishDiameter(gain, LEGACY_GAIN_FREQUENCY);
                else a.diameter = DEFAULT_DIAMETER;
            }
            else
            {
                a.gainDbi = ReadDouble(node, "gain", a.kind == StationAntennaKind.Omni ? 2.0 : 12.0);
                a.beamwidthDeg = ReadDouble(node, "beamwidth", a.kind == StationAntennaKind.Omni ? 360.0 : 40.0);
            }
            // Рупор тарелки чужие диапазоны не пропускает; штырь и решётка с SDR слышат и соседние частоты, только с потерями
            bool filter;
            a.bandFilter = bool.TryParse(node.GetValue("filter"), out filter) ? filter : a.kind == StationAntennaKind.Dish;

            string where = $"станция «{st.name}»";
            List<StationAntenna> feeds;
            string receive = node.GetValue("receive");
            if (!string.IsNullOrEmpty(receive) || (a.IsDish && !node.HasValue("minFrequency") && !node.HasValue("maxFrequency")))
            {
                feeds = Feeds(a, string.IsNullOrEmpty(receive) ? DEFAULT_RECEIVE : receive, where);
                if (!string.IsNullOrEmpty(a.title))
                {
                    foreach (StationAntenna f in feeds) f.title = a.title + " " + f.title;
                }
            }
            else
            {
                a.minMHz = ReadDouble(node, "minFrequency", a.IsDish ? 2200.0 : 130.0);
                a.maxMHz = ReadDouble(node, "maxFrequency", a.IsDish ? 2300.0 : 470.0);
                if (a.maxMHz <= a.minMHz) a.maxMHz = a.minMHz * 1.1;
                // Конвертер: lnb = гетеродин (МГц), 0 - без него; не задан - по полосе (выше диапазона SDR - вниз)
                a.lnbMHz = node.HasValue("lnb") ? Math.Max(0.0, ReadDouble(node, "lnb", 0.0)) : AutoLnb(a.minMHz, a.maxMHz);
                feeds = new List<StationAntenna> { a };
            }
            if (feeds.Count == 0)
            {
                Debug.LogWarning($"[RealisticRadio] {where}: ни одного диапазона приёма - ставлю {DEFAULT_RECEIVE}");
                feeds = Feeds(a, DEFAULT_RECEIVE, where);
            }

            // Шум по диапазонам: noise = L 28, Ka 27 (К, без неба) - у облучателей Ka и L тракт шумнее, чем у S и X
            string noise = node.GetValue("noise") ?? (node == stationNode ? null : stationNode.GetValue("noise"));
            if (noise != null) ApplyNoise(feeds, noise, where);

            double defaultPower = ReadDouble(node, "uplinkPower", ReadDouble(stationNode, "uplinkPower", 0.0));
            string transmit = node.GetValue("transmit") ?? stationNode.GetValue("transmit");
            if (transmit == null)
            {
                foreach (StationAntenna f in feeds) f.uplinkW = defaultPower > 0 ? defaultPower : DefaultUplink(f);
            }
            else
            {
                ApplyTransmit(feeds, transmit, defaultPower, where);
            }
            return feeds;
        }

        private static void ApplyNoise(List<StationAntenna> feeds, string noise, string where)
        {
            foreach (string raw in noise.Split(','))
            {
                string[] w = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                double kelvin;
                if (w.Length < 2 || !double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out kelvin) || kelvin <= 0)
                {
                    if (raw.Trim().Length > 0) Debug.LogWarning($"[RealisticRadio] {where}: непонятный шум «{raw.Trim()}» (нужно «диапазон К», например «Ka 27»)");
                    continue;
                }
                StationAntenna feed = FeedOf(feeds, w[0]);
                if (feed != null) feed.noiseTemperature = kelvin;
            }
        }

        // Облучатель по имени диапазона; у антенны с названием («решётка УКВ VHF») диапазон - последнее слово
        private static StationAntenna FeedOf(List<StationAntenna> feeds, string bandName)
        {
            Band band = FindBand(bandName);
            string name = band != null ? band.name : bandName;
            foreach (StationAntenna f in feeds)
            {
                string title = f.title.Length > 0 ? f.title.Substring(f.title.LastIndexOf(' ') + 1) : "";
                if (string.Equals(title, name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        // transmit = S 20000, X 20000 | 100 | нет
        private static void ApplyTransmit(List<StationAntenna> feeds, string transmit, double defaultPower, string where)
        {
            foreach (string raw in transmit.Split(','))
            {
                string token = raw.Trim();
                string lower = token.ToLowerInvariant();
                if (token.Length == 0 || lower == "нет" || lower == "none" || lower == "no") continue;
                string[] w = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                double power;
                if (double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out power))
                {
                    foreach (StationAntenna f in feeds) f.uplinkW = power;
                    continue;
                }
                if (w.Length < 2 || !double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out power)) power = defaultPower;
                StationAntenna feed = FeedOf(feeds, w[0]);
                if (feed != null) feed.uplinkW = power > 0 ? power : DefaultUplink(feed);
                else Debug.LogWarning($"[RealisticRadio] {where}: передатчик «{token}» - у станции нет приёма в этом диапазоне, пропущен");
            }
        }

        private static double ReadDouble(ConfigNode node, string name, double fallback)
        {
            double value;
            return TryReadDouble(node, name, out value) ? value : fallback;
        }

        private static bool TryReadDouble(ConfigNode node, string name, out double value)
        {
            return double.TryParse(node.GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
