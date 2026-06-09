using System;
using UnityEngine;

namespace KerbalRadio
{
    public class LinkResult
    {
        public bool hasSignal = false;
        public bool isOcculted = true;
        public string dsnName = "";
        public Vector3d dsnPos = Vector3d.zero;
        public double ping = 0;
        public double snr = -999;
        public double speed = 0;
        public double pointingError = 0;
        public int hopCount = 0;
        public string debugLog = "";
    }

    public class ModuleRadioLink : ModuleDataTransmitter
    {
        [KSPField(isPersistant = false)] public float antennaGain = 2.0f;
        [KSPField(isPersistant = false)] public float frequencyMHz = 137.0f;
        [KSPField(isPersistant = false)] public float transmitPower = 30.0f;
        [KSPField(isPersistant = false)] public float beamwidth = 360.0f;
        [KSPField(isPersistant = false)] public float bandwidthMHz = 1.0f;
        [KSPField(isPersistant = false)] public bool isRelay = false;
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Realistic Mode"), UI_Toggle(enabledText = "ON", disabledText = "OFF")]
        public bool isRealisticMode = true;

        private RadioUI ui;
        private float updateTimer = 0f;
        public LinkResult currentLink = new LinkResult();
        private const string LOCK_ID = "RealisticRadio_Lock";

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            try
            {
                this.antennaType = isRelay ? AntennaType.RELAY : AntennaType.DIRECT;

                this.antennaPower = CalculateEquivalentVanillaPower();

                DsnManager.LoadStations();
                ui = new RadioUI(this);
                ui.InitMapLine();
            }
            catch (Exception) { }
        }
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            try
            {
                this.antennaPower = CalculateEquivalentVanillaPower();
                this.antennaType = isRelay ? AntennaType.RELAY : AntennaType.DIRECT;
            }
            catch (Exception) { }
        }
        public override string GetInfo()
        {
            double kspPower = CalculateEquivalentVanillaPower();
            string powerString = KSPUtil.PrintSI(kspPower, " ");

            string info = $"<b>Пися попа</b>\n\n";
            info += $"Тип: {(isRelay ? "Ретранслятор" : "Прямая связь")}\n";
            info += $"Усиление: {antennaGain} dBi\n";
            info += $"Частота: {frequencyMHz} MHz\n";
            info += $"Мощность передатчика: {transmitPower} dBm\n";
            info += $"Ширина канала: {bandwidthMHz} MHz\n";
            info += $"Угол луча: {beamwidth}°\n\n";
            info += $"<b>Игровой рейтинг (antennaPower):</b>\n{powerString}";

            return info;
        }

        public void OnDestroy()
        {
            if (ui != null) ui.DestroyMapLine();
            InputLockManager.RemoveControlLock(LOCK_ID);
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;

            updateTimer += Time.fixedDeltaTime;
            if (updateTimer >= 0.2f)
            {
                updateTimer = 0f;
                LinkResult directLink = RadioPhysics.CalculateDirectLink(vessel.GetWorldPos3D(), part.transform.up, this);
                if (directLink != null && directLink.hasSignal && directLink.snr > -10.0)
                {
                    currentLink = directLink;
                    currentLink.hopCount = 0;
                }
                else
                {
                    currentLink = RadioPhysics.CalculateBestLink(vessel.GetWorldPos3D(), part.transform.up, this);
                }
                bool isActive = vessel.isActiveVessel;
                bool hasSignal = (!isRealisticMode || !isActive) ? currentLink.hasSignal : (currentLink.hasSignal && currentLink.snr > -10.0);

                if (hasSignal)
                {
                    this.packetInterval = 0.5f;
                    this.packetSize = (float)(currentLink.speed / 1000.0) * 0.5f;
                    this.packetResourceCost = 10.0f;
                }
                else
                {
                    this.packetSize = 0f;
                }

                if (isActive && vessel.GetCrewCount() == 0)
                {
                    bool anyAntennaWorks = false;
                    foreach (var radio in vessel.FindPartModulesImplementing<ModuleRadioLink>())
                    {
                        if (radio.currentLink != null && radio.currentLink.hasSignal && radio.currentLink.snr > -10.0)
                        {
                            anyAntennaWorks = true;
                            break;
                        }
                    }

                    if (anyAntennaWorks)
                    {
                        InputLockManager.RemoveControlLock(LOCK_ID);
                    }
                    else
                    {
                        bool hardMode = HighLogic.CurrentGame.Parameters.CustomParams<CommNet.CommNetParams>().requireSignalForControl;
                        ControlTypes locks = ControlTypes.YAW | ControlTypes.PITCH | ControlTypes.ROLL;
                        if (hardMode) locks |= ControlTypes.THROTTLE;

                        InputLockManager.SetControlLock(locks, LOCK_ID);
                    }
                }
            }
        }
        private double CalculateEquivalentVanillaPower()
        {
            double bandwidthHz = bandwidthMHz * 1e6;
            double systemNoiseDbm = -185.0 + 10.0 * Math.Log10(bandwidthHz);
            double minRxPower = systemNoiseDbm - 10.0;
            double dsnGain = 74.0;
            double maxFSPL = transmitPower + antennaGain + dsnGain - minRxPower;
            double log10Distance = (maxFSPL - 20.0 * Math.Log10(frequencyMHz) - 32.44) / 20.0;
            double maxDistanceKm = Math.Pow(10, log10Distance);
            double maxDistanceMeters = maxDistanceKm * 1000.0;
            double vanillaDsnPower = 250000000000d;
            double equivalentPower = (maxDistanceMeters * maxDistanceMeters) / vanillaDsnPower;
            return equivalentPower;
        }
        public override bool CanTransmit()
        {
            if (!isRealisticMode || !vessel.isActiveVessel) return currentLink.hasSignal;
            return currentLink.hasSignal && currentLink.snr > -10.0;
        }

        public void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || ui == null) return;
            ui.UpdateMapLine(vessel.GetWorldPos3D(), currentLink);
        }
    }
}