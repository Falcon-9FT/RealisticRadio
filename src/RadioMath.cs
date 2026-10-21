// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;

namespace RealisticRadio
{
    // Радиофизика без зависимостей от игры: потери трассы, усиление тарелок, шум, наведение, скорость
    public static class RadioMath
    {
        public const double SPEED_OF_LIGHT = 299792458.0;
        public const double BOLTZMANN = 1.380649e-23;
        public const double DISH_EFFICIENCY = 0.55;
        public const double IMPLEMENTATION_MARGIN = 1.0; // дБ: реальный модем хуже идеального
        private const double CMB_KELVIN = 2.7;

        // Режимы модуляции и кодирования (как в DVB-S2 и CCSDS для дальнего космоса): модем выбирает самый
        // быстрый режим, который выдерживает текущий SNR. Эффективность - бит/с на герц полосы, порог - Es/N0, дБ
        private struct ModCod
        {
            public readonly string name;
            public readonly double efficiency;
            public readonly double requiredSnr;

            public ModCod(string name, double efficiency, double requiredSnr)
            {
                this.name = name;
                this.efficiency = efficiency;
                this.requiredSnr = requiredSnr;
            }
        }

        private static readonly ModCod[] modCods =
        {
            new ModCod("BPSK turbo 1/6", 0.167, -7.0),
            new ModCod("BPSK turbo 1/3", 0.333, -4.0),
            new ModCod("QPSK 1/4", 0.490, -2.35),
            new ModCod("QPSK 1/2", 0.988, 1.00),
            new ModCod("QPSK 3/4", 1.487, 4.03),
            new ModCod("8PSK 2/3", 1.980, 6.62),
            new ModCod("8PSK 5/6", 2.479, 9.35),
            new ModCod("16APSK 3/4", 2.967, 10.21),
            new ModCod("16APSK 8/9", 3.523, 12.89),
            new ModCod("32APSK 4/5", 3.952, 13.64),
            new ModCod("32APSK 9/10", 4.453, 16.05)
        };

        // Порог самого медленного режима (Es/N0 в полосе, где идёт передача)
        public static readonly double LINK_THRESHOLD_SNR = modCods[0].requiredSnr + IMPLEMENTATION_MARGIN;

        // Самая медленная линия: ниже связи нет. Вдали аппарат снижает скорость, как настоящие АМС: «Пионер-10» в конце
        // работал на 16 бит/с с 80 а.е., «Вояджер-1» - на 160 бит/с со 160 а.е.
        public const double MIN_DATA_RATE_BPS = 10.0;
        public const double USEFUL_DATA_RATE_BPS = 1000.0; // «рабочая» связь: научные данные уходят за разумное время

        // Порог C/N0 для скорости: самый медленный режим на этой скорости, дБ·Гц
        public static double Cn0ForRate(double bitsPerSecond)
        {
            return LINK_THRESHOLD_SNR + 10.0 * Math.Log10(bitsPerSecond / modCods[0].efficiency);
        }

        // Связи нет ниже этого C/N0 (~12 дБ·Гц - как у слабейших линий дальнего космоса)
        public static readonly double MIN_LINK_CN0 = Cn0ForRate(MIN_DATA_RATE_BPS);

        // Скорость линии по C/N0, кбит/с. Пока сигнала хватает на всю полосу канала - самый быстрый режим, который она
        // выдерживает; меньше - аппарат снижает символьную скорость (полоса сужается) и держит самый медленный режим
        // на пороге, пока скорость не упадёт до MIN_DATA_RATE_BPS. mode - режим или пусто, если связи нет
        public static double LinkRateKbps(double cn0DbHz, double channelBandwidthHz, out string mode)
        {
            double bandwidth = Math.Max(channelBandwidthHz, 1.0);
            double rate = DataRateKbps(bandwidth, cn0DbHz - 10.0 * Math.Log10(bandwidth), out mode);
            if (rate > 0.0) return rate;
            double bps = Math.Pow(10.0, (cn0DbHz - LINK_THRESHOLD_SNR) / 10.0) * modCods[0].efficiency;
            if (bps < MIN_DATA_RATE_BPS)
            {
                mode = "";
                return 0.0;
            }
            mode = modCods[0].name + ", скорость снижена";
            return bps / 1000.0;
        }

