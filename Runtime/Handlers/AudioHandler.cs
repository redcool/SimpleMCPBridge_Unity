using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles audio-related MCP operations (query audio sources, etc.).
    /// </summary>
    [MCPToolClass]
    public class AudioHandler
    {
        [MCPTool(MCPMethodConst.AUDIO_GET_SOURCES,
            "List all currently playing AudioSources in the scene, plus listener info. " +
            "Params: 'maxResults' (int, optional, default 50, max 200). " +
            "Returns each source with: clipName, volume, isPlaying, time, length, loop, " +
            "spatialBlend, position, distanceFromListener, path, instanceId. " +
            "Also returns the AudioListener position for reference.")]
        public static string GetSources(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var maxResults = (int)GetOptionalInt(args, "maxResults").GetValueOrDefault(50);
                maxResults = Mathf.Clamp(maxResults, 1, 200);

                // Find AudioListener position
                var listener = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
                Vector3 listenerPos = Vector3.zero;
                bool hasListener = listener != null && listener.Length > 0;
                if (hasListener)
                    listenerPos = listener[0].transform.position;

                // Find all AudioSources
                var allSources = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
                var sourceJsons = new List<string>();
                int counted = 0;

                foreach (var source in allSources)
                {
                    if (counted >= maxResults) break;

                    // Skip truly silent sources (not playing AND no clip)
                    if (!source.isPlaying && source.clip == null)
                        continue;

                    var clipName = source.clip != null ? source.clip.name : "(none)";
                    var pos = source.transform.position;
                    var path = GetObjectPath(source.gameObject);

                    float distanceFromListener = -1f;
                    if (hasListener && source.spatialBlend > 0f)
                    {
                        distanceFromListener = Vector3.Distance(listenerPos, pos);
                    }

                    sourceJsons.Add(JsonHelper.BuildJsonObject(
                        ("clipName", JsonHelper.EscapeString(clipName)),
                        ("volume", source.volume.ToString("G", CultureInfo.InvariantCulture)),
                        ("isPlaying", JsonHelper.BoolJson(source.isPlaying)),
                        ("time", source.time.ToString("G", CultureInfo.InvariantCulture)),
                        ("length", source.clip != null
                            ? source.clip.length.ToString("G", CultureInfo.InvariantCulture)
                            : "0"),
                        ("loop", JsonHelper.BoolJson(source.loop)),
                        ("spatialBlend", source.spatialBlend.ToString("G", CultureInfo.InvariantCulture)),
                        ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                        ("distanceFromListener", distanceFromListener.ToString("G", CultureInfo.InvariantCulture)),
                        ("path", JsonHelper.EscapeString(path)),
                        ("instanceId", source.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                        ("mute", JsonHelper.BoolJson(source.mute)),
                        ("pitch", source.pitch.ToString("G", CultureInfo.InvariantCulture))
                    ));

                    counted++;
                }

                // Build listener info
                string listenerJson = "null";
                if (hasListener)
                {
                    var lPos = listener[0].transform.position;
                    listenerJson = JsonHelper.BuildJsonObject(
                        ("position", JsonHelper.FloatArrayJson(new[] { lPos.x, lPos.y, lPos.z })),
                        ("enabled", JsonHelper.BoolJson(listener[0].enabled))
                    );
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", counted.ToString(CultureInfo.InvariantCulture)),
                    ("total", allSources.Length.ToString(CultureInfo.InvariantCulture)),
                    ("listener", listenerJson),
                    ("sources", JsonHelper.BuildJsonArray(sourceJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"audio.get_sources failed: {ex.Message}");
            }
        }
    }
}
