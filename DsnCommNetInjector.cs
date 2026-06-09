using System;
using System.Collections.Generic;
using UnityEngine;
using CommNet;
using System.IO;

namespace KerbalRadio
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class DsnCommNetInjector : MonoBehaviour
    {
        private class CustomHome
        {
            public CommNode node;
            public CelestialBody body;
            public double lat, lon, alt;
            public bool addedToNetwork = false;
        }

        private static List<CustomHome> customHomes = new List<CustomHome>();
        private GUIStyle tooltipStyle;
        private GUIStyle shadowStyle;
        private GUIStyle fallbackIconStyle;
        private Texture2D dsnTexture;
        private const string texturePath = "RealisticRadio/Textures/dsn_icon";
        private static bool stationsLoaded = false;
        public void Start()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;

            if (customHomes.Count > 0) return;

            DsnManager.LoadStations();

            dsnTexture = GameDatabase.Instance.GetTexture(texturePath, false);

            if (dsnTexture == null)
            {
                Debug.LogError($"[KerbalRadio] ОШИБКА: Текстура не найдена по пути GameData/{texturePath}.png!");
            }

            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == st.bodyName);
                if (body != null)
                {
                    CustomHome ch = new CustomHome();
                    ch.body = body;
                    ch.lat = st.lat;
                    ch.lon = st.lon;
                    ch.alt = 250.0;

                    ch.node = new CommNode();
                    ch.node.name = st.name;
                    ch.node.isHome = true;
                    ch.node.isControlSource = true;
                    ch.node.isControlSourceMultiHop = true;

                    ch.node.antennaTransmit.power = 50000000000000d;
                    ch.node.antennaRelay.power = 50000000000000d;

                    customHomes.Add(ch);
                }
            }
        }

        public void Update()
        {
            foreach (var ch in customHomes)
            {
                if (ch.body == null) continue;
                Vector3d worldPos = ch.body.GetWorldSurfacePosition(ch.lat, ch.lon, ch.alt);
              ch.node.precisePosition = worldPos;
            }
        }
        public void OnGUI()
        {
            if (!MapView.MapIsEnabled || PlanetariumCamera.Camera == null) return;
            GUI.depth = -10;

            if (tooltipStyle == null)
            {
                tooltipStyle = new GUIStyle(GUI.skin.label);
                tooltipStyle.normal.textColor = Color.white;
                tooltipStyle.fontSize = 13;
                tooltipStyle.fontStyle = FontStyle.Bold;
                tooltipStyle.alignment = TextAnchor.MiddleLeft;

                shadowStyle = new GUIStyle(tooltipStyle);
                shadowStyle.normal.textColor = Color.black;

                fallbackIconStyle = new GUIStyle(GUI.skin.label);
                fallbackIconStyle.fontSize = 24;
                fallbackIconStyle.alignment = TextAnchor.MiddleCenter;
            }

            Camera cam = PlanetariumCamera.Camera;
            Vector2 mousePos = Event.current.mousePosition;

            float scaledPlanetRadius = (float)(FlightGlobals.GetHomeBody().Radius / ScaledSpace.ScaleFactor);

            foreach (var ch in customHomes)
            {
                if (ch.node.precisePosition == Vector3d.zero) continue;

                Vector3d worldPos = ch.node.precisePosition;
                Vector3 scaledBodyPos = (Vector3)ScaledSpace.LocalToScaledSpace(ch.body.position);

                float distToPlanet = Vector3.Distance(cam.transform.position, scaledBodyPos);
                bool isCloseView = distToPlanet < scaledPlanetRadius * 3f;
                float maxVisibleDistance = scaledPlanetRadius * 10f;

                if (distToPlanet > maxVisibleDistance) continue;

                Vector3d surfaceNormal = (worldPos - ch.body.position).normalized;
                double elevationBoost = isCloseView ? ch.body.Radius * 0.05 : 0;
                Vector3d elevatedPos = worldPos + surfaceNormal * elevationBoost;

                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(elevatedPos);
                Vector3 screenPos = cam.WorldToScreenPoint((Vector3)scaledPos);
                if (screenPos.z <= 0) continue;
                Vector3 normal3 = ((Vector3)scaledPos - scaledBodyPos).normalized;
                Vector3 viewDir3 = ((Vector3)scaledPos - cam.transform.position).normalized;
                float dot = Vector3.Dot(normal3, viewDir3);
                float dotThreshold = isCloseView ? 0.3f : 0.1f;
                if (dot > dotThreshold) continue;

                float iconSize = isCloseView ? 32f : 24f;

                float guiY = Screen.height - screenPos.y;
                Rect iconRect = new Rect(screenPos.x - iconSize / 2f,
                                         guiY - iconSize / 2f,
                                         iconSize, iconSize);

                if (dsnTexture != null)
                    GUI.DrawTexture(iconRect, dsnTexture);
                else
                    GUI.Label(iconRect, "📡", fallbackIconStyle);

                Rect hoverRect = new Rect(iconRect.x - 5f, iconRect.y - 5f,
                                          iconSize + 10f, iconSize + 10f);
                if (hoverRect.Contains(mousePos))
                {
                    GUI.Label(new Rect(screenPos.x + iconSize / 2f + 6f, guiY - 9f, 200f, 20f),
                              ch.node.name, shadowStyle);
                    GUI.Label(new Rect(screenPos.x + iconSize / 2f + 5f, guiY - 10f, 200f, 20f),
                              ch.node.name, tooltipStyle);
                }
            }
        }

        public void OnDestroy()
        {
            if (CommNetNetwork.Instance != null && CommNetNetwork.Instance.CommNet != null)
            {
                foreach (var ch in customHomes)
                {
                    if (ch.addedToNetwork)
                    {
                        CommNetNetwork.Instance.CommNet.Remove(ch.node);
                    }
                }
            }
        }
    }
}