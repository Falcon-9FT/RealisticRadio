// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace RealisticRadio
{
    // Радиохарактеристики антенны: полоса согласования, тарелка или усиление, шум приёмного тракта,
    // гетеродин конвертера и передатчик линии DSN
    public class AntennaSpec
    {
        public string kind = "";        // для окон: "тарелка X ⌀2.4 м", "штырь УКВ"...
        public double minMHz, maxMHz;
        public double dishDiameter;     // м; 0 - не апертурная антенна (штырь, спираль)
        public double gainDbi;
        public double beamwidthDeg = 360.0;
        public double noiseK;
        public double lnbMHz;
        public double linkPowerDbm;
        public double linkBandwidthMHz;

        public double CenterMHz { get { return 0.5 * (minMHz + maxMHz); } }

        public double GainAt(double frequencyMHz)
        {
            return dishDiameter > 0 ? RadioMath.DishGain(dishDiameter, frequencyMHz) : gainDbi;
        }

        public double BeamwidthAt(double frequencyMHz)
        {
            return dishDiameter > 0 ? 70.0 * RadioMath.Wavelength(frequencyMHz) / dishDiameter : beamwidthDeg;
        }
    }

    // Антенны других модов: в конфиге у них только параметры стокового CommNet - тип (прямая связь, ретранслятор,
    // встроенная) и "мощность" дальности - и название детали. По названию угадывается диапазон (S-Band, VHF...) и вид
    // антенны (штырь, спираль, ФАР, тарелка), по мощности - размер тарелки и передатчик линии DSN.
    // Числа правдоподобные, а не паспортные; явные значения в cfg всегда главнее
    public static class AntennaCatalog
    {
        public const string DIRECT = "DIRECT", RELAY = "RELAY", INTERNAL = "INTERNAL";

        public enum Kind { Unknown, Whip, LowGain, MediumGain, Helical, Yagi, Phased, Dish, Internal }

        private class Band
        {
            public readonly string name;
            public readonly double min, max, lnb, noise, bandwidth;

            public Band(string name, double min, double max, double lnb, double noise, double bandwidth)
            {
                this.name = name;
                this.min = min;
                this.max = max;
                this.lnb = lnb;
                this.noise = noise;
                this.bandwidth = bandwidth;
            }
        }

        // Диапазоны: полоса согласования, гетеродин конвертера (выше 1.7 ГГц RTL-SDR сам не принимает), шум тракта, полоса линии
        private static readonly Band HF = new Band("КВ", 24, 30, 0, 500, 0.003);
        private static readonly Band VHF = new Band("УКВ", 136, 148, 0, 200, 0.025);
        private static readonly Band UHF = new Band("UHF", 400, 450, 0, 150, 0.05);
        private static readonly Band L = new Band("L", 1525, 1710, 0, 120, 0.25);
        private static readonly Band S = new Band("S", 2200, 2300, 2000, 120, 1.0);
        private static readonly Band C = new Band("C", 3700, 4200, 3500, 100, 2.0);
        private static readonly Band X = new Band("X", 8400, 8500, 7800, 80, 2.0);
        private static readonly Band KU = new Band("Ku", 13700, 15100, 13500, 100, 10.0);
        private static readonly Band KA = new Band("Ka", 31800, 32300, 31000, 120, 20.0);

        // hint - имя и название детали, stockPower - "мощность" стокового CommNet, deployable - раскладная
        public static AntennaSpec Classify(string hint, string stockType, double stockPower, bool deployable)
        {
            string text = Normalize(hint);
            double power = stockPower > 0 ? stockPower : 5e5;
            bool builtIn = string.Equals(stockType, INTERNAL, StringComparison.OrdinalIgnoreCase);

            Kind kind = builtIn ? Kind.Internal : KindOf(text);
            if (kind == Kind.Unknown)
            {
                // Названия нет - судим по дальности: большие раскладные - тарелки, маленькие - штыри
                if ((deployable && power >= 1e9) || power >= 1e10) kind = Kind.Dish;
                else if (power <= 1e6) kind = Kind.Whip;
                else if (power <= 1e8) kind = Kind.LowGain;
                else kind = Kind.MediumGain;
            }

            Band band = BandOf(text) ?? DefaultBand(kind, power);
            var spec = new AntennaSpec
            {
                minMHz = band.min,
                maxMHz = band.max,
                noiseK = band.noise,
                lnbMHz = band.lnb,
                linkBandwidthMHz = band.bandwidth
            };

            switch (kind)
            {
                case Kind.Internal:
                    spec.gainDbi = 0.0;
                    spec.linkPowerDbm = 27.0;
                    spec.linkBandwidthMHz = 0.01;
                    spec.noiseK = 300.0;
                    spec.kind = "встроенная " + band.name;
                    break;
                case Kind.Whip:
                    spec.gainDbi = 2.15;
                    spec.linkPowerDbm = band.min < 1000 ? 30.0 : 33.0;
                    spec.linkBandwidthMHz = Math.Min(band.bandwidth, 0.1);
                    spec.kind = "штырь " + band.name;
                    break;
                case Kind.LowGain:
                    spec.gainDbi = 3.0;
                    spec.linkPowerDbm = band.min < 1000 ? 30.0 : 33.0;
                    spec.linkBandwidthMHz = Math.Min(band.bandwidth, 0.1);
                    spec.kind = "всенаправленная " + band.name;
                    break;
                case Kind.Yagi:
                    SetDirectional(spec, 10.0, 37.0, "Уда-Яги " + band.name);
                    break;
                case Kind.Helical:
                    SetDirectional(spec, 12.0, 37.0, "спиральная " + band.name);
                    break;
                case Kind.MediumGain:
                    SetDirectional(spec, 14.0, 37.0, "среднего усиления " + band.name);
                    spec.linkBandwidthMHz = Math.Min(band.bandwidth, 0.5);
                    break;
                case Kind.Phased:
                    spec.dishDiameter = Clamp(0.5 * Math.Pow(power / 2e9, 0.22), 0.2, 8.0);
                    spec.linkPowerDbm = 40.0;
                    spec.kind = $"ФАР {band.name} {spec.dishDiameter:0.#} м";
                    break;
                default:
                    // Тарелка: диаметр растёт с дальностью, но медленно - у стоковых антенн мощность меняется на порядки
                    spec.dishDiameter = Clamp(0.8 * Math.Pow(power / 2e9, 0.22), 0.3, 30.0);
                    spec.linkPowerDbm = power < 1e10 ? 40.0 : power < 1e12 ? 43.0 : power < 1e13 ? 46.0 : 50.0;
                    if (power >= 1e11 && band == X) spec.linkBandwidthMHz = 4.0;
                    spec.kind = $"тарелка {band.name} ⌀{spec.dishDiameter:0.#} м";
                    break;
            }
            return spec;
        }

        private static void SetDirectional(AntennaSpec spec, double gain, double powerDbm, string kind)
        {
            spec.gainDbi = gain;
            spec.beamwidthDeg = Math.Round(Math.Sqrt(41253.0 / Math.Pow(10.0, gain / 10.0)));
            spec.linkPowerDbm = powerDbm;
            spec.kind = kind;
        }

        private static Band DefaultBand(Kind kind, double power)
        {
            switch (kind)
            {
                case Kind.Internal: return S;
                case Kind.Whip: return power <= 1e6 ? VHF : UHF;
                case Kind.LowGain: return power <= 1e6 ? VHF : power <= 5e7 ? UHF : S;
                case Kind.Yagi: return power <= 1e6 ? VHF : UHF;
                case Kind.Helical: return power <= 1e8 ? UHF : S;
                case Kind.MediumGain: return S;
                case Kind.Phased: return power < 1e11 ? S : X;
                default: return power < 1e10 ? S : X;
            }
        }

        private static Band BandOf(string text)
        {
            if (Has(text, " ka band ", " kaband ")) return KA;
            if (Has(text, " ku band ", " kuband ", " ku ")) return KU;
            if (Has(text, " x band ", " xband ")) return X;
            if (Has(text, " c band ", " cband ")) return C;
            if (Has(text, " s band ", " sband ")) return S;
            if (Has(text, " l band ", " lband ")) return L;
            if (Has(text, " uhf ", " дмв ")) return UHF;
            if (Has(text, " vhf ", " укв ")) return VHF;
            if (Has(text, " hf ", " кв ")) return HF;
            return null;
        }

        private static Kind KindOf(string text)
        {
            if (Has(text, " phased ", " фар ", "фазирован")) return Kind.Phased;
            if (Has(text, "dish", " hga ", " high gain ", " highgain ", "parabol", "reflector", "тарелк", " hg ")) return Kind.Dish;
            if (Has(text, "helical", "helix", "spiral", "спирал")) return Kind.Helical;
            if (Has(text, " yagi ")) return Kind.Yagi;
            if (Has(text, " medium gain ", " mga ")) return Kind.MediumGain;
            if (Has(text, " whip ", " stub ", "штыр")) return Kind.Whip;
            if (Has(text, " low gain ", " lga ", "omni", "discone", "biconical", " hoop ", "scimitar", " blade ", "monopole", "dipole",
                    "colinear", "collinear", "turnstile", "всенаправ")) return Kind.LowGain;
            return Kind.Unknown;
        }

        // "bluedog_Nimbus_SBandAntenna Nimbus S-Band Antenna" -> " bluedog nimbus s band antenna nimbus s band antenna "
        private static string Normalize(string hint)
        {
            string split = Regex.Replace(hint ?? "", "(?<=[a-zа-яё0-9])(?=[A-ZА-ЯЁ])", " ");
            var result = new StringBuilder(" ");
            foreach (char c in split.ToLowerInvariant())
            {
                result.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
            result.Append(' ');
            return Regex.Replace(result.ToString(), " +", " ");
        }

        private static bool Has(string text, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (text.IndexOf(key, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
