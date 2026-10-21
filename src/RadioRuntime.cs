// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using UnityEngine;

namespace RealisticRadio
{
    // Ядро в работе: раз в секунду ставит свою сеть CommNet (путь по скорости линий) - игра создаёт сеть заново
    // в каждой сцене и при смене настроек
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RadioRuntime : MonoBehaviour
    {
        private float commNetTimer;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        // Сцена с планетами и аппаратами готова (в полёте - когда есть активный аппарат)
        public static bool InScene
        {
            get
            {
                if (!HighLogic.LoadedSceneHasPlanetarium || FlightGlobals.Vessels == null || FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0) return false;
                return !HighLogic.LoadedSceneIsFlight || (FlightGlobals.ready && FlightGlobals.ActiveVessel != null);
            }
        }

        public void Update()
        {
            commNetTimer -= Time.unscaledDeltaTime;
            if (commNetTimer > 0f) return;
            commNetTimer = 1f;
            try
            {
                if (InScene)
                {
                    // Станции мода - «дома» CommNet, сеть - наша (линии решает расчёт мода)
                    StationHome.Ensure();
                    RadioCommNetwork.Install();
                    StationHome.EnsureInNetwork();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RealisticRadio] CommNet: {ex}");
                commNetTimer = 60f;
            }
        }
    }
}
