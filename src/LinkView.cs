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
                GUILayout.Label(Loc.T("#rr_no_vessel"));
                return;
            }
            List<ModuleRadioLink> links = v.FindPartModulesImplementing<ModuleRadioLink>();
            if (links.Count == 0)
            {
                GUILayout.Label(Loc.T("#rr_no_mod_antennas"));
                return;
            }

            // Какую антенну разбирать: лучшую или выбранную
            if (links.Count > 1)
            {
                var names = new string[links.Count + 1];
                names[0] = Loc.T("#rr_lv_best");
                for (int i = 0; i < links.Count; i++) names[i + 1] = Short(RadioWindow.PartTitle(links[i].part));
                chosen = GUILayout.SelectionGrid(Mathf.Clamp(chosen, 0, links.Count), names, Math.Min(names.Length, 4));
            }
            ModuleRadioLink link = chosen > 0 && chosen <= links.Count ? links[chosen - 1] : ModuleRadioLink.FindBestRadio(v);
            LinkReport report = link != null ? link.currentLink.report : null;
            if (report == null)
            {
                GUILayout.Label(Loc.T("#rr_lv_computing"));
                return;
            }

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(460));
            DrawSummary(link, report);
            GUILayout.Space(6);
            GUILayout.Label(Loc.T("#rr_lv_down"));
            if (report.down.Count == 0) GUILayout.Label(Loc.T("#rr_lv_no_path", report.why));
            for (int i = 0; i < report.down.Count; i++) DrawHop(i + 1, report.down[i], false);

            GUILayout.Space(6);
            GUILayout.Label(Loc.T("#rr_lv_up"));
            if (report.up.Count == 0) GUILayout.Label(Loc.T("#rr_lv_up_none"));
            for (int i = 0; i < report.up.Count; i++) DrawHop(i + 1, report.up[i], true);

            GUILayout.Space(6);
            showStations = GUILayout.Toggle(showStations, " " + Loc.T("#rr_lv_direct"));
            if (showStations) DrawStations(report);
            GUILayout.EndScrollView();
        }

        private static void DrawSummary(ModuleRadioLink link, LinkReport report)
        {
            string route = !report.connected ? Loc.T("#rr_lv_route_none")
                         : report.viaRelay ? Loc.T("#rr_route_relays", report.down.Count) : Loc.T("#rr_route_direct");
            string down = report.Downlink ? $"<color=#00FF00>{RadioMath.FormatRate(report.RateKbps)}</color>" : Loc.T("#rr_no_red");
            string up = report.Uplink ? Loc.T("#rr_arrive_green") : Loc.T("#rr_arrive_red");
            GUILayout.Label(Loc.T("#rr_lv_antenna", RadioWindow.PartTitle(link.part), link.frequencyMHz.ToString("0.###", inv), Watts(link.transmitPower), Bandwidth(link.bandwidthMHz * 1e6)));
            GUILayout.Label(Loc.T("#rr_lv_route", route, down, up));
            if (report.connected && report.down.Count > 0 && report.up.Count > 0 && report.up[0].fromGround &&
                report.up[0].from != report.down[report.down.Count - 1].to)
                GUILayout.Label(Loc.T("#rr_lv_threeway", DsnManager.TitleOf(report.down[report.down.Count - 1].to), DsnManager.TitleOf(report.up[0].from)));
            if (report.down.Count > 0) GUILayout.Label(Loc.T("#rr_lv_delay", Delay(report.DelaySeconds)));
            // Масштаб системы: потери на трассе - как на настоящих расстояниях
            if (Math.Abs(RadioScale.Factor - 1.0) > 1e-6)
                GUILayout.Label(Loc.T("#rr_lv_scale", RadioScale.Name, RadioScale.Factor.ToString("0.##", inv)));
            else
                GUILayout.Label(Loc.T("#rr_lv_scale_real", RadioScale.Name));
            // Управление - по пути CommNet аппарата (любой его антенной)
            Vessel v = link.vessel;
            bool connected = v != null && v.connection != null && v.connection.IsConnected;
            string control = connected ? Loc.T("#rr_yes_green")
                           : !RadioControlLock.SignalRequired() ? Loc.T("#rr_lv_control_free")
                           : report.Downlink ? Loc.T("#rr_lv_control_no_cmd") : Loc.T("#rr_no_red");
            GUILayout.Label(Loc.T("#rr_lv_control", control));
            if (report.why.Length > 0) GUILayout.Label($"<color=orange>{report.why}</color>");
            HopBudget weak = report.Bottleneck;
            if (report.down.Count > 1 && weak != null) GUILayout.Label(Loc.T("#rr_lv_bottleneck", DsnManager.TitleOf(weak.from), DsnManager.TitleOf(weak.to)));
        }

        private static void DrawHop(int n, HopBudget h, bool command)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            string feed = h.feed.Length > 0 ? $" [{h.feed}]" : "";
            GUILayout.Label(Loc.T("#rr_lv_hop_title", n, DsnManager.TitleOf(h.from), DsnManager.TitleOf(h.to), feed, h.frequencyMHz.ToString("0.###", inv)));
            if (h.blocked.Length > 0) GUILayout.Label(Loc.T("#rr_lv_hop_blocked", h.blocked));

            // Передатчик
            string pointing = h.txPointingLossDb > 0.05 ? " − " + Loc.T("#rr_lv_point_tx", Db(h.txPointingLossDb), $"{h.txOffAxisDeg:0.#}") : "";
            string matching = h.txMatchingLossDb > 0.05 ? " − " + Loc.T("#rr_lv_match", Db(h.txMatchingLossDb)) : "";
            GUILayout.Label(Loc.T("#rr_lv_tx", Dbm(h.txPowerDbm), Watts(h.txPowerDbm), matching, Dbi(h.txGainDbi), pointing, Dbm(h.EirpDbm)));

            // Трасса
            string extra = "";
            if (h.horizonLossDb > 0.05) extra += "; " + Loc.T("#rr_lv_horizon", Db(h.horizonLossDb));
            if (h.atmosphereLossDb > 0.005) extra += "; " + Loc.T("#rr_lv_atm", h.atmosphereLossDb.ToString("0.00", inv));
            if (h.solarLossDb > 0.05) extra += "; " + Loc.T("#rr_lv_corona", Db(h.solarLossDb));
            string elevation = double.IsNaN(h.elevationDeg) ? "" : "; " + Loc.T("#rr_lv_elevation", $"{h.elevationDeg:0.#}");
            GUILayout.Label(Loc.T("#rr_lv_path", KSPUtil.PrintSI(h.distanceM, Loc.T("#rr_unit_m")), Delay(h.DelaySeconds), Db(h.pathLossDb), extra, elevation));

            // Приёмник
            string rxLoss = h.rxFeedLossDb > 0.05 ? " − " + Loc.T(h.toGround ? "#rr_lv_feed" : "#rr_lv_match_filter", Db(h.rxFeedLossDb)) : "";
            string rxPoint = h.rxPointingLossDb > 0.05 ? " − " + Loc.T("#rr_lv_point_rx", Db(h.rxPointingLossDb), $"{h.rxOffAxisDeg:0.#}") : "";
            GUILayout.Label(Loc.T("#rr_lv_rx", Dbi(h.rxGainDbi), rxLoss, rxPoint, $"{h.GOverT:0.0}"));
            // Шум приёмника по источникам (если приёмник считал его ядром)
            if (h.noiseOwnK > 0)
            {
                string bodies = h.noiseBodiesK >= 0.5 ? " + " + Loc.T("#rr_lv_noise_bodies", $"{h.noiseBodiesK:0}") + (h.noiseBodies.Length > 0 ? $" ({h.noiseBodies})" : "") : "";
                string atmosphere = h.noiseAtmosphereK >= 0.5 ? " + " + Loc.T("#rr_lv_noise_atm", $"{h.noiseAtmosphereK:0}") : "";
                GUILayout.Label(Loc.T("#rr_lv_noise", $"{h.noiseOwnK:0}", $"{h.noiseSkyK:0}", bodies, atmosphere, $"{h.noiseK:0}"));
            }
            else
            {
                GUILayout.Label(Loc.T("#rr_lv_noise_plain", $"{h.noiseK:0}"));
            }

            if (h.blocked.Length == 0)
            {
                // Итог
                double cn0 = h.Cn0;
                string margin = MarginText(h.Margin);
                GUILayout.Label(Loc.T("#rr_lv_signal", Dbm(h.RxPowerDbm), $"{cn0:0.0}", $"{RadioMath.MIN_LINK_CN0:0.0}", margin));
                string what = Loc.T(command ? "#rr_commands" : "#rr_data");
                GUILayout.Label(h.Usable
                    ? Loc.T("#rr_lv_band_rate", Bandwidth(h.bandwidthHz), h.mode, what, RadioMath.FormatRate(h.rateKbps))
                    : Loc.T("#rr_lv_weak"));
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
                string key = h.stationDeaf ? Loc.T("#rr_lv_deaf_group") : h.blocked;
                int count;
                reasons[key] = reasons.TryGetValue(key, out count) ? count + 1 : 1;
            }
            heard.Sort((a, b) => b.Cn0.CompareTo(a.Cn0));
            if (heard.Count == 0) GUILayout.Label(Loc.T("#rr_lv_nobody"));
            for (int i = 0; i < heard.Count && i < SHOWN_STATIONS; i++)
            {
                HopBudget h = heard[i];
                string rate = h.Usable ? RadioMath.FormatRate(h.rateKbps) : Loc.T("#rr_lv_weak10");
                GUILayout.Label(Loc.T("#rr_lv_station", DsnManager.TitleOf(h.to), h.feed, $"{h.elevationDeg:0}", $"{h.Cn0:0.0}", MarginText(h.Margin), rate));
            }
            if (heard.Count > SHOWN_STATIONS) GUILayout.Label("  " + Loc.T("#rr_lv_more", heard.Count - SHOWN_STATIONS));
            foreach (KeyValuePair<string, int> reason in reasons) GUILayout.Label($"  {reason.Key}: {reason.Value}");
        }

        private static string MarginText(double margin)
        {
            if (double.IsInfinity(margin) || double.IsNaN(margin)) return Loc.T("#rr_lv_no_signal");
            string color = margin >= 6 ? "#00FF00" : margin >= 0 ? "#FFD700" : "#FF6060";
            return Loc.T("#rr_lv_margin", color, $"{margin:+0.0;-0.0}");
        }

        private static string Db(double db) { return db.ToString("0.0", inv) + " " + Loc.T("#rr_unit_db"); }
        private static string Dbi(double db) { return db.ToString("0.0", inv) + " dBi"; }
        private static string Dbm(double dbm) { return dbm.ToString("0.0", inv) + " dBm"; }

        private static string Watts(double dbm)
        {
            double w = Math.Pow(10.0, (dbm - 30.0) / 10.0);
            return w >= 1000 ? (w / 1000).ToString("0.#", inv) + " " + Loc.T("#rr_unit_kw") : w >= 1 ? w.ToString("0.#", inv) + " " + Loc.T("#rr_unit_w") : (w * 1000).ToString("0", inv) + " " + Loc.T("#rr_unit_mw");
        }

        private static string Bandwidth(double hz)
        {
            return hz >= 1e6 ? (hz / 1e6).ToString("0.##", inv) + " " + Loc.T("#rr_unit_mhz") : (hz / 1e3).ToString("0.#", inv) + " " + Loc.T("#rr_unit_khz");
        }

        public static string Delay(double seconds)
        {
            if (seconds < 1.0) return Loc.T("#rr_time_ms", (seconds * 1000).ToString("0.#", inv));
            if (seconds < 120.0) return Loc.T("#rr_time_s", seconds.ToString("0.0", inv));
            int minutes = (int)(seconds / 60);
            return seconds < 7200.0 ? Loc.T("#rr_time_min_s", minutes, (int)(seconds - minutes * 60)) : Loc.T("#rr_time_h_min", minutes / 60, minutes % 60);
        }

        private static string Short(string name)
        {
            return name != null && name.Length > 20 ? name.Substring(0, 19) + "…" : name;
        }
    }
}
