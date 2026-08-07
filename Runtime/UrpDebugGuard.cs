#if RENDER_PIPELINES_CORE_ON && UNITY_INPUT_SYSTEM
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Prevents URP DebugUpdater from polling Touch.activeTouches every frame.
    /// URP's DebugUpdater (created at AfterSceneLoad) calls EnhancedTouchSupport.Enable()
    /// and reads Touch.activeTouches for the 3-finger debug-menu gesture. Synthetic
    /// virtual-Touchscreen events corrupt EnhancedTouch's history state, causing a
    /// null deref SIGSEGV (see AGENTS.md Known Issues). Setting enableRuntimeUI=false
    /// before AfterSceneLoad stops DebugUpdater from ever being created.
    /// </summary>
    public static class UrpDebugGuard
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DisableUrpDebugUpdater()
        {
            // DebugManager.instance is a lazy singleton; accessing it here is safe
            // (no DebugUpdater exists yet at BeforeSceneLoad). The enableRuntimeUI
            // setter also calls DebugUpdater.SetEnabled(false), which is a no-op
            // when the updater doesn't exist yet, and prevents its AfterSceneLoad
            // creation (RuntimeInit checks enableRuntimeUI).
            if (DebugManager.instance != null)
                DebugManager.instance.enableRuntimeUI = false;
        }
    }
}
#endif