        // Скорость для людей: 1.2 Мбит/с, 34 кбит/с, 160 бит/с
        public static string FormatRate(double kbps)
        {
            if (kbps <= 0.0) return "нет связи";
            if (kbps >= 1000.0) return (kbps / 1000.0).ToString(kbps >= 10000.0 ? "0" : "0.#") + " Мбит/с";
            if (kbps >= 1.0) return kbps.ToString(kbps >= 10.0 ? "0" : "0.#") + " кбит/с";
            return (kbps * 1000.0).ToString("0") + " бит/с";
        }

        // Скорость передачи в выбранном режиме, кбит/с; mode - название режима или пусто, если связи нет
        public static double DataRateKbps(double bandwidthHz, double snrDb, out string mode)
        {
            mode = "";
            double rate = 0.0;
            foreach (ModCod modCod in modCods)
            {
                if (snrDb < modCod.requiredSnr + IMPLEMENTATION_MARGIN) break;
                mode = modCod.name;
                rate = bandwidthHz * modCod.efficiency / 1000.0;
            }
            return rate;
        }

        // Потери в свободном пространстве, дБ
        public static double FreeSpaceLoss(double distanceKm, double frequencyMHz)
        {
            return 20.0 * Math.Log10(Math.Max(distanceKm, 0.001)) + 20.0 * Math.Log10(frequencyMHz) + 32.44;
        }

        // Дальность, на которой потери трассы достигают заданной величины, м
        public static double DistanceForLoss(double pathLossDb, double frequencyMHz)
        {
            return Math.Pow(10.0, (pathLossDb - 20.0 * Math.Log10(frequencyMHz) - 32.44) / 20.0) * 1000.0;
        }

        // Усиление параболической антенны: G = η·(π·D/λ)² - одна и та же тарелка на СВЧ гораздо сильнее, чем на УКВ
        public static double DishGain(double diameterM, double frequencyMHz)
        {
            double x = Math.PI * diameterM / Wavelength(frequencyMHz);
            return 10.0 * Math.Log10(DISH_EFFICIENCY * x * x);
        }

        // Обратная задача: диаметр тарелки по усилению на заданной частоте
        public static double DishDiameter(double gainDbi, double frequencyMHz)
        {
            return Math.Sqrt(Math.Pow(10.0, gainDbi / 10.0) / DISH_EFFICIENCY) * Wavelength(frequencyMHz) / Math.PI;
        }

        // Шумовая температура неба: галактический шум ~ λ^2.55 (на УКВ сотни кельвинов, на СВЧ почти ноль) + реликтовый фон
        public static double SkyNoise(double frequencyMHz)
        {
            return 60.0 * Math.Pow(Wavelength(frequencyMHz), 2.55) + CMB_KELVIN;
        }

        // Яркостная температура спокойного Солнца по частоте, К (DSN 810-005, модуль 105: T = 5672·λ^0.24517, λ в мм):
        // ~19 000 К в S-диапазоне, ~13 600 К в X, ~10 000 К в Ka. Активное Солнце - в 2-3 раза ярче
        public static double SunBrightnessK(double frequencyMHz)
        {
            return 5672.0 * Math.Pow(Wavelength(frequencyMHz) * 1000.0, 0.24517);
        }

