#if INSTANT_REPLAY_ON
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
    ///
    /// recording.* tools use [MCPTool(Platform = MCPToolPlatforms.Android | iOS | Standalone | Editor,
    /// RequirePlayMode = true)] to register on device builds AND Editor (Play Mode only), so they
    /// are never visible in Edit Mode where there is no camera rendering to capture.
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

        private static readonly object _stateLock = new();

        private const int MAX_KEEP_FILES = 5;

        // ─── recording.start ──────────────────────────────────────────────
        // Registers on device builds (Android/iOS/Standalone) AND Editor (Windows/macOS,
        // InstantReplay supports Media Foundation / Video Toolbox natively). RequirePlayMode
        // keeps it out of Edit Mode where there is no camera rendering to capture.
        [MCPTool(MCPMethodConst.START_RECORDING,
            "Start recording screen capture via CyberAgent InstantReplay (OS-native encoding). "
            + "Only works in Play Mode. "
            + "Params: width/height (default 1280x720), fps (default 30), "
            + "enableAudio (default false), quality 1-100 (default 50). "
            + "Returns success, outputPath, width, height, fps.",
            Platform = MCPToolPlatforms.Android | MCPToolPlatforms.iOS | MCPToolPlatforms.Standalone | MCPToolPlatforms.Editor, RequirePlayMode = true)]
        public static string StartRecording(string paramsJson)
        {
            lock (_stateLock)
            {
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

                    int bitrate = RecordingTools.QualityToBitrate(quality);

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

                    _outputPath = RecordingTools.BuildOutputPath();
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
        }

        // ─── recording.stop ────────────────────────────────────────────────
        [MCPTool(MCPMethodConst.STOP_RECORDING,
            "Stop recording and finalize the MP4. "
            + "Returns immediately with status='encoding'. "
            + "Poll recording.status for completion and output file path.",
            Platform = MCPToolPlatforms.Android | MCPToolPlatforms.iOS | MCPToolPlatforms.Standalone | MCPToolPlatforms.Editor, RequirePlayMode = true)]
        public static string StopRecording(string paramsJson)
        {
            lock (_stateLock)
            {
                // Already completed (previous export still stored)
                if (_session == null)
                {
                    if (!string.IsNullOrEmpty(_lastExportPath))
                        return RecordingTools.BuildCompletedJson(_lastExportPath);
                    if (!string.IsNullOrEmpty(_lastError))
                        return RecordingTools.BuildErrorJson(_lastError);
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
        }

        // ─── recording.status ──────────────────────────────────────────────

        [MCPTool(MCPMethodConst.GET_RECORDING_STATUS,
            "Get current recording/encoding status. "
            + "States: idle | recording | encoding | completed | error.",
            Platform = MCPToolPlatforms.Android | MCPToolPlatforms.iOS | MCPToolPlatforms.Standalone | MCPToolPlatforms.Editor, RequirePlayMode = true)]
        public static string GetStatus(string paramsJson)
        {
            lock (_stateLock)
            {
                var invariant = System.Globalization.CultureInfo.InvariantCulture;

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
                            RecordingTools.CleanupOldRecordings(dir, MAX_KEEP_FILES);

                        DebugUtils.Log($"[Recording] Exported: {_lastExportPath}");
                        return RecordingTools.BuildCompletedJson(_lastExportPath);
                    }

                    return RecordingTools.BuildErrorJson(_lastError ?? "Recording failed with unknown error.");
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
        }

        // ─── Async export ──────────────────────────────────────────────────

        private const int EXPORT_TIMEOUT_SECONDS = 20;

        /// <summary>
        /// Completes the UnboundedRecordingSession and waits for the MP4 to be finalized.
        /// Runs on the Unity main thread (via default SynchronizationContext capture).
        /// Has a built-in timeout to prevent hanging if the encoder stalls.
        /// Stores result in _lastExportPath / _lastError for polling by GetStatus.
        /// </summary>
        private static async Task ExportAsync()
        {
            // Capture session + outputPath under lock before any async work
            UnboundedRecordingSession session;
            string outputPath;
            lock (_stateLock)
            {
                session = _session;
                outputPath = _outputPath;
            }

            try
            {
                // CompleteAsync returns non-generic ValueTask (no .AsTask() in .NET Standard 2.1).
                // Bridge to Task via TaskCompletionSource for use with Task.WhenAny.
                var vt = session.CompleteAsync();
                var tcs = new TaskCompletionSource<bool>();
                vt.GetAwaiter().OnCompleted(() =>
                {
                    try
                    {
                        vt.GetAwaiter().GetResult(); // rethrow any exception
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
                var completeTask = tcs.Task;

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(EXPORT_TIMEOUT_SECONDS));
                var completed = await Task.WhenAny(completeTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    lock (_stateLock)
                    {
                        _lastExportPath = null;
                        _lastError = "Recording export timed out. The encoder may have stalled. Call recording.reset to recover.";
                    }
                    CleanupSession();
                    DebugUtils.LogError("[Recording] Recording export timed out. The encoder may have stalled.");
                    return;
                }

                // Propagate any exception from CompleteAsync
                await completeTask;
                lock (_stateLock)
                {
                    _lastExportPath = outputPath;
                    _lastError = null;
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _lastExportPath = null;
                    _lastError = $"Recording export failed: {ex.Message}";
                }
                CleanupSession();
                DebugUtils.LogError($"[Recording] Recording export failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Force-reset recording state. Use if export times out or gets stuck.
        /// </summary>
        [MCPTool("recording.reset", "Force-reset the recording system. Use if encoding gets stuck or times out. Cleans up all session state.",
            Platform = MCPToolPlatforms.Android | MCPToolPlatforms.iOS | MCPToolPlatforms.Standalone | MCPToolPlatforms.Editor, RequirePlayMode = true)]
        public static string ResetRecording(string paramsJson)
        {
            lock (_stateLock)
            {
                CleanupSession();
                ResetState();
                DebugUtils.Log("[Recording] Force reset by user request.");
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("message", JsonHelper.EscapeString("Recording state reset. Previous session abandoned."))
                );
            }
        }

        // ─── State reset (required for enter Play Mode without domain reload) ─

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            lock (_stateLock)
            {
                ResetState();
                // _session can't survive domain reload — Unity destroys it
                _session = null;
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        public static void ResetState()
        {
            lock (_stateLock)
            {
                _outputPath = null;
                _startTime = default;
                _lastExportPath = null;
                _lastError = null;
                _exportTask = null;
            }
        }

        public static void CleanupSession()
        {
            lock (_stateLock)
            {
                _session?.Dispose();
                _session = null;
            }
        }

    }
}
#endif

