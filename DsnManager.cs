using System;
using System.Collections.Generic;
using UnityEngine;

namespace KerbalRadio
{
    public class DsnStation
    {
        public string name;
        public string bodyName;
        public double lat;
        public double lon;
        public double gain;
    }

    public static class DsnManager
    {
        public static List<DsnStation> Stations { get; private set; }

        public static void LoadStations()
        {
            if (Stations != null) return;

            Stations = new List<DsnStation>();
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALISTIC_RADIO_DSN");

            if (nodes != null)
            {
                foreach (ConfigNode node in nodes)
                {
                    foreach (ConfigNode stationNode in node.GetNodes("Station"))
                    {
                        DsnStation st = new DsnStation();
                        st.name = (stationNode.GetValue("name") ?? "Unknown").Trim();
                        st.bodyName = (stationNode.GetValue("body") ?? "Kerbin").Trim();
                        double.TryParse(stationNode.GetValue("lat"), out st.lat);
                        double.TryParse(stationNode.GetValue("lon"), out st.lon);

                        if (!double.TryParse(stationNode.GetValue("gain"), out st.gain))
                        {
                            st.gain = 74.0;
                        }
                        Stations.Add(st);
                    }
                }
            }

            if (Stations.Count == 0)
            {
                Stations.Add(new DsnStation { name = "Fallback", bodyName = "Kerbin", lat = 0, lon = -74.5, gain = 74.0 });
            }
        }
    }
}