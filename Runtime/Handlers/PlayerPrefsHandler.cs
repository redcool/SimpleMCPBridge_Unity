using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// PlayerPrefs inspection/editing tools: playerprefs.get_all / get / set / delete.
    /// Platform: All (works in Editor, Play Mode, and built players).
    ///
    /// NOTE: Unity exposes NO PlayerPrefs key-enumeration API, so playerprefs.get_all can
    /// only report keys this session has seen (playerprefs.set / playerprefs.get add to an
    /// in-memory registry). Keys written by other code before the bridge connected are not
    /// enumerable — read them with playerprefs.get (with an explicit keyType if needed).
    /// </summary>
    [MCPToolClass]
    public class PlayerPrefsHandler
    {
        /// <summary>Keys this session has seen via playerprefs.set/get (Unity has no enumeration API).</summary>
        private static readonly HashSet<string> s_knownKeys = new HashSet<string>();

        // ══════════════════════════════════════════════════════════════
        //  playerprefs.get_all
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.ALL_PLAYERPREFS_GET,
            "Get ALL PlayerPrefs keys this session knows about, with type-sniffed values. " +
            "Each entry: { key, type: 'int'|'float'|'string', value }. " +
            "NOTE: Unity exposes no key enumeration API — this returns keys seen via " +
            "playerprefs.set/get in the current session (persisted keys written elsewhere " +
            "are not enumerable; read those with playerprefs.get).")]
        public static string GetAll(string paramsJson)
        {
            try
            {
                _ = ParseJsonObject(paramsJson ?? "{}");

                var entries = new List<string>();
                foreach (var key in s_knownKeys)
                {
                    if (!PlayerPrefs.HasKey(key)) continue; // deleted since tracked
                    var (type, valueJson) = SniffValue(key);
                    entries.Add(JsonHelper.BuildJsonObject(
                        ("key", JsonHelper.EscapeString(key)),
                        ("type", JsonHelper.EscapeString(type)),
                        ("value", valueJson)
                    ));
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", entries.Count.ToString(CultureInfo.InvariantCulture)),
                    ("entries", JsonHelper.BuildJsonArray(entries.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"PlayerPrefs get_all failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  playerprefs.get
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PLAYERPREFS_GET,
            "Get a single PlayerPrefs value. Params: key (required), keyType (optional: " +
            "'int'|'float'|'string' — auto-detected when omitted). Returns { success, key, type, value }.")]
        [MCPParam("key", Type = "string", Required = true, Description = "PlayerPrefs key")]
        [MCPParam("keyType", Type = "string", Description = "'int' | 'float' | 'string' — auto-detected when omitted")]
        public static string Get(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var key = GetRequiredString(args, "key");
                if (!PlayerPrefs.HasKey(key))
                    return ErrorJson($"Key '{key}' not found in PlayerPrefs");
                s_knownKeys.Add(key);

                var keyType = GetString(args, "keyType");
                string type;
                string valueJson;
                if (keyType == "int")
                {
                    type = "int";
                    valueJson = PlayerPrefs.GetInt(key).ToString(CultureInfo.InvariantCulture);
                }
                else if (keyType == "float")
                {
                    type = "float";
                    valueJson = PlayerPrefs.GetFloat(key).ToString("G", CultureInfo.InvariantCulture);
                }
                else if (keyType == "string")
                {
                    type = "string";
                    valueJson = JsonHelper.EscapeString(PlayerPrefs.GetString(key));
                }
                else
                {
                    (type, valueJson) = SniffValue(key);
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("key", JsonHelper.EscapeString(key)),
                    ("type", JsonHelper.EscapeString(type)),
                    ("value", valueJson)
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"PlayerPrefs get failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  playerprefs.set
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PLAYERPREFS_SET,
            "Set a PlayerPrefs value. Params: key (required), value (required), valueType " +
            "(optional: 'int'|'float'|'string' — auto-detected from the JSON value type), " +
            "save (bool, default true → PlayerPrefs.Save()). Returns { success, key, type }.")]
        [MCPParam("key", Type = "string", Required = true, Description = "PlayerPrefs key")]
        [MCPParam("value", Type = "string", Required = true, Description = "Value to store (parsed per valueType)")]
        [MCPParam("valueType", Type = "string", Description = "'int' | 'float' | 'string' — auto-detected from JSON type when omitted")]
        [MCPParam("save", Type = "boolean", Description = "Call PlayerPrefs.Save() (default true)")]
        public static string Set(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var key = GetRequiredString(args, "key");
                var rawValue = GetRawValue(args, "value");
                if (rawValue == null)
                    return ErrorJson("Missing required parameter: 'value'");

                var valueType = GetString(args, "valueType");
                if (string.IsNullOrEmpty(valueType))
                {
                    // Auto-detect from the parsed JSON type (ParseJsonValue: ints → int, floats → double, else string).
                    if (rawValue is int) valueType = "int";
                    else if (rawValue is double) valueType = "float";
                    else valueType = "string";
                }

                if (valueType == "int")
                    PlayerPrefs.SetInt(key, Convert.ToInt32(rawValue, CultureInfo.InvariantCulture));
                else if (valueType == "float")
                    PlayerPrefs.SetFloat(key, Convert.ToSingle(rawValue, CultureInfo.InvariantCulture));
                else if (valueType == "string")
                    PlayerPrefs.SetString(key, rawValue.ToString() ?? "");
                else
                    return ErrorJson($"Unsupported valueType '{valueType}' (supported: 'int', 'float', 'string')");

                s_knownKeys.Add(key);
                var save = GetOptionalBool(args, "save") ?? true;
                if (save) PlayerPrefs.Save();

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("key", JsonHelper.EscapeString(key)),
                    ("type", JsonHelper.EscapeString(valueType))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"PlayerPrefs set failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  playerprefs.delete
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PLAYERPREFS_DELETE,
            "Delete a PlayerPrefs key. Params: key (required). NO delete_all in v1 — delete " +
            "keys individually. Returns { success, key }.")]
        [MCPParam("key", Type = "string", Required = true, Description = "PlayerPrefs key to delete")]
        public static string Delete(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var key = GetRequiredString(args, "key");
                PlayerPrefs.DeleteKey(key);
                s_knownKeys.Remove(key);
                PlayerPrefs.Save();
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("key", JsonHelper.EscapeString(key))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"PlayerPrefs delete failed: {ex.Message}");
            }
        }

        // ── Internal helpers ──

        /// <summary>
        /// Type-sniff a PlayerPrefs key using sentinel defaults (int.MinValue / float.MinValue / null).
        /// Order matters: int → float → string. If a stored value happens to equal a sentinel the
        /// sniff may misreport — pass an explicit keyType to playerprefs.get for exact reads.
        /// </summary>
        private static (string type, string valueJson) SniffValue(string key)
        {
            var intVal = PlayerPrefs.GetInt(key, int.MinValue);
            if (intVal != int.MinValue)
                return ("int", intVal.ToString(CultureInfo.InvariantCulture));

            var floatVal = PlayerPrefs.GetFloat(key, float.MinValue);
            if (floatVal != float.MinValue)
                return ("float", floatVal.ToString("G", CultureInfo.InvariantCulture));

            var strVal = PlayerPrefs.GetString(key, null);
            return ("string", JsonHelper.EscapeString(strVal ?? ""));
        }
    }
}
