using System;
using System.IO;
using UnityEngine;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Centralized logging utility for SimpleMCPBridge.
    /// All log output goes through here for consistent timestamp formatting.
    /// </summary>
    public static class DebugUtils
    {
        public static void Log(string msg)
        {
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        public static void LogWarning(string msg)
        {
            Debug.LogWarning($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        public static void LogError(string msg)
        {
            Debug.LogError($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        /// <summary>Append a timestamped line to a log file.</summary>
        public static void LogToFile(string filePath, string msg)
        {
            try
            {
                using (var writer = new StreamWriter(filePath, append: true, encoding: System.Text.Encoding.UTF8))
                {
                    writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
                }
            }
            catch { }
        }
    }
}