        // Какую долю шума тела (его яркостной температуры) антенна собирает: тело с угловым радиусом radiusDeg в angleDeg
        // от оси луча. Тело шире луча и в его центре - вся температура; меньше луча - доля его площади в луче; в стороне -
        // ослабленная диаграммой (боковые и задний лепестки). Всенаправленная антенна видит всю сферу поровну: доля - телесный
        // угол тела от полной сферы
        public static double BodyCoverage(double angleDeg, double radiusDeg, double beamwidthDeg, double gainDbi)
        {
            if (radiusDeg <= 0) return 0.0;
            if (beamwidthDeg >= 180.0) return (1.0 - Math.Cos(radiusDeg * DEG2RAD)) / 2.0;
            double half = beamwidthDeg / 2.0;
            if (angleDeg + half <= radiusDeg) return 1.0;                   // тело закрывает весь луч
            double area = Math.Min(1.0, (radiusDeg * radiusDeg) / (half * half));
            double edge = Math.Max(0.0, angleDeg - radiusDeg);              // ближний к оси край тела
            return area * Math.Pow(10.0, -PointingLoss(edge, beamwidthDeg, gainDbi) / 10.0);
        }

        // Затухание в атмосфере в зенит для ясной погоды, дБ, у поверхности с земным давлением: на S почти ноль, к K-диапазону
        // (линия водяного пара у 22 ГГц) растёт. pressureRatio - давление у поверхности к земному
        public static double AtmosphereZenithLossDb(double frequencyMHz, double pressureRatio)
        {
            double f = frequencyMHz / 1000.0;
            double earth = f < 3.0 ? 0.035 : f < 10.0 ? 0.045 : f < 18.0 ? 0.07 : f < 28.0 ? 0.20 : f < 40.0 ? 0.13 : 0.3;
            return earth * Math.Max(pressureRatio, 0.0);
        }

        // Сколько толщ атмосферы проходит луч под углом места elevationDeg (формула Кастена-Янга: у горизонта ~38, не бесконечность)
        public static double AirMass(double elevationDeg)
        {
            double e = Math.Max(elevationDeg, 0.0);
            return 1.0 / (Math.Sin(e * DEG2RAD) + 0.50572 * Math.Pow(e + 6.07995, -1.6364));
        }

        // Шум атмосферы на луче с затуханием lossDb, К: атмосфера и поглощает сигнал, и излучает (средняя температура ~275 К)
        public static double AtmosphereNoiseK(double lossDb)
        {
            return ATMOSPHERE_MEAN_K * (1.0 - Math.Pow(10.0, -lossDb / 10.0));
        }

        private const double ATMOSPHERE_MEAN_K = 275.0;

        // Ширина луча антенны по усилению, градусы (θ ≈ √(41253/G)), когда диаметр неизвестен
        public static double BeamFromGain(double gainDbi)
        {
            return gainDbi <= 3.0 ? 360.0 : Math.Min(360.0, Math.Sqrt(41253.0 / Math.Pow(10.0, gainDbi / 10.0)));
        }

        // Мощность шума kTB, dBm
        public static double NoiseDbm(double temperatureK, double bandwidthHz)
        {
            return 10.0 * Math.Log10(BOLTZMANN * temperatureK * bandwidthHz) + 30.0;
        }

        // Плотность шума kT, dBm/Гц
        public static double NoiseDensityDbm(double temperatureK)
        {
            return NoiseDbm(temperatureK, 1.0);
        }

        // C/N0 линии из двух участков через ретранслятор: шумы участков складываются (1/C/N0 = 1/a + 1/b), итог хуже худшего
        public static double CombineCn0(double a, double b)
        {
            if (double.IsNaN(a)) return b;
            if (double.IsNaN(b)) return a;
            return -10.0 * Math.Log10(Math.Pow(10.0, -a / 10.0) + Math.Pow(10.0, -b / 10.0));
        }

