using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.IO;
using UnityEngine;
using FFmpegUnityBind2;
using FFmpegUnityBind2.Components;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles gameplay recording via FFmpegUnityBind2 (FFmpegREC).
    /// Provides start/stop/status MCP tools for recording the scene from a dedicated Camera.
    ///
    /// Creates a hidden Camera that mirrors the Main Camera and captures frames
    /// via FFmpegREC (OnPostRender). Audio (system) is optional.
    /// Encoding runs asynchronously — poll recording.status for completion.
    /// </summary>
    [MCPToolClass]
    public class RecordingHandler
    {
        // Session state
        private static GameObject _recorderGo;
        private static Camera _recorderCamera;
        private static FFmpegREC _recorder;
        private static string _outputPath;
        private static DateTime _startTime;
        private static string _lastExportPath;
        private static string _lastError;
        private static bool _encodingSuccess;
        private static bool _encodingFailed;

        private const string VIDEO_RECORD_DIR = "VideoRecord";
        private const int MAX_KEEP_FILES = 5;

        /// <summary>
        /// Start recording from a dedicated Camera mirroring the Main Camera.
        /// Only works in Play Mode.
        /// </summary>
        [MCPTool(MCPMethodConst.START_RECORDING,
            "Start recording gameplay from a dedicated Camera matching the Main Camera. " +
            "Only works in Play Mode. Optional params: width, height (0=current resolution), " +
            "fps (default 30), enableAudio (default false), quality (CRF 0-51, default 23). " +
            "Returns success, outputPath, width, height, fps.")]
        public static string StartRecording(string paramsJson)
        {
            if (!Application.isPlaying)
                return ErrorJson("Recording is only supported in Play Mode. Enter Play Mode first.");

            if (_recorder != null && _recorder.State == FFmpegRECState.Capturing)
                return ErrorJson("Already recording. Call recording.stop first.");

            // Clean up any stale objects first (e.g. from previous Play Mode)
            Cleanup();

            var args = ParseJsonObject(paramsJson);
            int width = (int)GetOptionalInt(args, "width").GetValueOrDefault(0);
            int height = (int)GetOptionalInt(args, "height").GetValueOrDefault(0);
            int fps = (int)GetOptionalInt(args, "fps").GetValueOrDefault(30);
            bool enableAudio = args.TryGetValue("enableAudio", out var audioVal) && (audioVal is bool b ? b : (bool.TryParse(audioVal?.ToString(), out var r) && r));
            int quality = (int)GetOptionalInt(args, "quality").GetValueOrDefault(CRF.DEFAULT_QUALITY);

            try
            {
                // Build output path
                _outputPath = BuildOutputPath();
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath));

                _startTime = DateTime.UtcNow;
                _lastError = null;
                _encodingSuccess = false;
                _encodingFailed = false;

                // Create GameObject to hold recording components
                _recorderGo = new GameObject("MCP Recording Camera");
                // Position at main camera (or origin)
                if (Camera.main != null)
                {
                    _recorderGo.transform.SetPositionAndRotation(
                        Camera.main.transform.position,
                        Camera.main.transform.rotation
                    );
                }
                // Add Camera (required by FFmpegREC) — FFmpegREC will configure it in StartREC
                _recorderCamera = _recorderGo.AddComponent<Camera>();
                // Required audio components (required by [RequireComponent] on FFmpegREC)
                _recorderGo.AddComponent<RecMicAudio>();
                _recorderGo.AddComponent<RecSystemAudio>();

                // Configure FFmpegREC
                _recorder = _recorderGo.AddComponent<FFmpegREC>();
                if (width > 0)
                    _recorder.ResolutionWidth = width;
                if (height > 0)
                    _recorder.ResolutionHeight = height;
                _recorder.TargetFPS = fps;
                _recorder.AudioSource = enableAudio ? RecAudioSource.System : RecAudioSource.None;
                _recorder.Quality = quality;

                // Subscribe to completion events
                _recorder.OnSuccessEvent += OnRecordingSuccess;
                _recorder.OnFailEvent += OnRecordingFailed;

                // Start capturing
                _recorder.StartREC(_outputPath);

                DebugUtils.Log($"[Recording] Started: {_outputPath} ({fps}fps, audio={enableAudio})");

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("outputPath", JsonHelper.EscapeString(_outputPath)),
                    ("width", width > 0 ? width.ToString() : "auto"),
                    ("height", height > 0 ? height.ToString() : "auto"),
                    ("fps", fps.ToString()),
                    ("enableAudio", enableAudio ? "true" : "false")
                );
            }
            catch (Exception ex)
            {
                Cleanup();
                _lastError = ex.Message;
                return ErrorJson($"Failed to start recording: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop recording and encode captured frames to MP4.
        /// Encoding runs async — poll recording.status for completion.
        /// </summary>
        [MCPTool(MCPMethodConst.STOP_RECORDING,
            "Stop recording and encode to MP4. " +
            "Returns immediately with status='encoding'. " +
            "Call recording.status to poll for completion and get the output file path.")]
        public static string StopRecording(string paramsJson)
        {
            // Idle with a completed export
            if (_recorder == null || _recorder.State == FFmpegRECState.Idle)
            {
                if (_encodingSuccess && !string.IsNullOrEmpty(_lastExportPath))
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("status", "\"completed\""),
                        ("filePath", JsonHelper.EscapeString(_lastExportPath))
                    );

                return ErrorJson("No active recording.");
            }

            // Already encoding
            if (_recorder.State == FFmpegRECState.Processing)
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("status", "\"encoding\""),
                    ("message", JsonHelper.EscapeString("Already encoding previous recording."))
                );

            // State is Capturing — stop it
            _recorder.StopREC();
            DebugUtils.Log("[Recording] Stopped, encoding to MP4...");

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("status", "\"encoding\""),
                ("message", JsonHelper.EscapeString("Recording stopped. Encoding to MP4..."))
            );
        }

        /// <summary>
        /// Get current recording/encoding status.
        /// States: idle | recording | encoding | completed | error
        /// </summary>
        [MCPTool(MCPMethodConst.GET_RECORDING_STATUS,
            "Get current recording status. Returns state (idle/recording/encoding/completed/error), " +
            "elapsed seconds, encoding progress (0-1), and output file path when complete.")]
        public static string GetStatus(string paramsJson)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            // Stale state: recording objects survive Play Mode exit
            if (_recorderGo != null && !Application.isPlaying)
            {
                DebugUtils.Log("[Recording] Cleaning up stale recording objects from previous Play Mode.");
                Cleanup();
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"idle\"")
                );
            }

            // Currently capturing frames
            if (_recorder != null && _recorder.State == FFmpegRECState.Capturing)
            {
                float elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "true"),
                    ("state", "\"recording\""),
                    ("elapsedSeconds", elapsed.ToString("F1", invariant))
                );
            }

            // Encoding in progress
            if (_recorder != null && _recorder.State == FFmpegRECState.Processing)
            {
                float progress = _recorder.WritingProgress;
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"encoding\""),
                    ("progress", progress.ToString("F2", invariant))
                );
            }

            // Encoding completed (detected via OnSuccessEvent)
            if (_encodingSuccess && !string.IsNullOrEmpty(_outputPath))
            {
                _lastExportPath = _outputPath;
                _encodingSuccess = false; // report once
                Cleanup();

                DebugUtils.Log($"[Recording] Exported: {_lastExportPath}");

                var dir = Path.GetDirectoryName(_lastExportPath);
                if (!string.IsNullOrEmpty(dir))
                    CleanupOldRecordings(dir, MAX_KEEP_FILES);

                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"completed\""),
                    ("filePath", JsonHelper.EscapeString(_lastExportPath)),
                    ("message", JsonHelper.EscapeString("Recording exported successfully."))
                );
            }

            // Encoding failed
            if (_encodingFailed)
            {
                _encodingFailed = false; // report once
                Cleanup();

                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"error\""),
                    ("error", JsonHelper.EscapeString(_lastError ?? "Encoding failed"))
                );
            }

            // Idle with previous export
            if (_lastExportPath != null)
            {
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"idle\""),
                    ("lastExportPath", JsonHelper.EscapeString(_lastExportPath))
                );
            }

            // Sticky error
            if (!string.IsNullOrEmpty(_lastError))
            {
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", "\"error\""),
                    ("error", JsonHelper.EscapeString(_lastError))
                );
            }

            return JsonHelper.BuildJsonObject(
                ("isRecording", "false"),
                ("state", "\"idle\"")
            );
        }

        // ─── Event callbacks from FFmpegREC ───────────────────────────────

        private static void OnRecordingSuccess(long executionId, FFmpegCallbacksHandlerBase handler)
        {
            _encodingSuccess = true;
            DebugUtils.Log($"[Recording] Encoding succeeded (execution: {executionId}). Output: {_outputPath}");
        }

        private static void OnRecordingFailed(long executionId, FFmpegCallbacksHandlerBase handler)
        {
            _encodingFailed = true;
            _lastError = $"FFmpeg encoding failed (execution: {executionId})";
            DebugUtils.LogError($"[Recording] {_lastError}");
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        private static string BuildOutputPath()
        {
            string dir;
#if UNITY_EDITOR || UNITY_STANDALONE
            dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), VIDEO_RECORD_DIR);
#else
            dir = Path.Combine(Application.temporaryCachePath, VIDEO_RECORD_DIR);
#endif
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            return Path.Combine(dir, $"recording_{timestamp}.mp4");
        }

        private static void Cleanup()
        {
            if (_recorder != null)
            {
                _recorder.OnSuccessEvent -= OnRecordingSuccess;
                _recorder.OnFailEvent -= OnRecordingFailed;
                _recorder = null;
            }

            // Disable + null RT on camera before destroying so it doesn't
            // render green (SolidColor) to the GameView on the next frame.
            if (_recorderCamera != null)
            {
                _recorderCamera.enabled = false;
                _recorderCamera.targetTexture = null;
                _recorderCamera = null;
            }

            if (_recorderGo != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_recorderGo);
                else
                    UnityEngine.Object.DestroyImmediate(_recorderGo);
                _recorderGo = null;
            }
        }

        private static void CleanupOldRecordings(string directory, int keepCount)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                var files = Directory.GetFiles(directory, "*.mp4");
                if (files.Length <= keepCount) return;

                Array.Sort(files);
                for (int i = 0; i < files.Length - keepCount; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
        }
    }
}
