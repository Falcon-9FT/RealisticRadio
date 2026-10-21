// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using CommNet;
using UnityEngine;

namespace RealisticRadio
{
    // Дальность связи для стокового CommNet. В стоке дальность любой пары - sqrt(P_a * P_b), и одна «мощность» антенны
    // отвечает и за связь с Землёй, и за связь между аппаратами. В жизни это разные вещи: тарелка сети 70 м с охлаждённым
    // приёмником слышит в сотни раз дальше, чем такая же антенна на соседнем аппарате, а вдали аппарат снижает скорость
    // до десятков бит в секунду - «Вояджеры» так и работают со 160 а.е.
    // Поэтому связь с наземной станцией считается по физике мода: «мощность» антенны = (дальность до DSN)² / RELAY_SCALE,
    // и с домом дальность = sqrt(P * RELAY_SCALE) - ровно наша дальность самой медленной линии (на младших уровнях
    // станции слежения - короче, как в стоке). Между аппаратами - стоковое sqrt(P_a * P_b): масштаб подобран так, что
    // пары ретрансляторов S-диапазона дотягиваются примерно на физическую дальность, штыри - не дальше, тарелки X - с запасом
    public class RadioRangeModel : IRangeModel
    {
        public const double RELAY_SCALE = 1.3e15; // м

        private static readonly float[] levels = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly List<double> homes = new List<double>();
        private static readonly List<double> otherHomes = new List<double>();
        private static double strongestHome;
        private static int cachedFrame = -1;
        private static float nextScan;

        public static void Install()
        {
            if (CommNetScenario.RangeModel is RadioRangeModel) return;
            CommNetScenario.RangeModel = new RadioRangeModel();
            Debug.Log("[RealisticRadio] CommNet: связь с наземными станциями - по дальности радиолинии мода");
        }

        public bool InRange(double aPower, double bPower, double sqrDistance)
        {
            double range2 = RangeSquared(aPower, bPower);
            return range2 > 0 && sqrDistance <= range2;
        }

        public double GetNormalizedRange(double aPower, double bPower, double distance)
        {
            double range2 = RangeSquared(aPower, bPower);
            return range2 > 0 ? 1.0 - distance / Math.Sqrt(range2) : -1.0;
        }

        public double GetMaximumRange(double aPower, double bPower)
        {
            return Math.Sqrt(Math.Max(0.0, RangeSquared(aPower, bPower)));
        }

        private static double RangeSquared(double a, double b)
        {
            Refresh();
            if (IsHome(a)) return b * RELAY_SCALE * Share(a);
            if (IsHome(b)) return a * RELAY_SCALE * Share(b);
            return a * b;
        }

        // Наземная станция узнаётся по «мощности»: у постоянных - мощность сети на уровне станции слежения (с поправкой
        // сложности), у остальных - своя из конфига. У аппаратов такой ровно не бывает
        private static bool IsHome(double power)
        {
            if (power <= 0) return false;
            foreach (double home in homes)
            {
                if (Math.Abs(power - home) <= 1e-9 * Math.Max(power, home)) return true;
            }
            return false;
        }

        // Доля от сети высшего уровня: младшие уровни станции слежения слышат хуже
        private static double Share(double homePower)
        {
            return strongestHome > 0 ? homePower / strongestHome : 1.0;
        }

        // Мощности станций - раз за кадр (CommNet спрашивает о дальности тысячи раз), список станций - раз в несколько секунд
        private static void Refresh()
        {
            if (Time.frameCount == cachedFrame) return;
            cachedFrame = Time.frameCount;
            homes.Clear();
            strongestHome = 0;
            GameVariables variables = GameVariables.Instance;
            if (variables != null)
            {
                foreach (float level in levels) AddHome(variables.GetDSNRange(level));
                try
                {
                    AddHome(variables.GetDSNRange(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation)));
                }
                catch (Exception)
                {
                    // Уровней построек ещё нет (загрузка) - хватит стандартных
                }
                strongestHome = variables.GetDSNRange(1f);
            }
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 5f;
                otherHomes.Clear();
                foreach (CommNetHome home in UnityEngine.Object.FindObjectsOfType<CommNetHome>())
                {
                    if (!home.isPermanent && home.antennaPower > 0) otherHomes.Add(home.antennaPower);
                }
            }
            homes.AddRange(otherHomes);
        }

        private static void AddHome(double power)
        {
            if (power > 0 && !homes.Contains(power)) homes.Add(power);
        }
    }
}
