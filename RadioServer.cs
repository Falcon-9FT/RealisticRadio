using System;
using System.Collections;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace KerbalRadio
{
    [Serializable]
    public class SdrConfig
    {
        public string type;
        public float frequency;
        public string modulation;
        public float bandwidth;
        public string protocol;
    }

    public class RadioBehavior : WebSocketBehavior
    {
        protected override void OnOpen()
        {
            Debug.Log("ЦУП подключился к приемнику.");
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.IsText)
            {
                try
                {
                    SdrConfig config = JsonUtility.FromJson<SdrConfig>(e.Data);
                    if (config != null && config.type == "TUNE")
                    {
                        RadioServer.TunedFrequency = config.frequency;
                        RadioServer.ActiveProtocol = config.protocol;
                        Debug.Log($"ЦУП настроился на {config.frequency} МГц | Протокол: {config.protocol}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"Ошибка чтения команды: {ex.Message}");
                }
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RadioServer : MonoBehaviour
    {
        private WebSocketServer wss;
        private Texture2D lineTex;
        public static float TunedFrequency = 137.100f;
        public static string ActiveProtocol = "APT";
        private float satelliteFrequency = 137.100f;

        private float scanTimer = 0f;
        private float timeBetweenLines = 0.5f;

        public void Start()
        {
            try
            {
                wss = new WebSocketServer(8025);
                wss.AddWebSocketService<RadioBehavior>("/radio");
                wss.Start();
            }
        }

        public void Update()
        {
            if (wss != null && wss.IsListening)
            {
                scanTimer += Time.deltaTime;
                if (scanTimer >= timeBetweenLines)
                {
                    scanTimer = 0f;
                    if (Mathf.Abs(TunedFrequency - satelliteFrequency) < 0.01f)
                    {
                        StartCoroutine(ScanAndSendLine());
                    }
                }
            }
        }

        private IEnumerator ScanAndSendLine()
        {
            yield return new WaitForEndOfFrame();
            Camera mainCam = FlightCamera.fetch?.mainCamera;
            if (mainCam == null) yield break;
            int width = 909;
            int height = 1;

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24);
            GameObject spyObj = new GameObject("SpyScanner");
            Camera spyCam = spyObj.AddComponent<Camera>();
            spyCam.CopyFrom(mainCam);
            spyCam.targetTexture = rt;
            spyCam.Render();
            RenderTexture.active = rt;

            if (lineTex == null || lineTex.width != width || lineTex.height != height)
            {
                lineTex = new Texture2D(width, height, TextureFormat.RGB24, false);
            }

            lineTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            lineTex.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            Destroy(spyObj);
            Color[] pixels = lineTex.GetPixels();
            byte[] basebandSignal = new byte[width];

            for (int i = 0; i < width; i++)
            {
                float grayscale = (pixels[i].r * 0.3f) + (pixels[i].g * 0.59f) + (pixels[i].b * 0.11f);
                basebandSignal[i] = (byte)(grayscale * 255f);
            }
            if (wss != null)
            {
                wss.WebSocketServices["/radio"].Sessions.Broadcast(basebandSignal);
            }
        }

        public void OnDestroy()
        {
            if (wss != null)
            {
                wss.Stop();
                wss = null;
            }
            if (lineTex != null) Destroy(lineTex);
        }
    }
}