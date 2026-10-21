// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using KSP.UI.Screens;

namespace RealisticRadio
{
    // Окно мода (кнопка на панели в полёте). Ядро рисует вкладку «Аппарат»: связь с Землёй и антенны. Дополнение
    // добавляет свои вкладки (AddTab) и строки на вкладке «Аппарат» (AddSection). Положение окна и вкладка - в настройках
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RadioWindow : MonoBehaviour
    {
        private const int WINDOW_ID = 133742;
        private const float WIDTH = 540f;
        private static readonly CultureInfo inv = CultureInfo.InvariantCulture;

        // Порядок строк ядра на вкладке «Аппарат»: дополнение вставляет свои между ними
        public const int SECTION_TITLE = 0, SECTION_LINK = 20, SECTION_ANTENNAS = 30;

        private class Tab
        {
            public string name;
            public Action<Vessel> draw;
        }

        private class Section
        {
            public int order;
            public Action<Vessel> draw;
        }

        private static readonly List<Tab> tabs = new List<Tab>();
        private static readonly List<Section> sections = new List<Section>();

        private const int OWN_TABS = 2;   // «Аппарат» и «Линия связи»

        private readonly LinkView linkView = new LinkView();
        private ApplicationLauncherButton appButton;
        private Rect windowRect = new Rect(Screen.width - 600, 100, WIDTH, 200);
        private int tab;

        // Окно открыто (дополнение по нему решает, считать ли эфир для показа)
        public static bool Open { get; private set; }

        // Вкладка дополнения после «Аппарата»; повторный вызов с тем же именем заменяет её
        public static void AddTab(string name, Action<Vessel> draw)
        {
            tabs.RemoveAll(t => t.name == name);
            tabs.Add(new Tab { name = name, draw = draw });
        }

        public static void RemoveTab(string name)
        {
            tabs.RemoveAll(t => t.name == name);
        }

        public static void AddSection(int order, Action<Vessel> draw)
        {
            sections.Add(new Section { order = order, draw = draw });
            sections.Sort((a, b) => a.order.CompareTo(b.order));
        }

        public static void RemoveSection(Action<Vessel> draw)
        {
            sections.RemoveAll(s => s.draw == draw);
        }

        public void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnDestroyed);
            float x, y;
            int saved;
            if (float.TryParse(RadioSettings.Get("rarWindowX"), NumberStyles.Float, inv, out x) &&
                float.TryParse(RadioSettings.Get("rarWindowY"), NumberStyles.Float, inv, out y))
            {
                windowRect.x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 120f));
                windowRect.y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 80f));
            }
            if (int.TryParse(RadioSettings.Get("rarTab"), NumberStyles.Integer, inv, out saved)) tab = Math.Max(0, saved);
            if (ApplicationLauncher.Ready) OnReady();
        }

        public void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnDestroyed);
            RemoveButton();
            if (Open) SavePosition();
            Open = false;
        }

        private void SavePosition()
        {
            RadioSettings.Set("rarWindowX", windowRect.x.ToString("0", inv));
            RadioSettings.Set("rarWindowY", windowRect.y.ToString("0", inv));
        }

        private void OnReady()
        {
            if (appButton != null) return;
            Texture2D icon = GameDatabase.Instance.GetTexture("RealisticRadio/icon", false);
            if (icon == null) icon = new Texture2D(38, 38);
            appButton = ApplicationLauncher.Instance.AddModApplication(ToggleOn, ToggleOff, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT, icon);
        }

        private void OnDestroyed()
        {
            RemoveButton();
        }

        private void RemoveButton()
        {
            if (appButton == null) return;
            ApplicationLauncher.Instance.RemoveModApplication(appButton);
            appButton = null;
        }

        private void ToggleOn()
        {
            Open = true;
        }

        private void ToggleOff()
        {
            Open = false;
            SavePosition();
        }

        private void OnGUI()
        {
            if (!Open || !HighLogic.LoadedSceneIsFlight) return;
            GUI.skin = HighLogic.Skin;
            windowRect = GUILayout.Window(WINDOW_ID, windowRect, DrawWindow, "RealisticRadio", GUILayout.Width(WIDTH));
        }

        private void DrawWindow(int windowID)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            // Вкладки ядра, потом дополнения
            var names = new string[OWN_TABS + tabs.Count];
            names[0] = "Аппарат";
            names[1] = "Линия связи";
            for (int i = 0; i < tabs.Count; i++) names[OWN_TABS + i] = tabs[i].name;
            int current = Mathf.Clamp(tab, 0, names.Length - 1);
            int chosen = GUILayout.Toolbar(current, names);
            if (chosen != tab)
            {
                tab = chosen;
                windowRect.height = 0f; // окно подстроится под новую вкладку
                RadioSettings.Set("rarTab", tab.ToString(inv));
            }
            GUILayout.Space(4);
            try
            {
                if (chosen == 0) DrawVessel(v);
                else if (chosen == 1) linkView.Draw(v);
                else tabs[chosen - OWN_TABS].draw(v);
            }
            catch (Exception ex)
            {
                GUILayout.Label($"<color=orange>Ошибка вкладки: {ex.Message}</color>");
                Debug.LogException(ex);
            }
            GUI.DragWindow();
        }

        // --- «Аппарат»: связь с Землёй и антенны (и строки дополнения между ними) ---

        private void DrawVessel(Vessel v)
        {
            if (v == null)
            {
                GUILayout.Label("Нет активного аппарата");
                return;
            }
            int next = 0;
            DrawSections(v, ref next, SECTION_TITLE);
            GUILayout.Label($"<b>{v.vesselName}</b> - {Situation(v)}");
            DrawSections(v, ref next, SECTION_LINK);
            DrawLink(v);
            DrawScience(v);
            DrawSections(v, ref next, SECTION_ANTENNAS);
            DrawAntennas(v);
            DrawSections(v, ref next, int.MaxValue);
        }

        // Строки дополнения с порядком меньше границы
        private static void DrawSections(Vessel v, ref int next, int below)
        {
            while (next < sections.Count && sections[next].order < below)
            {
                try
                {
                    sections[next].draw(v);
                }
                catch (Exception ex)
                {
                    GUILayout.Label($"<color=orange>Ошибка: {ex.Message}</color>");
                    Debug.LogException(ex);
                }
                next++;
            }
        }

        private static void DrawLink(Vessel v)
        {
            ModuleRadioLink activeRadio = ModuleRadioLink.FindBestRadio(v);
            if (activeRadio == null)
            {
                GUILayout.Label("<color=orange>На аппарате нет антенн мода: связи с Землёй нет</color>");
                return;
            }
            LinkResult link = activeRadio.currentLink;
            GUILayout.Label($"Связь с Землёй: {(link.hasSignal ? "<color=#00FF00>есть</color>" : "<color=#FF0000>нет</color>")}");
            if (link.hasSignal)
            {
                GUILayout.Label(link.hopCount > 1
                    ? $"Маршрут: <color=#FFA500>через ретрансляторы ({link.hopCount} участков)</color>, до: {link.dsnName}"
                    : $"Маршрут: <color=#00FFFF>напрямую</color>, станция: {link.dsnName}");
                if (link.commandStation.Length > 0)
                    GUILayout.Label($"Команды: с {link.commandStation} - станция приёма не может командовать бортом");
            }
            string mode = link.mode != "" ? $", {link.mode}" : "";
            GUILayout.Label($"Скорость: {RadioMath.FormatRate(link.speed)}{mode}; задержка {LinkView.Delay(link.ping / 2.0)} (туда-обратно {LinkView.Delay(link.ping)}); наведение {link.pointingError:F1}°");
            // C/N0 не зависит от полосы: по нему видно, сколько осталось до самой медленной линии
            if (!double.IsInfinity(link.cn0))
            {
                GUILayout.Label($"C/N0: {link.cn0:F1} дБ·Гц (запас до {RadioMath.FormatRate(RadioMath.MIN_DATA_RATE_BPS / 1000.0)}: {link.Margin:F1} дБ)");
            }
            if (link.hasSignal) GUILayout.Label("Команды с Земли: " + (link.uplink ? "<color=#00FF00>доходят</color>" : "<color=orange>не доходят - управления нет</color>"));
            if (!link.hasSignal && link.report != null && link.report.why.Length > 0) GUILayout.Label($"<color=orange>{link.report.why}</color>");
            GUILayout.Label("<color=#aaaaaa>Расчёт по шагам - вкладка «Линия связи»</color>");
        }

        // Очередь передачи науки аппарата: режим, состояние, файлы
        private static void DrawScience(Vessel v)
        {
            ModuleRadioLink radio = ModuleRadioLink.FindBestRadio(v);
            if (radio == null) return;
            ScienceTransfer.Queue q = ScienceTransfer.QueueOf(v);
            GUILayout.Space(6);
            GUILayout.Label("<b>Передача науки</b>");
            GUILayout.Label($"<color=#aaaaaa>{ScienceRules.Describe()}</color>");
            GUILayout.BeginHorizontal();
            GUILayout.Label(radio.scienceInParts ? "Режим: <b>по частям</b> - наука за каждую дошедшую десятую часть"
                                                 : "Режим: <b>целиком</b> - наука, когда дошёл весь файл");
            if (GUILayout.Button(radio.scienceInParts ? "Целиком" : "По частям", GUILayout.Width(90))) radio.TransmitIncompleteToggle();
            GUILayout.EndHorizontal();
            if (ScienceRules.UsesPower)
                GUILayout.Label($"Питание передатчика: {(radio.PowerDrawKw * 1000.0).ToString("0.#", inv)} Вт ({radio.PowerDrawKw.ToString("0.###", inv)} ед./с)");
            if (q == null)
            {
                GUILayout.Label("<color=#aaaaaa>Очередь пуста: «Передать» в окне эксперимента или «Передать науку» в меню антенны</color>");
                return;
            }

            string rate = q.rateBps > 0 ? RadioMath.FormatRate(q.rateBps / 1000.0) : "-";
            string eta = q.rateBps > 0 ? $", ещё {LinkView.Delay(q.Left * 1e6 / q.rateBps)}" : "";
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Состояние: {q.state}; скорость {rate}; осталось {ScienceTransfer.Mit(q.Left)}{eta}");
            if (GUILayout.Button(q.paused ? "Продолжить" : "Пауза", GUILayout.Width(90))) ScienceTransfer.SetPaused(v, !q.paused);
            GUILayout.EndHorizontal();

            ScienceTransfer.Item cancel = null;
            foreach (ScienceTransfer.Item item in q.items)
            {
                GUILayout.BeginHorizontal();
                string mode = item.mode == ScienceSendMode.Parts ? $"по частям, засчитано {item.credited / Math.Max(item.total, 1e-9):P0}" : "целиком";
                string restarts = item.restarts > 0 ? $", заново {item.restarts} раз" : "";
                GUILayout.Label($"• {item.title}: {ScienceTransfer.Mit(item.sent)} из {ScienceTransfer.Mit(item.total)} ({item.Fraction:P0}), {mode}{restarts}");
                if (v.loaded && GUILayout.Button("Отменить", GUILayout.Width(90))) cancel = item;
                GUILayout.EndHorizontal();
            }
            if (cancel != null)
            {
                string why;
                ScreenMessages.PostScreenMessage(ScienceTransfer.Cancel(v, cancel, out why)
                    ? $"«{cancel.title}»: передача отменена, недошедшее - в контейнере науки"
                    : $"«{cancel.title}» не отменить: {why}", 4f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        private static void DrawAntennas(Vessel v)
        {
            List<ModuleRadioAntenna> antennas = ModuleRadioAntenna.On(v);
            GUILayout.Space(6);
            GUILayout.Label($"<b>Антенны ({antennas.Count})</b>");
            foreach (ModuleRadioAntenna antenna in antennas) GUILayout.Label("• " + DescribeAntenna(antenna));
        }

        public static string Situation(Vessel v)
        {
            string body = v.mainBody != null ? v.mainBody.bodyName : "";
            switch (v.situation)
            {
                case Vessel.Situations.PRELAUNCH: return $"на старте ({body})";
                case Vessel.Situations.LANDED: return $"на поверхности ({body})";
                case Vessel.Situations.SPLASHED: return $"на воде ({body})";
                case Vessel.Situations.FLYING: return $"в полёте ({body})";
                case Vessel.Situations.SUB_ORBITAL: return $"суборбитальный полёт ({body})";
                case Vessel.Situations.ORBITING: return $"на орбите ({body})";
                case Vessel.Situations.ESCAPING: return $"уходит из сферы действия ({body})";
                case Vessel.Situations.DOCKED: return $"в связке ({body})";
                default: return body;
            }
        }

        public static string PartTitle(Part p)
        {
            return p != null && p.partInfo != null ? p.partInfo.title : p != null ? p.name : "деталь";
        }

        public static string DescribeAntenna(ModuleRadioAntenna a)
        {
            var parts = new List<string> { a.Title, a.kind, a.Band() };
            parts.Add(a.IsDirectional ? $"{a.GainAt(a.BandCenterMHz):0} dBi, луч {a.BeamwidthAt(a.BandCenterMHz):0.#}°" : $"{a.antennaGain:0.#} dBi");
            if (a.IsDirectional) parts.Add(a.tracking ? "следит за целью" : "по оси детали");
            if (a.dedicated) parts.Add("антенна прибора");
            if (!a.IsDeployed()) parts.Add("<color=orange>сложена</color>");
            ModuleRadioLink link = a.part != null ? a.part.FindModuleImplementing<ModuleRadioLink>() : null;
            if (link != null)
            {
                double watts = Math.Pow(10.0, (link.transmitPower - 30.0) / 10.0);
                parts.Add($"линия DSN {link.frequencyMHz:0.###} МГц, {watts:0.#} Вт" + (link.isRelay ? ", <color=#00FFFF>ретранслятор</color>" : ""));
                LinkResult current = link.currentLink;
                parts.Add(current.hasSignal && current.speed > 0 ? $"сейчас {RadioMath.FormatRate(current.speed)} ({current.dsnName})" : "сейчас связи нет");
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
