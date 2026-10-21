// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RealisticRadio
{
    // Как отдавать науку: целиком - начисляется, когда дошёл весь файл; по частям - каждая десятая часть начисляется,
    // как только дошла (на медленной линии наука идёт сразу, а обрыв не губит уже дошедшее)
    public enum ScienceSendMode { Whole, Parts }

    // Правила передачи науки по уровню сложности игры
    public static class ScienceRules
    {
        public enum Level { Easy, Normal, Hard }

        public const double EASY_RATE_FACTOR = 10.0;

        // Лёгкий; нормальный (и свой без «Требовать сигнал»); средний, сложный и свой с «Требовать сигнал»
        public static Level Current
        {
            get
            {
                GameParameters parameters = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Parameters : null;
                if (parameters == null) return Level.Normal;
                switch (parameters.preset)
                {
                    case GameParameters.Preset.Easy: return Level.Easy;
                    case GameParameters.Preset.Moderate:
                    case GameParameters.Preset.Hard: return Level.Hard;
                    case GameParameters.Preset.Custom: return RadioControlLock.SignalRequired() ? Level.Hard : Level.Normal;
                    default: return Level.Normal;
                }
            }
        }

        // Лёгкий: скорость линии ×10 и без расхода электричества
        public static double RateFactor { get { return Current == Level.Easy ? EASY_RATE_FACTOR : 1.0; } }
        public static bool UsesPower { get { return Current != Level.Easy; } }

        // Средний и сложный: обрыв связи - как на старых АМС без подтверждения приёма: файл, который шёл целиком,
        // надо передавать заново, а по частям пропадает только недошедшая часть. Ниже - передача встаёт на паузу и
        // продолжается с того же места (как протокол CFDP у современных аппаратов)
        public static bool RestartOnLoss { get { return Current == Level.Hard; } }

        public static string Describe()
        {
            switch (Current)
            {
                case Level.Easy: return "лёгкий уровень: скорость линии ×10, электричество не тратится, при обрыве - пауза";
                case Level.Hard: return "средний/сложный: при обрыве файл «целиком» начинается заново, «по частям» - с последней дошедшей части";
                default: return "нормальный уровень: настоящая скорость, электричество по мощности передатчика, при обрыве - пауза";
            }
        }
    }

    // Очередь передачи науки аппаратов: данные уходят на Землю со скоростью линии связи (узкое место пути CommNet) в
    // игровом времени - и при ускорении времени, и пока аппарат в фоне (в полёте, центре слежения и космоцентре).
    // Нет связи - пауза; электричество - по мощности передатчика (у загруженного аппарата). Заменяет стоковую передачу:
    // та считает число пакетов в момент нажатия и не замечает ни смены скорости, ни обрыва
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public class ScienceTransfer : ScenarioModule
    {
        public const int PARTS = 10;
        private const float TICK_S = 0.2f;
        private static readonly CultureInfo inv = CultureInfo.InvariantCulture;

        public class Item
        {
            public ConfigNode data;                  // ScienceData целиком: вернуть на борт или отдать событию контракта
            public string subjectId = "", title = "";
            public double total, sent, credited;     // мит (мегабит)
            public float ratio, xmit;
            public bool triggered;
            public ScienceSendMode mode;
            public bool flowing;                     // данные шли в прошлый раз (для обрыва)
            public int restarts;

            public double Fraction { get { return total > 0 ? sent / total : 1.0; } }
        }

        public class Queue
        {
            public uint vessel;
            public uint part;                         // деталь-передатчик (flightID): за ней очередь переходит при стыковке
            public bool paused;
            public readonly List<Item> items = new List<Item>();
            public string state = "";                // что сейчас происходит - для окна и детали
            public double rateBps;                    // скорость передачи сейчас, бит/с
            public double Left { get { double left = 0; foreach (Item i in items) left += i.total - i.sent; return left; } }
        }

        public static ScienceTransfer Instance { get; private set; }

        private readonly Dictionary<uint, Queue> queues = new Dictionary<uint, Queue>();
        private double lastUT = double.NaN;
        private float nextTick;

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
        }

        public void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // --- Для деталей и окон ---

        public static Queue QueueOf(Vessel v)
        {
            Queue q;
            return Instance != null && v != null && Instance.queues.TryGetValue(v.persistentId, out q) && q.items.Count > 0 ? q : null;
        }

        public static void Enqueue(Vessel v, ScienceData data, ScienceSendMode mode)
        {
            if (Instance == null || v == null || data == null) return;
            Queue q;
            if (!Instance.queues.TryGetValue(v.persistentId, out q))
            {
                q = new Queue { vessel = v.persistentId };
                Instance.queues[v.persistentId] = q;
            }
            if (q.part == 0) q.part = PartOf(v);
            var node = new ConfigNode("ScienceData");
            data.Save(node);
            q.items.Add(new Item
            {
                data = node,
                subjectId = data.subjectID ?? "",
                title = data.title ?? "",
                total = Math.Max(data.dataAmount, 0f),
                ratio = data.scienceValueRatio,
                xmit = data.baseTransmitValue + data.transmitBonus,
                triggered = data.triggered,
                mode = mode
            });
            ScreenMessages.PostScreenMessage($"Наука «{data.title}» в очереди на передачу ({Mit(data.dataAmount)}, {(mode == ScienceSendMode.Whole ? "целиком" : "по частям")})",
                                             4f, ScreenMessageStyle.UPPER_LEFT);
        }

        public static void SetPaused(Vessel v, bool paused)
        {
            Queue q = QueueOf(v);
            if (q != null) q.paused = paused;
        }

        // Отменить передачу: что не дошло, вернуть в контейнер науки на борту (только у загруженного аппарата)
        public static bool Cancel(Vessel v, Item item, out string why)
        {
            why = "";
            Queue q = QueueOf(v);
            if (q == null || !q.items.Contains(item)) return false;
            if (!v.loaded)
            {
                why = "аппарат не загружен";
                return false;
            }
            double left = item.total - item.credited;
            if (left > 1e-9)
            {
                var data = new ScienceData(item.data);
                data.dataAmount = (float)left;
                bool stored = false;
                foreach (ModuleScienceContainer box in v.FindPartModulesImplementing<ModuleScienceContainer>())
                {
                    if (box.AddData(data))
                    {
                        stored = true;
                        break;
                    }
                }
                if (!stored)
                {
                    why = "на борту нет контейнера науки со свободным местом";
                    return false;
                }
            }
            q.items.Remove(item);
            return true;
        }

        // --- Ход времени ---

        public void Update()
        {
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + TICK_S;
            double ut = Planetarium.GetUniversalTime();
            if (double.IsNaN(lastUT) || ut < lastUT)
            {
                lastUT = ut;
                return;
            }
            double dt = ut - lastUT;
            lastUT = ut;
            if (dt <= 0 || queues.Count == 0) return;

            var gone = new List<uint>();
            var moved = new List<KeyValuePair<Queue, Vessel>>();
            foreach (Queue q in queues.Values)
            {
                Vessel v;
                if (q.items.Count == 0)
                {
                    gone.Add(q.vessel);
                    continue;
                }
                if (!FlightGlobals.FindVessel(q.vessel, out v) || v == null)
                {
                    // Аппарат пристыковался (у него новый id) - очередь идёт за деталью-передатчиком. Пропал совсем
                    // (разбился, возвращён) - очередь пропадает с ним; в сцене без аппаратов - ждём
                    Vessel owner = WithPart(q.part);
                    if (owner != null) moved.Add(new KeyValuePair<Queue, Vessel>(q, owner));
                    else if (!Saved(q.vessel)) gone.Add(q.vessel);
                    continue;
                }
                try
                {
                    Process(q, v, dt);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RealisticRadio] Передача науки «{v.vesselName}»: {ex}");
                }
            }
            foreach (uint id in gone) queues.Remove(id);
            foreach (KeyValuePair<Queue, Vessel> m in moved)
            {
                queues.Remove(m.Key.vessel);
                Queue target;
                if (queues.TryGetValue(m.Value.persistentId, out target)) target.items.AddRange(m.Key.items);
                else
                {
                    m.Key.vessel = m.Value.persistentId;
                    queues[m.Key.vessel] = m.Key;
                }
            }
        }

        // Деталь-передатчик аппарата: лучшая антенна мода
        private static uint PartOf(Vessel v)
        {
            if (!v.loaded) return 0;
            ModuleRadioLink radio = ModuleRadioLink.FindBestRadio(v);
            return radio != null && radio.part != null ? radio.part.flightID : 0;
        }

        private static Vessel WithPart(uint flightId)
        {
            if (flightId == 0 || FlightGlobals.Vessels == null) return null;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null) continue;
                if (v.loaded)
                {
                    foreach (Part p in v.parts) if (p.flightID == flightId) return v;
                }
                else if (v.protoVessel != null)
                {
                    foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) if (p.flightID == flightId) return v;
                }
            }
            return null;
        }

        // Аппарат есть в сохранении (сцена, где аппараты ещё не созданы)
        private static bool Saved(uint persistentId)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null) return false;
            foreach (ProtoVessel pv in HighLogic.CurrentGame.flightState.protoVessels)
            {
                if (pv != null && pv.persistentId == persistentId) return true;
            }
            return false;
        }

        private static void Process(Queue q, Vessel v, double dt)
        {
            q.rateBps = 0;
            if (q.paused)
            {
                q.state = "остановлена";
                return;
            }
            // Скорость - как у линии: узкое место пути CommNet до станции
            double bps = RadioCommNetwork.DownlinkRate(v);
            if (bps <= 0)
            {
                LinkLost(q, v);
                q.state = "пауза: нет связи с Землёй";
                return;
            }
            bps *= ScienceRules.RateFactor;

            // Электричество на передатчик (у загруженного аппарата): нет - передача идёт настолько, насколько хватило
            double seconds = dt;
            q.state = "идёт";
            if (ScienceRules.UsesPower && v.loaded)
            {
                ModuleRadioLink radio = ModuleRadioLink.FindBestRadio(v);
                double kw = radio != null ? radio.PowerDrawKw : 0.0;
                if (kw > 0)
                {
                    double need = kw * dt;
                    double got = radio.part.RequestResource(PartResourceLibrary.ElectricityHashcode, need);
                    if (got < need * 0.999)
                    {
                        seconds = dt * Math.Max(0.0, got) / need;
                        q.state = "пауза: не хватает электричества";
                    }
                }
            }

            q.rateBps = seconds > 0 ? bps * seconds / dt : 0.0;
            double mit = bps * seconds / 1e6;
            while (mit > 0 && q.items.Count > 0)
            {
                Item item = q.items[0];
                double take = Math.Min(mit, item.total - item.sent);
                item.sent += take;
                mit -= take;
                item.flowing = true;
                if (item.sent >= item.total - 1e-9)
                {
                    item.sent = item.total;
                    Credit(v, item, true);
                    Finish(v, item);
                    q.items.RemoveAt(0);
                }
                else
                {
                    Credit(v, item, false);
                }
            }
        }

        // Связь пропала посреди файла: на среднем и сложном недошедшее надо слать заново
        private static void LinkLost(Queue q, Vessel v)
        {
            if (q.items.Count == 0) return;
            Item item = q.items[0];
            if (!item.flowing) return;
            item.flowing = false;
            if (!ScienceRules.RestartOnLoss || item.sent <= item.credited + 1e-9) return;
            item.sent = item.credited;
            item.restarts++;
            ScreenMessages.PostScreenMessage(item.mode == ScienceSendMode.Whole
                ? $"{v.vesselName}: связь прервалась - «{item.title}» придётся передавать заново"
                : $"{v.vesselName}: связь прервалась - «{item.title}» продолжится с последней дошедшей части", 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        // Начислить дошедшее: целиком - в конце, по частям - каждую дошедшую десятую часть
        private static void Credit(Vessel v, Item item, bool final)
        {
            double due;
            if (item.mode == ScienceSendMode.Whole) due = final ? item.total : 0.0;
            else due = final ? item.total : Math.Floor(item.sent / (item.total / PARTS) + 1e-9) * (item.total / PARTS);
            double amount = due - item.credited;
            if (amount <= 1e-9) return;
            item.credited = due;
            double gained = Submit(v, item, amount);
            string science = gained >= 0 ? $": +{gained.ToString("0.##", inv)} науки" : "";
            if (item.mode == ScienceSendMode.Parts && !final)
                ScreenMessages.PostScreenMessage($"«{item.title}»: дошло {item.credited / item.total:P0}{science}", 3f, ScreenMessageStyle.UPPER_LEFT);
            else
                ScreenMessages.PostScreenMessage($"«{item.title}» передано на Землю{science}", 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        // -1 - в игре нет науки (песочница)
        private static double Submit(Vessel v, Item item, double amount)
        {
            if (ResearchAndDevelopment.Instance == null) return -1.0;
            ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(item.subjectId);
            if (subject == null)
            {
                Debug.LogWarning($"[RealisticRadio] Наука «{item.title}»: субъект {item.subjectId} не найден");
                return 0.0;
            }
            return ResearchAndDevelopment.Instance.SubmitScienceData((float)amount, item.ratio, subject, item.xmit, v.protoVessel, false);
        }

        // Файл дошёл: условие контракта «передать данные» - как у стоковой передачи
        private static void Finish(Vessel v, Item item)
        {
            if (!item.triggered) return;
            try
            {
                GameEvents.OnTriggeredDataTransmission.Fire(new ScienceData(item.data), v, false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RealisticRadio] Событие передачи данных: {ex.Message}");
            }
        }

        public static string Mit(double mit)
        {
            return mit >= 0.1 ? mit.ToString("0.##", inv) + " Мит" : (mit * 1000).ToString("0.#", inv) + " кбит";
        }

        // --- Сохранение ---

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            foreach (Queue q in queues.Values)
            {
                if (q.items.Count == 0) continue;
                ConfigNode qn = node.AddNode("QUEUE");
                qn.AddValue("vessel", q.vessel);
                qn.AddValue("paused", q.paused);
                qn.AddValue("part", q.part);
                foreach (Item i in q.items)
                {
                    ConfigNode n = qn.AddNode("ITEM");
                    n.AddValue("subject", i.subjectId);
                    n.AddValue("title", i.title);
                    n.AddValue("total", i.total.ToString("R", inv));
                    n.AddValue("sent", i.sent.ToString("R", inv));
                    n.AddValue("credited", i.credited.ToString("R", inv));
                    n.AddValue("ratio", i.ratio.ToString("R", inv));
                    n.AddValue("xmit", i.xmit.ToString("R", inv));
                    n.AddValue("triggered", i.triggered);
                    n.AddValue("mode", i.mode.ToString());
                    n.AddValue("flowing", i.flowing);
                    n.AddValue("restarts", i.restarts);
                    if (i.data != null) n.AddNode(i.data);
                }
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            queues.Clear();
            foreach (ConfigNode qn in node.GetNodes("QUEUE"))
            {
                uint id;
                if (!uint.TryParse(qn.GetValue("vessel"), out id)) continue;
                var q = new Queue { vessel = id };
                bool paused;
                if (bool.TryParse(qn.GetValue("paused"), out paused)) q.paused = paused;
                uint part;
                if (uint.TryParse(qn.GetValue("part"), out part)) q.part = part;
                foreach (ConfigNode n in qn.GetNodes("ITEM"))
                {
                    var i = new Item
                    {
                        subjectId = n.GetValue("subject") ?? "",
                        title = n.GetValue("title") ?? "",
                        total = Num(n, "total"),
                        sent = Num(n, "sent"),
                        credited = Num(n, "credited"),
                        ratio = (float)Num(n, "ratio"),
                        xmit = (float)Num(n, "xmit"),
                        data = n.GetNode("ScienceData")
                    };
                    bool flag;
                    if (bool.TryParse(n.GetValue("triggered"), out flag)) i.triggered = flag;
                    if (bool.TryParse(n.GetValue("flowing"), out flag)) i.flowing = flag;
                    int restarts;
                    if (int.TryParse(n.GetValue("restarts"), out restarts)) i.restarts = restarts;
                    i.mode = n.GetValue("mode") == ScienceSendMode.Parts.ToString() ? ScienceSendMode.Parts : ScienceSendMode.Whole;
                    q.items.Add(i);
                }
                if (q.items.Count > 0) queues[id] = q;
            }
        }

        private static double Num(ConfigNode n, string key)
        {
            double value;
            return double.TryParse(n.GetValue(key), NumberStyles.Float, inv, out value) ? value : 0.0;
        }
    }
}
