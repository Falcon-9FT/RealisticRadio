// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System.Collections.Generic;
using UnityEngine;

namespace RealisticRadio
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RadioControlLock : MonoBehaviour
    {
        private const string LOCK_ID = "RealisticRadio_Lock";
        private const float CHECK_INTERVAL = 0.2f;

        // Без связи отключается только ручное управление: вращение, поступательное движение на RCS, руль и газ ровера.
        // SAS и его режимы (проград, на цель, на манёвр...), газ двигателей и ступени продолжают работать
        private const ControlTypes LOCKED_CONTROLS = ControlTypes.YAW | ControlTypes.PITCH | ControlTypes.ROLL
            | ControlTypes.LINEAR | ControlTypes.WHEEL_STEER | ControlTypes.WHEEL_THROTTLE;

        private Vessel lastVessel = null;
        private bool isLocked = false;
        private float checkTimer = 0f;

        // Теряется ли без связи управление: только на сложном уровне и в своих настройках, если там включено «Требовать сигнал
        // для управления» (как в стоке). На лёгком, нормальном и среднем управление остаётся - без связи нельзя только
        // передавать науку
        public static bool SignalRequired()
        {
            GameParameters parameters = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Parameters : null;
            if (parameters == null) return false;
            if (parameters.preset == GameParameters.Preset.Hard) return true;
            if (parameters.preset != GameParameters.Preset.Custom) return false;
            CommNet.CommNetParams commNet = parameters.CustomParams<CommNet.CommNetParams>();
            return commNet != null && commNet.requireSignalForControl;
        }

        public void Start()
        {
            InputLockManager.RemoveControlLock(LOCK_ID);
        }

        public void OnDestroy()
        {
            InputLockManager.RemoveControlLock(LOCK_ID);
        }

        public void Update()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;

            // Блокировка глобальная: при смене аппарата снимаем её сразу, иначе она достанется новому аппарату
            if (vessel != lastVessel)
            {
                lastVessel = vessel;
                Unlock();
                scienceLost = false;
                checkTimer = CHECK_INTERVAL;
            }

            checkTimer += Time.deltaTime;
            if (checkTimer < CHECK_INTERVAL) return;
            checkTimer = 0f;

            UpdateLock(vessel);
        }

        private void UpdateLock(Vessel vessel)
        {
            if (vessel == null || vessel.GetCrewCount() > 0 || !CommNet.CommNetScenario.CommNetEnabled)
            {
                Unlock();
                return;
            }

            // Лёгкий, нормальный и средний уровни: управление остаётся, только сказать, что науку не передать
            if (!SignalRequired())
            {
                Unlock();
                NoticeScience(vessel);
                return;
            }

            // Аппарат без антенн мода - по стоковым правилам CommNet
            List<ModuleRadioLink> radios = vessel.FindPartModulesImplementing<ModuleRadioLink>();
            if (radios.Count == 0 || vessel.connection == null)
            {
                Unlock();
                return;
            }

            // Одна правда о связи: путь CommNet. Его линии для антенн мода решает расчёт мода (в обе стороны - команды
            // должны доходить), а при сложенных антеннах мода путь держит встроенная антенна - ракета на старте не теряет
            // управление
            if (vessel.connection.IsConnected)
            {
                Unlock();
                return;
            }

            // Пока антенны не посчитали линию хотя бы раз, решение о блокировке не принимаем
            foreach (ModuleRadioLink radio in radios)
            {
                if (!radio.linkReady) return;
            }

            Lock(vessel);
        }

        private bool scienceLost;

        // Связь пропала у аппарата с антеннами мода - сообщить один раз (и когда вернулась)
        private void NoticeScience(Vessel vessel)
        {
            List<ModuleRadioLink> radios = vessel.FindPartModulesImplementing<ModuleRadioLink>();
            if (radios.Count == 0 || vessel.connection == null || !radios.TrueForAll(r => r.linkReady)) return;
            bool lost = !vessel.connection.IsConnected;
            if (lost == scienceLost) return;
            scienceLost = lost;
            ScreenMessages.PostScreenMessage(lost ? "Связь потеряна: научные данные не передать, управление остаётся"
                                                  : "Связь восстановлена", 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void Lock(Vessel vessel)
        {
            bool justLost = !isLocked;
            if (justLost)
            {
                InputLockManager.SetControlLock(LOCKED_CONTROLS, LOCK_ID);
                isLocked = true;
                ScreenMessages.PostScreenMessage("Связь потеряна! Ручное управление отключено: доступны SAS и газ", 4f, ScreenMessageStyle.UPPER_CENTER);
            }

            // Safe Mode: пока связи нет, SAS держится включённым - управлять можно только его режимами
            if (vessel.ActionGroups != null && !vessel.ActionGroups[KSPActionGroup.SAS] &&
                vessel.Autopilot != null && vessel.Autopilot.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist))
            {
                vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            }
        }

        private void Unlock()
        {
            if (!isLocked) return;
            InputLockManager.RemoveControlLock(LOCK_ID);
            isLocked = false;
        }
    }
}
