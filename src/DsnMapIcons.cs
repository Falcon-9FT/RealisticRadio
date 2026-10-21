// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System.Collections.Generic;
using UnityEngine;

namespace RealisticRadio
{
    // Значки наземных станций DSN на карте и в центре слежения; название - при наведении мыши
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class DsnMapIcons : MonoBehaviour
    {
        private const string TEXTURE_PATH = "RealisticRadio/Textures/dsn_icon";
        private const double ICON_ALTITUDE = 250.0;
        // Планета, закрывающая станции, чуть меньше настоящей: рельеф и сглаживание сетки не прячут станцию на виду
        private const double OCCLUDER_SHARE = 0.998;

        private class StationIcon
        {
            public string name;
            public CelestialBody body;
            public double lat, lon;
        }

        private static readonly List<StationIcon> stations = new List<StationIcon>();
        private GUIStyle tooltipStyle;
        private GUIStyle shadowStyle;
        private GUIStyle fallbackIconStyle;
        private Texture2D dsnTexture;

        public void Start()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;

            dsnTexture = GameDatabase.Instance.GetTexture(TEXTURE_PATH, false);
            if (dsnTexture == null)
            {
                Debug.LogError($"[RealisticRadio] ОШИБКА: Текстура не найдена по пути GameData/{TEXTURE_PATH}.png!");
            }

            if (stations.Count > 0) return;

            DsnManager.LoadStations();
            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody body = DsnManager.BodyOf(st);
                if (body != null) stations.Add(new StationIcon { name = st.name, body = body, lat = st.lat, lon = st.lon });
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

            Vector3d camPos = cam.transform.position;

            foreach (StationIcon st in stations)
            {
                // Значок - ровно над станцией, без подъёма: иначе вблизи планеты значки из-за горизонта «взлетают» в космос
                Vector3d scaledBodyPos = ScaledSpace.LocalToScaledSpace(st.body.position);
                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(st.body.GetWorldSurfacePosition(st.lat, st.lon, ICON_ALTITUDE));
                double scaledRadius = st.body.Radius / ScaledSpace.ScaleFactor;

                double distToPlanet = (camPos - scaledBodyPos).magnitude;
                bool isCloseView = distToPlanet < scaledRadius * 3.0;
                if (distToPlanet > scaledRadius * 10.0) continue;

                Vector3 screenPos = cam.WorldToScreenPoint((Vector3)scaledPos);
                if (screenPos.z <= 0) continue;

                // Станция за горизонтом не рисуется: луч от камеры до неё проходит сквозь планету
                if (Hidden(camPos, scaledPos, scaledBodyPos, scaledRadius * OCCLUDER_SHARE)) continue;

                float iconSize = isCloseView ? 32f : 24f;

                float guiY = Screen.height - screenPos.y;
                Rect iconRect = new Rect(screenPos.x - iconSize / 2f, guiY - iconSize / 2f, iconSize, iconSize);

                if (dsnTexture != null)
                    GUI.DrawTexture(iconRect, dsnTexture);
                else
                    GUI.Label(iconRect, "📡", fallbackIconStyle);

                Rect hoverRect = new Rect(iconRect.x - 5f, iconRect.y - 5f, iconSize + 10f, iconSize + 10f);
                if (hoverRect.Contains(mousePos))
                {
                    GUI.Label(new Rect(screenPos.x + iconSize / 2f + 6f, guiY - 9f, 200f, 20f), st.name, shadowStyle);
                    GUI.Label(new Rect(screenPos.x + iconSize / 2f + 5f, guiY - 10f, 200f, 20f), st.name, tooltipStyle);
                }
            }
        }

        // Закрыта ли точка шаром: ближайшая к центру шара точка отрезка «камера - точка» лежит внутри него
        private static bool Hidden(Vector3d from, Vector3d point, Vector3d center, double radius)
        {
            Vector3d ray = point - from;
            double length2 = ray.sqrMagnitude;
            if (length2 <= 0) return false;
            double t = Vector3d.Dot(center - from, ray) / length2;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return (from + ray * t - center).sqrMagnitude < radius * radius;
        }
    }
}