        // Потери наведения по эталонной диаграмме ITU-R F.699: главный лепесток G - 2.5·10⁻³·(D/λ·θ)² (3 дБ на краю
        // луча), дальше плато первого бокового лепестка G1 = 2 + 15·lg(D/λ), огибающая боковых 32 - 25·lg θ (у небольших
        // антенн 52 - 10·lg(D/λ) - 25·lg θ) и задний уровень: -10 dBi у больших тарелок, у малых - не ближе 15 дБ к максимуму.
        // D/λ - по ширине луча (θ3 ≈ 70·λ/D). Так 70-метровая тарелка, наведённая на один аппарат, давит второй в 10° от оси
        // на ~65 дБ, а штырь и всенаправленные антенны принимают одинаково со всех сторон
        public static double PointingLoss(double errorDeg, double beamwidthDeg, double gainDbi)
        {
            if (beamwidthDeg >= 180.0) return 0.0;
            double theta = Math.Max(0.0, Math.Min(errorDeg, 180.0));
            double d = 70.0 / Math.Max(beamwidthDeg, 1e-4);
            double gMax = gainDbi;
            double g1 = 2.0 + 15.0 * Math.Log10(d);
            bool large = d > 100.0;
            double back = large ? -10.0 : Math.Min(10.0 - 10.0 * Math.Log10(d), gMax - 15.0);
            double main = gMax - 2.5e-3 * (d * theta) * (d * theta);
            double gain;
            if (gMax <= g1)
            {
                // Антенна слабее эталона - только главный лепесток до заднего уровня
                gain = Math.Max(main, back);
            }
            else if (theta < 20.0 / d * Math.Sqrt(gMax - g1))
            {
                gain = main;
            }
            else
            {
                // У малых антенн огибающая спадает и за 48°, пока не упрётся в задний уровень, - без ступеньки
                double thetaR = large ? 15.85 * Math.Pow(d, -0.6) : 100.0 / d;
                double envelope = theta < thetaR ? g1
                                : large ? (theta < 48.0 ? 32.0 - 25.0 * Math.Log10(theta) : -10.0)
                                : 52.0 - 10.0 * Math.Log10(d) - 25.0 * Math.Log10(theta);
                gain = Math.Max(Math.Min(envelope, g1), back);
            }
            return Math.Max(0.0, gMax - gain);
        }

        public static double Wavelength(double frequencyMHz)
        {
            return SPEED_OF_LIGHT / (frequencyMHz * 1e6);
        }

        // Согласование антенны с фидером 50 Ом: потери (дБ, на приём и на передачу одинаковые) и КСВ на частоте.
        // Антенна - резонансный контур, согласованный в полосе [low, high] (КСВ 2 на краях); дальше растёт реактивное
        // сопротивление, и всё больше мощности отражается. Штырь резонирует ещё на 3/4, 5/4, 7/4 волны - там принимает
        // почти как на своей частоте, но диаграмма дробится. Облучатель тарелки широкополосный, зато ниже критической
        // частоты своего волновода волну не пропускает. Совсем глухих антенн не бывает: что-то наводится на фидер и корпус
        public static double MatchingLoss(double frequencyMHz, double lowMHz, double highMHz, bool dishFeed, out double vswr)
        {
            double f0 = Math.Sqrt(lowMHz * highMHz);
            double bandwidth = Math.Max((highMHz - lowMHz) / f0, 1e-4);
            double q = 1.0 / (bandwidth * Math.Sqrt(2.0));
            if (dishFeed) q = Math.Min(q, 3.5); // рупор: полоса не уже примерно ±20%

            double bestLoss = double.MaxValue, bestGamma2 = 1.0;
            int lastMode = dishFeed ? 1 : 7;
            for (int n = 1; n <= lastMode; n += 2)
            {
                double fn = n * f0;
                double x = frequencyMHz / fn - fn / frequencyMHz;
                double a = q * x;
                double gamma2 = a * a / (4.0 + a * a);
                // На высших резонансах сопротивление больше 50 Ом (у 3/4 волны ~100 Ом, |Г| = 1/3) и диаграмма дробится
                if (n > 1) gamma2 = 1.0 - (1.0 - gamma2) * (1.0 - 1.0 / 9.0);
                double loss = -10.0 * Math.Log10(Math.Max(1.0 - gamma2, 1e-12)) + 0.8 * (n - 1);
                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    bestGamma2 = gamma2;
                }
            }

            if (dishFeed)
            {
                double cutoff = 0.62 * lowMHz;
                if (frequencyMHz < cutoff) bestLoss += Math.Min(60.0, 40.0 * Math.Log(cutoff / frequencyMHz, 2.0));
            }

