// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RealisticRadio
{
    // Вкладка «Линия связи»: расчёт связи аппарата по шагам - откуда берётся каждый децибел. Вниз (данные) и вверх
    // (команды), по участкам, и все станции: кто слышит и почему не слышат остальные
    public class LinkView
    {
        private static readonly CultureInfo inv = CultureInfo.InvariantCulture;
        private const int SHOWN_STATIONS = 6;

        private Vector2 scroll;
        private int chosen;               // какая линия DSN аппарата (0 - лучшая)
        private bool showStations = true;

        public void Draw(Vessel v)
        {
            if (v == null)
            {
                GUILayout.Label("Нет активного аппарата");
                return;
            }
            List<ModuleRadioLink> links = v.FindPartModulesImplementing<ModuleRadioLink>();
            if (links.Count == 0)
            {
                GUILayout.Label("<color=orange>На аппарате нет антенн мода: связи с Землёй нет</color>");
                return;
            }

            // Какую антенну разбирать: лучшую или выбранную
            if (links.Count > 1)
            {
                var names = new string[links.Count + 1];
                names[0] = "Лучшая";
                for (int i = 0; i < links.Count; i++) names[i + 1] = Short(RadioWindow.PartTitle(links[i].part));
                chosen = GUILayout.SelectionGrid(Mathf.Clamp(chosen, 0, links.Count), names, Math.Min(names.Length, 4));
            }
            ModuleRadioLink link = chosen > 0 && chosen <= links.Count ? links[chosen - 1] : ModuleRadioLink.FindBestRadio(v);
            LinkReport report = link != null ? link.currentLink.report : null;
            if (report == null)
            {
                GUILayout.Label("Связь ещё считается…");
                return;
            }

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(460));
            DrawSummary(link, report);
            GUILayout.Space(6);
            GUILayout.Label("<b>Вниз: данные и телеметрия</b>");
            if (report.down.Count == 0) GUILayout.Label($"<color=orange>Пути нет: {report.why}</color>");
            for (int i = 0; i < report.down.Count; i++) DrawHop(i + 1, report.down[i], false);

            GUILayout.Space(6);
            GUILayout.Label("<b>Вверх: команды с Земли</b>");
            if (report.up.Count == 0) GUILayout.Label("<color=orange>Команды не считались: нет пути вниз</color>");
            for (int i = 0; i < report.up.Count; i++) DrawHop(i + 1, report.up[i], true);

            GUILayout.Space(6);
            showStations = GUILayout.Toggle(showStations, " <b>Станции напрямую</b> (кто слышит эту антенну)");
            if (showStations) DrawStations(report);
            GUILayout.EndScrollView();
        }

        private static void DrawSummary(ModuleRadioLink link, LinkReport report)
        {
            string route = !report.connected ? "<color=orange>пути CommNet нет</color> (ниже - лучшая попытка напрямую)"
                         : report.viaRelay ? $"через ретрансляторы ({report.down.Count} участков)" : "напрямую";
            string down = report.Downlink ? $"<color=#00FF00>{RadioMath.FormatRate(report.RateKbps)}</color>" : "<color=#FF0000>нет</color>";
            string up = report.Uplink ? "<color=#00FF00>доходят</color>" : "<color=#FF0000>не доходят</color>";
            GUILayout.Label($"Антенна: {RadioWindow.PartTitle(link.part)} - {link.frequencyMHz.ToString("0.###", inv)} МГц, {Watts(link.transmitPower)}, " +
                            $"полоса {Bandwidth(link.bandwidthMHz * 1e6)}");
            GUILayout.Label($"Маршрут: {route}; данные вниз: {down}; команды: {up}");
            if (report.connected && report.down.Count > 0 && report.up.Count > 0 && report.up[0].fromGround &&
                report.up[0].from != report.down[report.down.Count - 1].to)
                GUILayout.Label($"Трёхсторонний режим: данные принимает {report.down[report.down.Count - 1].to}, команды посылает {report.up[0].from}");
            if (report.down.Count > 0) GUILayout.Label($"Задержка в одну сторону: {Delay(report.DelaySeconds)}");
            // Масштаб системы: потери на трассе - как на настоящих расстояниях
            if (Math.Abs(RadioScale.Factor - 1.0) > 1e-6)
                GUILayout.Label($"Система: {RadioScale.Name} - потери на трассе как на расстоянии в {RadioScale.Factor.ToString("0.##", inv)} раза больше");
            else
                GUILayout.Label($"Система: {RadioScale.Name} - настоящие расстояния");
            // Управление - по пути CommNet аппарата (любой его антенной)
            Vessel v = link.vessel;
            bool connected = v != null && v.connection != null && v.connection.IsConnected;
            string control = connected ? "<color=#00FF00>есть</color>"
                           : !RadioControlLock.SignalRequired() ? "<color=#FFD700>есть - на этом уровне сложности без связи нельзя только передавать науку</color>"
                           : report.Downlink ? "<color=orange>нет - команды не доходят до борта</color>" : "<color=#FF0000>нет</color>";
            GUILayout.Label($"Управление аппаратом: {control}");
            if (report.why.Length > 0) GUILayout.Label($"<color=orange>{report.why}</color>");
            HopBudget weak = report.Bottleneck;
            if (report.down.Count > 1 && weak != null) GUILayout.Label($"Узкое место: {weak.from} → {weak.to}");
        }

        private static void DrawHop(int n, HopBudget h, bool command)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            string feed = h.feed.Length > 0 ? $" [{h.feed}]" : "";
            GUILayout.Label($"<b>{n}. {h.from} → {h.to}{feed}</b>, {h.frequencyMHz.ToString("0.###", inv)} МГц");
            if (h.blocked.Length > 0) GUILayout.Label($"<color=orange>Участка нет: {h.blocked}</color>");

            // Передатчик
            string pointing = h.txPointingLossDb > 0.05 ? $" − наведение {Db(h.txPointingLossDb)} ({h.txOffAxisDeg:0.#}° от оси)" : "";
            string matching = h.txMatchingLossDb > 0.05 ? $" − согласование {Db(h.txMatchingLossDb)}" : "";
            GUILayout.Label($"Передатчик {Dbm(h.txPowerDbm)} ({Watts(h.txPowerDbm)}){matching} + антенна {Dbi(h.txGainDbi)}{pointing} = ЭИИМ {Dbm(h.EirpDbm)}");

            // Трасса
            string extra = "";
            if (h.horizonLossDb > 0.05) extra += $"; горизонт −{Db(h.horizonLossDb)}";
            if (h.atmosphereLossDb > 0.005) extra += $"; атмосфера −{h.atmosphereLossDb.ToString("0.00", inv)} дБ";
            if (h.solarLossDb > 0.05) extra += $"; солнечная корона −{Db(h.solarLossDb)}";
            string elevation = double.IsNaN(h.elevationDeg) ? "" : $"; угол места {h.elevationDeg:0.#}°";
            GUILayout.Label($"Трасса {KSPUtil.PrintSI(h.distanceM, "м")} ({Delay(h.DelaySeconds)}): в пространстве −{Db(h.pathLossDb)}{extra}{elevation}");

            // Приёмник
            string rxLoss = h.rxFeedLossDb > 0.05 ? $" − {(h.toGround ? "облучатель" : "согласование и фильтр")} {Db(h.rxFeedLossDb)}" : "";
            string rxPoint = h.rxPointingLossDb > 0.05 ? $" − наведение {Db(h.rxPointingLossDb)} ({h.rxOffAxisDeg:0.#}°)" : "";
            GUILayout.Label($"Приём: антенна {Dbi(h.rxGainDbi)}{rxLoss}{rxPoint} → G/T {h.GOverT:0.0} дБ/К");
            // Шум приёмника по источникам (если приёмник считал его ядром)
            if (h.noiseOwnK > 0)
            {
                string bodies = h.noiseBodiesK >= 0.5 ? $" + тела в луче {h.noiseBodiesK:0}" + (h.noiseBodies.Length > 0 ? $" ({h.noiseBodies})" : "") : "";
                string atmosphere = h.noiseAtmosphereK >= 0.5 ? $" + атмосфера {h.noiseAtmosphereK:0}" : "";
                GUILayout.Label($"Шум: тракт {h.noiseOwnK:0} + небо {h.noiseSkyK:0}{bodies}{atmosphere} = {h.noiseK:0} К");
            }
            else
            {
                GUILayout.Label($"Шум {h.noiseK:0} К");
            }

            if (h.blocked.Length == 0)
            {
                // Итог
                double cn0 = h.Cn0;
                string margin = MarginText(h.Margin);
                GUILayout.Label($"Сигнал {Dbm(h.RxPowerDbm)}; C/N0 = <b>{cn0:0.0} дБ·Гц</b>; для 10 бит/с нужно {RadioMath.MIN_LINK_CN0:0.0} → {margin}");
                string what = command ? "команды" : "данные";
                GUILayout.Label(h.Usable
                    ? $"Полоса {Bandwidth(h.bandwidthHz)}: {h.mode} → {what} {RadioMath.FormatRate(h.rateKbps)}"
                    : $"<color=orange>Сигнал слабее самой медленной линии (10 бит/с)</color>");
            }
            GUILayout.EndVertical();
        }

        // Станции по слышимости и сводка, почему остальные не слышат
        private static void DrawStations(LinkReport report)
        {
            var heard = new List<HopBudget>();
            var reasons = new Dictionary<string, int>();
            foreach (HopBudget h in report.stations)
            {
                if (h.blocked.Length == 0)
                {
                    heard.Add(h);
                    continue;
                }
                string key = h.blocked.StartsWith("станция не слышит") ? "не слышат эту частоту" : h.blocked;
                int count;
                reasons[key] = reasons.TryGetValue(key, out count) ? count + 1 : 1;
            }
            heard.Sort((a, b) => b.Cn0.CompareTo(a.Cn0));
            if (heard.Count == 0) GUILayout.Label("<color=orange>Ни одна станция не принимает эту антенну</color>");
            for (int i = 0; i < heard.Count && i < SHOWN_STATIONS; i++)
            {
                HopBudget h = heard[i];
                string rate = h.Usable ? RadioMath.FormatRate(h.rateKbps) : "<color=orange>слабее 10 бит/с</color>";
                GUILayout.Label($"• {h.to} [{h.feed}]: угол места {h.elevationDeg:0}°, C/N0 {h.Cn0:0.0} дБ·Гц, {MarginText(h.Margin)}, {rate}");
            }
            if (heard.Count > SHOWN_STATIONS) GUILayout.Label($"  …и ещё {heard.Count - SHOWN_STATIONS} в прямой видимости");
            foreach (KeyValuePair<string, int> reason in reasons) GUILayout.Label($"  {reason.Key}: {reason.Value}");
        }

        private static string MarginText(double margin)
        {
            if (double.IsInfinity(margin) || double.IsNaN(margin)) return "сигнала нет";
            string color = margin >= 6 ? "#00FF00" : margin >= 0 ? "#FFD700" : "#FF6060";
            return $"запас <color={color}>{margin:+0.0;-0.0} дБ</color>";
        }

        private static string Db(double db) { return db.ToString("0.0", inv) + " дБ"; }
        private static string Dbi(double db) { return db.ToString("0.0", inv) + " dBi"; }
        private static string Dbm(double dbm) { return dbm.ToString("0.0", inv) + " dBm"; }

        private static string Watts(double dbm)
        {
            double w = Math.Pow(10.0, (dbm - 30.0) / 10.0);
            return w >= 1000 ? (w / 1000).ToString("0.#", inv) + " кВт" : w >= 1 ? w.ToString("0.#", inv) + " Вт" : (w * 1000).ToString("0", inv) + " мВт";
        }

        private static string Bandwidth(double hz)
        {
            return hz >= 1e6 ? (hz / 1e6).ToString("0.##", inv) + " МГц" : (hz / 1e3).ToString("0.#", inv) + " кГц";
        }

        public static string Delay(double seconds)
        {
            if (seconds < 1.0) return (seconds * 1000).ToString("0.#", inv) + " мс";
            if (seconds < 120.0) return seconds.ToString("0.0", inv) + " с";
            int minutes = (int)(seconds / 60);
            return seconds < 7200.0 ? $"{minutes} мин {(int)(seconds - minutes * 60)} с" : $"{minutes / 60} ч {minutes % 60} мин";
        }

        private static string Short(string name)
        {
            return name != null && name.Length > 20 ? name.Substring(0, 19) + "…" : name;
        }
    }
}
