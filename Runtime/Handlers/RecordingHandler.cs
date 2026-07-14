using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;
using InstantReplay;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles gameplay recording via CyberAgent InstantReplay.
    /// Uses ScreenshotFrameProvider (screen capture) and UnboundedRecordingSession
    /// to produce MP4 files with OS-native hardware encoding (MediaCodec / Video Toolbox / Media Foundation).
    ///
    /// Encoding runs asynchronously — poll recording.status for completion.
    /// No external ffmpeg binaries required on any platform.
    /// </summary>
    [MCPToolClass]
    public class RecordingHandler
    {
        // ─── Session state ────────────────────────────────────────────────
        private static UnboundedRecordingSession _session;
        private static string _outputPath;
        private static DateTime _startTime;

        // ─── Export state (async) ─────────────────────────────────────────
        private static Task _exportTask;
        private static string _lastExportPath;
        private static string _lastError;

        private const string VIDEO_RECORD_DIR = "VideoRecord";
        private const int MAX_KEEP_FILES = 5;

        // ─── recording.start ──────────────────────────────────────────────

        [MCPTool(MCPMethodConst.START_RECORDING,
            "Start recording screen capture via CyberAgent InstantReplay (OS-native encoding). "
            + "Only works in Play Mode. "
            + "Params: width/height (default 1280x720), fps (default 30), "
            + "enableAudio (default false), quality 1-100 (default 50). "
            + "Returns success, outputPath, width, height, fps.")]
        public static string StartRecording(string paramsJson)
        {
            if (!Application.isPlaying)
                return ErrorJson("Recording is only supported in Play Mode. Enter Play Mode first.");

            if (_session != null)
                return ErrorJson("Already recording. Call recording.stop first.");

            // Clean up stale state (e.g. from previous Play Mode without domain reload)
            ResetState();

            var args = ParseJsonObject(paramsJson);
            int width = (int)GetOptionalInt(args, "width").GetValueOrDefault(1280);
            int height = (int)GetOptionalInt(args, "height").GetValueOrDefault(720);
            int fps = (int)GetOptionalInt(args, "fps").GetValueOrDefault(30);
            bool enableAudio = args.TryGetValue("enableAudio", out var audioVal) &&
                (audioVal is bool b ? b : (bool.TryParse(audioVal?.ToString(), out var r) && r));
            int quality = (int)GetOptionalInt(args, "quality").GetValueOrDefault(50);

            try
            {
                // Ensure even resolution (encoder requirement for most codecs)
                width = Mathf.Clamp(width % 2 == 0 ? width : width + 1, 320, 3840);
                height = Mathf.Clamp(height % 2 == 0 ? height : height + 1, 240, 2160);

                int bitrate = QualityToBitrate(quality);

                var options = new RealtimeEncodingOptions
                {
                    VideoOptions = new VideoEncoderOptions
                    {
                        Width = (uint)Mathf.Clamp(width, 320, 3840),
                        Height = (uint)Mathf.Clamp(height, 240, 2160),
                        FpsHint = (uint)Mathf.Clamp(fps, 1, 120),
                        Bitrate = (uint)bitrate
                    },
                    AudioOptions = new AudioEncoderOptions
                    {
                        SampleRate = 44100,
                        Channels = 2,
                        Bitrate = 128000
                    },
                    FixedFrameRate = Mathf.Clamp(fps, 1, 120),
                    VideoInputQueueSize = 5,
                    AudioInputQueueSizeSeconds = 1.0,
                    MaxMemoryUsageBytesForCompressedFrames = 20L * 1024 * 1024,
                    ForceReadback = false, // FreshFrameProvider gives unique textures per frame — no race
                };

                _outputPath = BuildOutputPath();
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);

                // Use FreshFrameProvider instead of ScreenshotFrameProvider to avoid
                // RenderTexture reuse race — each frame gets its own texture.
                _session = new UnboundedRecordingSession(
                    _outputPath, options,
                    frameProvider: new FreshFrameProvider(),
                    disposeFrameProvider: true
                );

                _startTime = DateTime.UtcNow;
                _lastError = null;
                _lastExportPath = null;
                _exportTask = null;

                DebugUtils.Log($"[Recording] Started: {_outputPath} ({width}x{height}, {fps}fps, audio={enableAudio}, quality={quality})");

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("outputPath", JsonHelper.EscapeString(_outputPath)),
                    ("width", width.ToString()),
                    ("height", height.ToString()),
                    ("fps", fps.ToString()),
                    ("enableAudio", enableAudio ? "true" : "false")
                );
            }
            catch (Exception ex)
            {
                CleanupSession();
                _lastError = ex.Message;
                return ErrorJson($"Failed to start recording: {ex.Message}");
            }
        }

        // ─── recording.stop ────────────────────────────────────────────────

        [MCPTool(MCPMethodConst.STOP_RECORDING,
            "Stop recording and finalize the MP4. "
            + "Returns immediately with status='encoding'. "
            + "Poll recording.status for completion and output file path.")]
        public static string StopRecording(string paramsJson)
        {
            // Already completed (previous export still stored)
            if (_session == null)
            {
                if (!string.IsNullOrEmpty(_lastExportPath))
                    return BuildCompletedJson(_lastExportPath);
                if (!string.IsNullOrEmpty(_lastError))
                    return BuildErrorJson(_lastError);
                return ErrorJson("No active recording.");
            }

            // Already in export
            if (_exportTask != null)
            {
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("status", JsonHelper.EscapeString("encoding")),
                    ("message", JsonHelper.EscapeString("Already encoding previous recording."))
                );
            }

            // Start async export (fire-and-forget, poll via status)
            _exportTask = ExportAsync();

            DebugUtils.Log("[Recording] Stopped, encoding to MP4...");
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("status", JsonHelper.EscapeString("encoding")),
                ("message", JsonHelper.EscapeString("Recording stopped. Encoding to MP4..."))
            );
        }

        // ─── recording.status ──────────────────────────────────────────────

        [MCPTool(MCPMethodConst.GET_RECORDING_STATUS,
            "Get current recording/encoding status. "
            + "States: idle | recording | encoding | completed | error.")]
        public static string GetStatus(string paramsJson)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            // Stale state after exiting Play Mode (no domain reload)
            if (!Application.isPlaying && _session != null)
            {
                DebugUtils.Log("[Recording] Cleaning up stale session from previous Play Mode.");
                CleanupSession();
                ResetState();
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", JsonHelper.EscapeString("idle"))
                );
            }

            // Currently recording
            if (_session != null && _exportTask == null)
            {
                float elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "true"),
                    ("state", JsonHelper.EscapeString("recording")),
                    ("elapsedSeconds", elapsed.ToString("F1", invariant))
                );
            }

            // Export in progress
            if (_exportTask != null && !_exportTask.IsCompleted)
            {
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", JsonHelper.EscapeString("encoding"))
                );
            }

            // Export just completed — consume result
            if (_exportTask != null && _exportTask.IsCompleted)
            {
                _exportTask = null;
                CleanupSession();

                if (!string.IsNullOrEmpty(_lastExportPath))
                {
                    var dir = Path.GetDirectoryName(_lastExportPath);
                    if (!string.IsNullOrEmpty(dir))
                        CleanupOldRecordings(dir, MAX_KEEP_FILES);

                    DebugUtils.Log($"[Recording] Exported: {_lastExportPath}");
                    return BuildCompletedJson(_lastExportPath);
                }

                return BuildErrorJson(_lastError ?? "Recording failed with unknown error.");
            }

            // Idle with previous export
            if (!string.IsNullOrEmpty(_lastExportPath))
            {
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", JsonHelper.EscapeString("idle")),
                    ("lastExportPath", JsonHelper.EscapeString(_lastExportPath))
                );
            }

            // Sticky error
            if (!string.IsNullOrEmpty(_lastError))
            {
                return JsonHelper.BuildJsonObject(
                    ("isRecording", "false"),
                    ("state", JsonHelper.EscapeString("error")),
                    ("error", JsonHelper.EscapeString(_lastError))
                );
            }

            return JsonHelper.BuildJsonObject(
                ("isRecording", "false"),
                ("state", JsonHelper.EscapeString("idle"))
            );
        }

        // ─── Async export ──────────────────────────────────────────────────

        /// <summary>
        /// Completes the UnboundedRecordingSession and waits for the MP4 to be finalized.
        /// Runs on the Unity main thread (via default SynchronizationContext capture).
        /// Stores result in _lastExportPath / _lastError for polling by GetStatus.
        /// </summary>
        private static async Task ExportAsync()
        {
            try
            {
                await _session.CompleteAsync();
                _lastExportPath = _outputPath;
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = $"Recording export failed: {ex.Message}";
                _lastExportPath = null;
                DebugUtils.LogError($"[Recording] {_lastError}");
            }
        }

        // ─── State reset (required for enter Play Mode without domain reload) ─

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            ResetState();
            // _session can't survive domain reload — Unity destroys it
            _session = null;
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private static void ResetState()
        {
            _outputPath = null;
            _startTime = default;
            _lastExportPath = null;
            _lastError = null;
            _exportTask = null;
        }

        private static void CleanupSession()
        {
            _session?.Dispose();
            _session = null;
        }

        private static string BuildOutputPath()
        {
            string dir;
#if UNITY_EDITOR || UNITY_STANDALONE
            dir = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, VIDEO_RECORD_DIR);
