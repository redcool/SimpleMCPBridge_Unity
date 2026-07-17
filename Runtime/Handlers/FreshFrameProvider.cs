#if INSTANT_REPLAY_ON
using System;
using System.Collections;
using InstantReplay;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// A frame provider that creates a NEW RenderTexture every frame
    /// to avoid the reuse race condition in ScreenshotFrameProvider.
    /// Each frame gets its own texture — no shared state.
    /// Uses a hidden MonoBehaviour with WaitForEndOfFrame coroutine.
    /// </summary>
    public class FreshFrameProvider : IFrameProvider
    {
        private class EndOfFrameHost : MonoBehaviour
        {
            public event Action OnEndOfFrame;

            private IEnumerator Start()
            {
                var wait = new WaitForEndOfFrame();
                while (true)
                {
                    yield return wait;
                    OnEndOfFrame?.Invoke();
                }
            }

            private void OnDestroy()
            {
                OnEndOfFrame = null;
            }
        }

        private static readonly object HostLock = new();
        private static EndOfFrameHost _host;

        private RenderTexture _previousRt;
        private bool _subscribed;

        public FreshFrameProvider()
        {
            EnsureHost();
            EndOfFrameHost host;
            lock (HostLock) { host = _host; }
            if (host != null)
            {
                host.OnEndOfFrame += OnEndOfFrame;
                _subscribed = true;
            }
        }

        public event IFrameProvider.ProvideFrame OnFrameProvided;

        public void Dispose()
        {
            if (_subscribed)
            {
                EndOfFrameHost host;
                lock (HostLock) { host = _host; }
                if (host != null)
                    host.OnEndOfFrame -= OnEndOfFrame;
                _subscribed = false;
            }

            if (_previousRt != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_previousRt);
                else
                    UnityEngine.Object.DestroyImmediate(_previousRt);
                _previousRt = null;
            }
        }

        private static void EnsureHost()
        {
            lock (HostLock)
            {
                if (_host != null) return;
                var go = new GameObject("FreshFrameProvider Host");
                go.hideFlags = HideFlags.HideAndDontSave;
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(go);
                _host = go.AddComponent<EndOfFrameHost>();
            }
        }

        /// <summary>
        /// Reset static host on domain reload (if domain reload is enabled).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            lock (HostLock) { _host = null; }
        }

        private void OnEndOfFrame()
        {
            var time = Time.unscaledTimeAsDouble;
            var width = Screen.width;
            var height = Screen.height;

            if (width <= 0 || height <= 0) return;

            // Create a FRESH RenderTexture every frame
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.name = "FreshFrameProvider Frame";
            rt.Create();

            // Capture current screen into it
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);

            // Provide the frame — this texture goes to the pipeline
            OnFrameProvided?.Invoke(new IFrameProvider.Frame(rt, time, SystemInfo.graphicsUVStartsAtTop));

            // Dispose the PREVIOUS frame's render texture (no longer in use by pipeline)
            // Current one stays alive for the pipeline to process
            if (_previousRt != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_previousRt);
                else
                    UnityEngine.Object.DestroyImmediate(_previousRt);
            }
            _previousRt = rt;
        }
    }
}
#endif
