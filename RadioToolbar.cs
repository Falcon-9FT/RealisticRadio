using System;
using UnityEngine;
using KSP.UI.Screens;

namespace KerbalRadio
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RadioToolbar : MonoBehaviour
    {
        private ApplicationLauncherButton appButton = null;
        private bool isWindowOpen = false;
        private Rect windowRect = new Rect(Screen.width - 350, 100, 300, 200);

        public void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnDestroyed);
        }

        public void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnDestroyed);
            RemoveButton();
        }

        private void OnReady()
        {
            if (appButton == null)
            {
                Texture2D icon = GameDatabase.Instance.GetTexture("RealisticRadio/icon", false);
                if (icon == null) icon = new Texture2D(38, 38);

                appButton = ApplicationLauncher.Instance.AddModApplication(
                    ToggleOn,
                    ToggleOff,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    icon
                );
            }
        }

        private void OnDestroyed()
        {
            RemoveButton();
        }

        private void RemoveButton()
        {
            if (appButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(appButton);
                appButton = null;
            }
        }

        private void ToggleOn()
        {
            isWindowOpen = true;
        }

        private void ToggleOff()
        {
            isWindowOpen = false;
        }

        private void OnGUI()
        {
            if (isWindowOpen && HighLogic.LoadedSceneIsFlight)
            {
                GUI.skin = HighLogic.Skin;
                windowRect = GUILayout.Window(133742, windowRect, DrawGlobalWindow, "Realistic Radio");
            }
        }

        private void DrawGlobalWindow(int windowID)
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;

            if (activeVessel != null)
            {
                ModuleRadioLink activeRadio = activeVessel.FindPartModuleImplementing<ModuleRadioLink>();

                if (activeRadio != null)
                {
                  GUILayout.Label($"Status: {(activeRadio.currentLink.hasSignal ? "<color=#00FF00>CONNECTED</color>" : "<color=#FF0000>NO SIGNAL</color>")}");
                    if (activeRadio.currentLink.hasSignal)
                    {
                        if (activeRadio.currentLink.hopCount > 1)
                        {
                            GUILayout.Label($"Route: <color=#FFA500>RELAY ({activeRadio.currentLink.hopCount} hops)</color>");
                            GUILayout.Label($"Target: {activeRadio.currentLink.dsnName}");
                        }
                        else
                        {
                            GUILayout.Label($"Route: <color=#00FFFF>DIRECT</color>");
                            GUILayout.Label($"Station: {activeRadio.currentLink.dsnName}");
                        }
                    }
                    GUILayout.Label($"Speed: {activeRadio.currentLink.speed:F1} kbps");
                    GUILayout.Label($"Ping: {activeRadio.currentLink.ping:F3} s");
                    GUILayout.Label($"SNR: {activeRadio.currentLink.snr:F1} dB");
                    GUILayout.Label($"Error: {activeRadio.currentLink.pointingError:F2}°");
                }
                else
                {
                    GUILayout.Label("No active radio module on vessel.");
                }
            }
            else
            {
                GUILayout.Label("No active vessel.");
            }
            GUI.DragWindow();
        }
    }
}