#else
            dir = Path.Combine(Application.temporaryCachePath, VIDEO_RECORD_DIR);
#endif
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            return Path.Combine(dir, $"recording_{timestamp}.mp4");
        }

        private static string BuildCompletedJson(string filePath)
        {
            return JsonHelper.BuildJsonObject(
                ("isRecording", "false"),
                ("state", JsonHelper.EscapeString("completed")),
                ("filePath", JsonHelper.EscapeString(filePath)),
                ("message", JsonHelper.EscapeString("Recording exported successfully."))
            );
        }

        private static string BuildErrorJson(string error)
        {
            return JsonHelper.BuildJsonObject(
                ("isRecording", "false"),
                ("state", JsonHelper.EscapeString("error")),
                ("error", JsonHelper.EscapeString(error))
            );
        }

        /// <summary>
        /// Maps quality (1-100) to video bitrate in bps.
        /// Lower quality → lower bitrate → smaller file but worse image.
        /// </summary>
        private static int QualityToBitrate(int quality)
        {
            quality = Mathf.Clamp(quality, 1, 100);
            if (quality >= 90) return 12000000; // 12 Mbps — very high
            if (quality >= 75) return 8000000;  //  8 Mbps — high
            if (quality >= 50) return 4000000;  //  4 Mbps — medium (default)
            if (quality >= 25) return 2000000;  //  2 Mbps — low
            return 1000000;                      //  1 Mbps — very low
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
