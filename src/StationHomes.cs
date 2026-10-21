// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using CommNet;
using UnityEngine;

namespace RealisticRadio
{
    // Наземная станция мода как «дом» CommNet: путь связи аппарата с антеннами мода заканчивается на настоящей станции из
    // DsnStations.cfg, а не на штатной станции KSP. Объект висит на теле и вращается вместе с ним
    public class StationHome : CommNetHome
    {
        // Узел CommNet -> станция: по нему сеть узнаёт наши станции
        public static readonly Dictionary<CommNode, StationHome> ByNode = new Dictionary<CommNode, StationHome>();

        public DsnStation Station { get; private set; }
        public CelestialBody Body { get { return body; } }
        public CommNode Node { get { return comm; } }

        public void Configure(DsnStation station, CelestialBody onBody)
        {
            Station = station;
            body = onBody;
            lat = station.lat;
            lon = station.lon;
            alt = station.altitude;
            nodeName = "RealisticRadio " + station.name;
            displaynodeName = station.name;
            isKSC = false;
            isPermanent = true;
            nodeTransform = transform;
            transform.SetPositionAndRotation(onBody.GetWorldSurfacePosition(lat, lon, alt), Quaternion.identity);
            transform.SetParent(onBody.transform, true);
        }

        protected override void CreateNode()
        {
            if (comm == null)
            {
                comm = new CommNode(nodeTransform)
                {
                    OnNetworkPreUpdate = OnNetworkPreUpdate,
                    isHome = true,
                    isControlSource = true,
                    isControlSourceMultiHop = true
                };
            }
            comm.name = nodeName;
            comm.displayName = displaynodeName;
            // Мощность для стоковой модели дальности (подсказки, карта); есть ли линия, решает расчёт мода
            comm.antennaRelay.Update(ModuleRadioLink.DsnPower(), GameVariables.Instance.GetDSNRangeCurve(), false);
            ByNode[comm] = this;
        }

        protected override void OnDestroy()
        {
            if (comm != null) ByNode.Remove(comm);
            base.OnDestroy();
        }

        // --- Создание: раз на игру, когда тела загружены (тела и их объекты живут всю игру) ---

        private static readonly List<StationHome> homes = new List<StationHome>();

        public static void Ensure()
        {
            homes.RemoveAll(h => h == null);
            if (homes.Count > 0) return;
            DsnManager.LoadStations();
            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody body = DsnManager.BodyOf(st);
                if (body == null) continue;
                try
                {
                    var go = new GameObject("RealisticRadio station " + st.name);
                    var home = go.AddComponent<StationHome>();
                    home.Configure(st, body);
                    homes.Add(home);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RealisticRadio] Станция «{st.name}» не стала узлом CommNet: {ex}");
                }
            }
            if (homes.Count > 0) Debug.Log($"[RealisticRadio] CommNet: станций мода {homes.Count} - связь аппаратов с антеннами мода идёт через них");
        }

        // Узлы станций - в текущей сети CommNet: сеть создаётся заново в каждой сцене, а станция, созданная в момент её
        // запуска, могла не успеть на событие «сеть готова»
        public static void EnsureInNetwork()
        {
            CommNetNetwork network = CommNetNetwork.Instance;
            if (network == null || !CommNetNetwork.Initialized || network.CommNet == null) return;
            foreach (StationHome home in homes)
            {
                if (home == null || home.comm == null) continue;
                if (!network.CommNet.Contains(home.comm)) CommNetNetwork.Add(home.comm);
            }
        }
    }
}
