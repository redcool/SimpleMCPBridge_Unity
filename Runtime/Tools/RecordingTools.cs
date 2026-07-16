using System;
using System.IO;
using SimpleMCPBridge.Runtime;
using UnityEngine;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Static utility methods for recording operations.
    /// Extracted from RecordingHandler to keep MCP tool methods focused on protocol logic.
    /// </summary>
    public static class RecordingTools
    {
        private const string VIDEO_RECORD_DIR = "VideoRecord";

        public static string BuildOutputPath()
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

        public static string BuildCompletedJson(string filePath)
        {
            return JsonHelper.BuildJsonObject(
                ("isRecording", "false"),
                ("state", JsonHelper.EscapeString("completed")),
                ("filePath", JsonHelper.EscapeString(filePath)),
                ("message", JsonHelper.EscapeString("Recording exported successfully."))
            );
        }

        public static string BuildErrorJson(string error)
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
        public static int QualityToBitrate(int quality)
        {
            quality = Mathf.Clamp(quality, 1, 100);
            if (quality >= 90) return 12000000; // 12 Mbps — very high
            if (quality >= 75) return 8000000;  //  8 Mbps — high
            if (quality >= 50) return 4000000;  //  4 Mbps — medium (default)
            if (quality >= 25) return 2000000;  //  2 Mbps — low
            return 1000000;                      //  1 Mbps — very low
        }

        public static void CleanupOldRecordings(string directory, int keepCount)
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
