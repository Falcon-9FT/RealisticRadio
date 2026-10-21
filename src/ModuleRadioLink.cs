// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRadio
{
    public class LinkResult
    {
        public bool hasSignal = false;
        public string dsnName = "";
        public string commandStation = "";   // станция команд, если команды идут не с той, что принимает данные
        public double ping = 0;
        public double snr = -999;  // С/Ш во всей полосе канала, дБ
        public double cn0 = double.NegativeInfinity; // C/N0 слабейшего участка, дБ·Гц: по нему скорость и есть ли связь
        public double speed = 0;   // кбит/с

        // Запас до самой медленной линии (10 бит/с), дБ
        public double Margin { get { return cn0 - RadioMath.MIN_LINK_CN0; } }
        public string mode = "";   // режим модуляции и кодирования
        public double pointingError = 0;
        public int hopCount = 0;

        public bool visible;          // хоть одна станция или ретранслятор в прямой видимости
        public bool uplink;           // команды с Земли доходят до борта
        public double uplinkCn0 = double.NegativeInfinity;
        public LinkReport report;     // разбор по участкам (окно «Линия связи»)

        // Итог по разбору: скорость и C/N0 - по слабейшему участку пути вниз, задержка - туда и обратно
        public static LinkResult From(LinkReport report)
        {
            var result = new LinkResult { report = report, visible = report.visible, uplink = report.connected && report.Uplink };
            HopBudget weakest = report.Bottleneck;
            result.speed = report.RateKbps;
            // Связь есть, когда есть путь CommNet до станции мода (его линии решает этот же расчёт)
            result.hasSignal = report.connected && !report.stock && result.speed > 0;
            result.hopCount = report.down.Count;
            double upDelay = 0;
            foreach (HopBudget h in report.up) upDelay += h.DelaySeconds;
            result.ping = report.DelaySeconds + (report.up.Count > 0 ? upDelay : report.DelaySeconds);
            if (weakest != null)
            {
                result.cn0 = weakest.Cn0;
                result.snr = weakest.Cn0 - 10.0 * Math.Log10(Math.Max(weakest.bandwidthHz, 1.0));
                result.mode = weakest.mode;
            }
            if (report.down.Count > 0)
            {
                HopBudget last = report.down[report.down.Count - 1];
                result.dsnName = last.toGround ? last.to : "";
                result.pointingError = report.down[0].txOffAxisDeg;
            }
            HopBudget up = report.UplinkBottleneck;
            if (up != null) result.uplinkCn0 = up.Cn0;
            if (report.up.Count > 0 && report.up[0].fromGround && report.up[0].from != result.dsnName) result.commandStation = report.up[0].from;
            return result;
        }
    }

    public class ModuleRadioLink : ModuleDataTransmitter, ICommAntenna
    {
        private const double VANILLA_DSN_POWER = 2.5e11; // стоковая станция слежения третьего уровня

        // Нули - подобрать по названию детали и стоковой "мощности" (антенны других модов, см. AntennaCatalog)
        [KSPField(isPersistant = false)] public float antennaGain = 0f;     // dBi
        [KSPField(isPersistant = false)] public float frequencyMHz = 0f;
        [KSPField(isPersistant = false)] public float transmitPower = 0f;   // dBm
        [KSPField(isPersistant = false)] public float beamwidth = 360.0f;    // градусы, 360 - всенаправленная
        [KSPField(isPersistant = false)] public float bandwidthMHz = 0f;
        [KSPField(isPersistant = false)] public bool isRelay = false;
        [KSPField(isPersistant = false)] public double stockPower = 0;       // "мощность" стокового CommNet из конфига детали
        [KSPField(isPersistant = false)] public string hintName = "";        // имя и название детали для подбора (пишет патч MM)
        [KSPField(isPersistant = false)] public string hintTitle = "";

        // Наука: целиком или по частям (одна настройка на аппарат - кнопка меняет её у всех антенн)
        [KSPField(isPersistant = true)] public bool scienceInParts = false;

        // Электропитание передатчика: излучаемая мощность / КПД усилителя + приёмник и электроника. 1 ед. электричества = 1 кДж
        private const double TX_EFFICIENCY = 0.35;
        private const double ELECTRONICS_W = 5.0;

        private float updateTimer = 0f;
        public LinkResult currentLink = new LinkResult();
        public bool linkReady = false;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            try
            {
                AutoConfigure();
                this.antennaType = isRelay ? AntennaType.RELAY : AntennaType.DIRECT;
                this.antennaPower = CalculateEquivalentVanillaPower();
                DsnManager.LoadStations();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RealisticRadio] ModuleRadioLink.OnStart: {ex}");
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            try
            {
                // Деталь другого мода: патч переименовал её стоковый передатчик, и в конфиге остались его тип и "мощность"
                if (node.HasValue("antennaPower") && stockPower <= 0) stockPower = antennaPower;
                if (node.HasValue("antennaType") && !node.HasValue("isRelay")) isRelay = antennaType == AntennaType.RELAY;
                AutoConfigure();
                this.antennaPower = CalculateEquivalentVanillaPower();
                this.antennaType = isRelay ? AntennaType.RELAY : AntennaType.DIRECT;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RealisticRadio] ModuleRadioLink.OnLoad: {ex}");
            }
        }

        // Частота, антенна и передатчик линии DSN, если конфиг их не задал: по той же классификации, что и антенна для SDR
        public void AutoConfigure()
        {
            if (frequencyMHz > 0) return;
            bool deployable = part != null && part.FindModuleImplementing<ModuleDeployableAntenna>() != null;
            string title = hintTitle;
            if (part != null && part.partInfo != null) title += " " + part.partInfo.title;
            AntennaSpec spec = AntennaCatalog.Classify(hintName + " " + title, isRelay ? AntennaCatalog.RELAY : AntennaCatalog.DIRECT, stockPower, deployable);
            frequencyMHz = (float)spec.CenterMHz;
            antennaGain = (float)spec.GainAt(spec.CenterMHz);
            beamwidth = (float)Math.Min(360.0, spec.BeamwidthAt(spec.CenterMHz));
            if (beamwidth >= 180f) beamwidth = 360f;
            transmitPower = (float)spec.linkPowerDbm;
            bandwidthMHz = (float)spec.linkBandwidthMHz;
        }

        // Образец детали пересчитывается, когда игра уже знает свою сеть DSN: стоковый CommNet берёт дальность
        // выгруженных аппаратов из образца
        public void RefreshPrefab()
        {
            frequencyMHz = frequencyMHz > 0 && hintName.Length == 0 && hintTitle.Length == 0 ? frequencyMHz : 0f;
            AutoConfigure();
            antennaType = isRelay ? AntennaType.RELAY : AntennaType.DIRECT;
            antennaPower = CalculateEquivalentVanillaPower();
        }

        public override string GetInfo()
        {
            AutoConfigure();
            string info = "<b>RealisticRadio: связь с Землёй</b>\n\n";
            info += $"Тип: {(isRelay ? "ретранслятор" : "прямая связь")}\n";
            info += $"Частота: {frequencyMHz:0.###} МГц\n";
            info += $"Усиление: {antennaGain:0.#} dBi\n";
            info += $"Мощность передатчика: {transmitPower:0.#} dBm ({Math.Pow(10.0, (transmitPower - 30.0) / 10.0):0.#} Вт)\n";
            info += $"Луч: {(beamwidth >= 180f ? "всенаправленная" : beamwidth.ToString("0.#") + "°")}\n";
            info += $"Полоса канала: {bandwidthMHz * 1000.0:0.#} кГц\n";
            info += $"Питание при передаче науки: {PowerDrawKw * 1000.0:0.#} Вт ({PowerDrawKw:0.###} ед./с)\n\n";
            // Вдали аппарат снижает скорость: связь есть и там, где сигнала на полный канал уже не хватает
            string full;
            double fullRate = RadioMath.DataRateKbps(bandwidthMHz * 1e6, 100.0, out full);
            info += $"<b>Дальность связи с DSN:</b> {KSPUtil.PrintSI(MaxDirectRange(), "м")} (на {RadioMath.FormatRate(RadioMath.MIN_DATA_RATE_BPS / 1000.0)})\n";
            info += $"На {RadioMath.FormatRate(RadioMath.USEFUL_DATA_RATE_BPS / 1000.0)} - до {KSPUtil.PrintSI(RangeForCn0(RadioMath.Cn0ForRate(RadioMath.USEFUL_DATA_RATE_BPS)), "м")}, " +
                    $"на полной скорости (до {RadioMath.FormatRate(fullRate)}) - до {KSPUtil.PrintSI(RangeForCn0(FullRateCn0()), "м")}";
            return info;
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;

            updateTimer += Time.fixedDeltaTime;
            if (updateTimer >= 0.2f)
            {
                updateTimer = 0f;

                // CanComm() - стоковая проверка: модуль включен, антенна раскрыта и не сломана
                currentLink = RadioPhysics.CalculateBestLink(this);
                linkReady = true;
                UpdateScienceUi();
            }
        }

        // --- Передача науки: своя очередь (ScienceTransfer) вместо стоковой ---

        public ScienceSendMode SendMode { get { return scienceInParts ? ScienceSendMode.Parts : ScienceSendMode.Whole; } }

        public double PowerDrawKw
        {
            get { return (Math.Pow(10.0, (transmitPower - 30.0) / 10.0) / TX_EFFICIENCY + ELECTRONICS_W) / 1000.0; }
        }

        // Скорость передачи науки сейчас, мит/с
        public override float DataRate
        {
            get
            {
                double bps = vessel != null ? RadioCommNetwork.DownlinkRate(vessel) * ScienceRules.RateFactor : 0.0;
                return (float)Math.Max(bps / 1e6, 1e-9);
            }
        }

        // Игра выбирает передатчик по (1 / скорость) × (1 + расход): -1 даёт нашим антеннам ноль, и данные всегда идут в
        // очередь мода, а не стоковой антенне аппарата (та отправила бы их мимо расчёта связи)
        public override double DataResourceCost { get { return -1.0; } }

        // Дойдёт ли наука: стоковая проверка дальности по своей модели могла бы не совпасть с расчётом связи мода (особенно
        // через ретранслятор). Метод у игры запечатан, поэтому интерфейс реализован заново - решает тот же путь CommNet
        bool ICommAntenna.CanScienceTo(bool combined, double bPower, double sqrDistance)
        {
            return CanTransmit();
        }

        // Очередь принимает данные и во время передачи
        public override bool IsBusy() { return false; }

        // Кнопка «Передать» в окне эксперимента
        public override void TransmitData(List<ScienceData> dataQueue)
        {
            TransmitData(dataQueue, null);
        }

        public override void TransmitData(List<ScienceData> dataQueue, Callback callback)
        {
            if (dataQueue != null)
            {
                foreach (ScienceData data in dataQueue) ScienceTransfer.Enqueue(vessel, data, SendMode);
            }
            if (callback != null) callback();
        }

        // «Передать данные» в меню антенны: все данные аппарата. Приборы без повторного запуска после этого не работают
        // (как в стоке) - спросить
        public override void StartTransmission()
        {
            if (vessel == null) return;
            List<IScienceDataContainer> containers = vessel.FindPartModulesImplementing<IScienceDataContainer>();
            int count = 0, spent = 0;
            foreach (IScienceDataContainer c in containers)
            {
                int n = c.GetScienceCount();
                count += n;
                if (n > 0 && !c.IsRerunnable()) spent++;
            }
            if (count == 0)
            {
                ScreenMessages.PostScreenMessage("На борту нет научных данных", 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (spent == 0)
            {
                Collect(containers);
                return;
            }
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RealisticRadioTransmit",
                    $"Данных на борту: {count}. Приборов без повторного запуска: {spent} - после передачи они не будут работать, пока их не сбросит учёный.",
                    "Передать данные", HighLogic.UISkin,
                    new DialogGUIButton("Передать", () => Collect(containers)),
                    new DialogGUIButton("Отмена", () => { })),
                false, HighLogic.UISkin);
        }

        private void Collect(List<IScienceDataContainer> containers)
        {
            foreach (IScienceDataContainer c in containers)
            {
                ScienceData[] data = c.GetData();
                if (data == null) continue;
                foreach (ScienceData d in data)
                {
                    if (d == null) continue;
                    ScienceTransfer.Enqueue(vessel, d, SendMode);
                    c.DumpData(d);
                }
            }
        }

        // Стоковая «остановить передачу» - пауза очереди аппарата и продолжение
        public override void StopTransmission()
        {
            ScienceTransfer.Queue q = ScienceTransfer.QueueOf(vessel);
            if (q != null) ScienceTransfer.SetPaused(vessel, !q.paused);
            UpdateScienceUi();
        }

        // Стоковая «передавать неполные данные» - наша кнопка «целиком / по частям», одна на весь аппарат
        public override void TransmitIncompleteToggle()
        {
            bool parts = !scienceInParts;
            if (vessel != null)
            {
                foreach (ModuleRadioLink radio in vessel.FindPartModulesImplementing<ModuleRadioLink>())
                {
                    radio.scienceInParts = parts;
                    radio.UpdateScienceUi();
                }
            }
            else
            {
                scienceInParts = parts;
            }
            ScreenMessages.PostScreenMessage(parts ? "Наука по частям: каждая десятая часть начисляется, как только дошла"
                                                   : "Наука целиком: начисляется, когда дошёл весь файл", 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        public void UpdateScienceUi()
        {
            try
            {
                ScienceTransfer.Queue q = ScienceTransfer.QueueOf(vessel);
                BaseEvent start = Events["StartTransmission"];
                if (start != null) start.guiName = "Передать науку";
                BaseEvent stop = Events["StopTransmission"];
                if (stop != null)
                {
                    stop.active = stop.guiActive = q != null;
                    stop.guiName = q != null && q.paused ? "Продолжить передачу науки" : "Остановить передачу науки";
                }
                BaseEvent mode = Events["TransmitIncompleteToggle"];
                if (mode != null)
                {
                    mode.active = mode.guiActive = true;
                    mode.guiName = scienceInParts ? "Наука: по частям" : "Наука: целиком";
                }
                statusText = q == null ? "готов" : $"наука: {q.state}, осталось {ScienceTransfer.Mit(q.Left)}";
            }
            catch (Exception)
            {
                // События детали ещё не созданы
            }
        }

        // Дальность связи с DSN: дальше нет и самой медленной линии (10 бит/с)
        public double DirectRangeMeters { get { return MaxDirectRange(); } }

        // Дальность «рабочей» связи (1 кбит/с): для вкладки редактора и подсказок новичкам
        public double UsefulRangeMeters { get { return RangeForCn0(RadioMath.Cn0ForRate(RadioMath.USEFUL_DATA_RATE_BPS)); } }

        private double MaxDirectRange()
        {
            return RangeForCn0(RadioMath.MIN_LINK_CN0);
        }

        // Дальность, на которой C/N0 на станции, лучше всех слышащей эту частоту, падает до заданного (антенна наведена).
        // Расстояние игры: настоящая дальность, делённая на масштаб системы
        private double RangeForCn0(double cn0DbHz)
        {
            DsnStation station = DsnManager.BestFor(frequencyMHz);
            double minRxPower = RadioMath.NoiseDensityDbm(station.NoiseTemperatureAt(frequencyMHz)) + cn0DbHz;
            double maxPathLoss = transmitPower + antennaGain + station.NetGainAt(frequencyMHz) - minRxPower;
            return RadioScale.GameDistance(RadioMath.DistanceForLoss(maxPathLoss, frequencyMHz));
        }

        // C/N0, при котором самый медленный режим занимает весь канал: дальше скорость снижается
        private double FullRateCn0()
        {
            return RadioMath.LINK_THRESHOLD_SNR + 10.0 * Math.Log10(Math.Max(bandwidthMHz * 1e6, 1.0));
        }

        // Стоковый CommNet строит маршруты по "мощности" антенн. Связь с наземной станцией считает наша модель дальности
        // (RadioRangeModel): дальность = sqrt(P_антенны * RELAY_SCALE), то есть ровно наша дальность до DSN; связь
        // аппарат - аппарат - как в стоке, sqrt(P_a * P_b)
        private double CalculateEquivalentVanillaPower()
        {
            double range = MaxDirectRange();
            return range * range / RadioRangeModel.RELAY_SCALE;
        }

        // "Мощность" DSN высшего уровня из самой игры: моды масштаба (Sol, KSRSS через CustomBarnKit) её меняют
        public static double DsnPower()
        {
            try
            {
                if (GameVariables.Instance != null)
                {
                    double power = GameVariables.Instance.GetDSNRange(1f);
                    if (power > 0 && !double.IsNaN(power) && !double.IsInfinity(power)) return power;
                }
            }
            catch (Exception)
            {
                // Игра ещё загружается - берём стоковое значение
            }
            return VANILLA_DSN_POWER;
        }

        public override bool CanTransmit()
        {
            return HasUsableLink();
        }

        // Аппаратом можно управлять: данные идут вниз, а команды доходят до борта
        public bool HasUsableLink()
        {
            return currentLink.hasSignal && currentLink.uplink;
        }

        public static ModuleRadioLink FindBestRadio(Vessel v)
        {
            ModuleRadioLink best = null;
            foreach (var radio in v.FindPartModulesImplementing<ModuleRadioLink>())
            {
                if (best == null)
                {
                    best = radio;
                    continue;
                }

                bool usable = radio.HasUsableLink();
                bool bestUsable = best.HasUsableLink();
                if (usable != bestUsable)
                {
                    if (usable) best = radio;
                }
                // Лучшая - быстрее; на одной скорости - с большим запасом
                else if (radio.currentLink.speed > best.currentLink.speed ||
                         (radio.currentLink.speed == best.currentLink.speed && radio.currentLink.cn0 > best.currentLink.cn0))
                {
                    best = radio;
                }
            }
            return best;
        }
    }
}