            double gamma = Math.Sqrt(Math.Min(bestGamma2, 0.999999));
            vswr = (1.0 + gamma) / (1.0 - gamma);
            return Math.Min(bestLoss, 45.0);
        }

        // Входной фильтр конвертера (LNB): свою полосу пропускает, соседние частоты давит
        public static double LnbFilterLoss(double frequencyMHz, double lowMHz, double highMHz)
        {
            double edge = frequencyMHz < lowMHz ? lowMHz : frequencyMHz > highMHz ? highMHz : frequencyMHz;
            return Math.Min(50.0, 300.0 * Math.Abs(frequencyMHz - edge) / edge);
        }

        // Входной фильтр приёмника (облучатель станции, приёмник ретранслятора): полоса проходит, чужая частота гаснет круто -
        // 1% за краем полосы стоит 10 дБ, чужой диапазон не слышен совсем
        public static double BandFilterLoss(double frequencyMHz, double lowMHz, double highMHz)
        {
            double edge = frequencyMHz < lowMHz ? lowMHz : frequencyMHz > highMHz ? highMHz : frequencyMHz;
            return Math.Min(120.0, 1000.0 * Math.Abs(frequencyMHz - edge) / edge);
        }

        // Защита передатчика: при КСВ выше 3 он сам снижает мощность, чтобы отражённая волна не сожгла выходной каскад
        public static double TransmitterFoldback(double vswr)
        {
            return vswr > 3.0 ? (3.0 / vswr) * (3.0 / vswr) : 1.0;
        }

        private const double DEG2RAD = Math.PI / 180.0;
        private const double ROUNDED_EXCESS_DB = 8.0; // доп. потери за скруглённой выпуклостью планеты на единицу ν

        // Дифракция на препятствии (ITU-R P.526): marginDeg - насколько луч выше края препятствия, ν > 0 - край
        // перекрывает луч. Выпуклость планеты - скруглённое препятствие, за ним сигнал гаснет быстрее, чем за острым гребнем
        public static double DiffractionLoss(double marginDeg, double obstacleDistance, double wavelength, bool rounded)
        {
            double v = -marginDeg * DEG2RAD * Math.Sqrt(2.0 * Math.Max(obstacleDistance, 1.0) / wavelength);
            return DiffractionLossNu(v, rounded);
        }

        // То же по параметру Френеля-Кирхгофа ν (ν > 0 - препятствие перекрывает прямую видимость)
        public static double DiffractionLossNu(double v, bool rounded)
        {
            if (v <= -0.78) return 0.0;
            double loss = 6.9 + 20.0 * Math.Log10(Math.Sqrt((v - 0.1) * (v - 0.1) + 1.0) + v - 0.1);
            if (rounded && v > 0) loss += ROUNDED_EXCESS_DB * v;
            return loss;
        }

        // Препятствие высотой h над прямой между концами (d1, d2 - расстояния от него до концов, м): ν = h·√(2/λ·(1/d1 + 1/d2))
        public static double FresnelNu(double heightM, double d1, double d2, double wavelength)
        {
            return heightM * Math.Sqrt(2.0 / wavelength * (1.0 / Math.Max(d1, 1.0) + 1.0 / Math.Max(d2, 1.0)));
        }

        // Потери на горизонте гладкого шара для антенны на высоте heightM: с высоты видно чуть ниже нуля градусов,
        // у самого горизонта сигнал не обрывается, а тонет в шуме за несколько градусов
        public static double SphericalHorizonLoss(double elevationDeg, double bodyRadius, double heightM, double wavelength)
        {
            double height = Math.Max(heightM, 0.1);
            double horizonDeg = -Math.Acos(bodyRadius / (bodyRadius + height)) / DEG2RAD;
            double horizonDistance = Math.Sqrt(2.0 * bodyRadius * height);
            return DiffractionLoss(elevationDeg - horizonDeg, horizonDistance, wavelength, true);
        }
    }
}
