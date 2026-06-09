using System;
using UnityEngine;

namespace KerbalRadio
{
    public class RadioUI
    {
        private ModuleRadioLink module;
        private bool showUI = false;
        private Rect windowRect = new Rect(50, 50, 400, 200);
        private LineRenderer mapLine;

        public RadioUI(ModuleRadioLink module)
        {
            this.module = module;
        }

        public void Toggle()
        {
            showUI = !showUI;
        }

        public void InitMapLine()
        {
            try
            {
                GameObject lineObj = new GameObject("RadioMapLine_" + module.part.flightID);
                lineObj.layer = 9;
                mapLine = lineObj.AddComponent<LineRenderer>();
                Material unlitMat = new Material(Shader.Find("Particles/Standard Unlit"));
                mapLine.material = unlitMat;
                mapLine.startWidth = 0.02f;
                mapLine.endWidth = 0.02f;
                mapLine.positionCount = 2;
                mapLine.enabled = false;
            }
            catch (Exception) { }
        }

        public void DestroyMapLine()
        {
            if (mapLine != null)
            {
                UnityEngine.Object.Destroy(mapLine.gameObject);
            }
        }

        public void UpdateMapLine(Vector3d vesselPos, LinkResult link)
        {
            if (mapLine == null) return;

            if (MapView.MapIsEnabled && showUI)
            {
                mapLine.enabled = true;
                Vector3 scaledStart = ScaledSpace.LocalToScaledSpace(vesselPos);
                Vector3 scaledEnd = ScaledSpace.LocalToScaledSpace(link.dsnPos);

                mapLine.SetPosition(0, scaledStart);
                mapLine.SetPosition(1, scaledEnd);

                if (link.isOcculted || link.snr <= -10.0)
                {
                    mapLine.startColor = Color.red;
                    mapLine.endColor = Color.red;
                }
                else
                {
                    mapLine.startColor = Color.green;
                    mapLine.endColor = Color.green;
                }
            }
            else
            {
                mapLine.enabled = false;
            }
        }

        public void DrawWindow()
        {
            if (!showUI) return;
            windowRect = GUILayout.Window(module.GetInstanceID(), windowRect, WindowContent, "Realistic Radio");
        }

        private void WindowContent(int windowID)
        {
            GUILayout.BeginVertical();
            LinkResult link = module.currentLink;

            if (!link.hasSignal)
            {
                GUILayout.Label("<color=red>NO SIGNAL (OCCULTATION)</color>");
            }
            else if (link.snr <= -10.0)
            {
                GUILayout.Label("<color=orange>LINK LOST (TOO MUCH NOISE)</color>");
                GUILayout.Label($"SNR: {link.snr:F1} dB");
                GUILayout.Label($"Alignment Error: {link.pointingError:F1}°");
            }
            else
            {
                GUILayout.Label($"<color=#00FF00>LINK ESTABLISHED</color>");
                GUILayout.Label($"Station: {link.dsnName}");
                GUILayout.Label($"Ping: {link.ping:F3} s");
                GUILayout.Label($"SNR: {link.snr:F1} dB");
                GUILayout.Label($"Speed: {link.speed:F0} kbps");
                GUILayout.Label($"Alignment Error: <color=yellow>{link.pointingError:F1}°</color>");
            }

            GUILayout.Space(10);
            GUILayout.Label("--- DIAGNOSTICS ---");
            GUILayout.Label(link.debugLog);

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}