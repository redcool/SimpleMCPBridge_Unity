using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers.Game
{
    /// <summary>
    /// game.watch / game.get_delta 工具层 + TickWatch 入口。
    /// 状态与轮询引擎在 GameWatchService（单一职责）；此处只做参数解析与响应组装，wire API 与旧 GameHandler 一致。
    /// </summary>
    [MCPToolClass]
    public static class GameWatchHandler
    {
        [MCPTool(MCPMethodConst.GAME_WATCH,
            "Register signals to watch for value changes. " +
            "Params: 'signals' (array, required) — each signal has: " +
            "'id' (unique name), 'type' ('property'), " +
            "'path' (GameObject path), 'component' (component type name), " +
            "'property' (field/property name). " +
            "Returns current values as baselines. " +
            "Use game.get_delta to poll for changes since last check. " +
            "Example: {\"signals\":[{\"id\":\"hp\",\"type\":\"property\"," +
            "\"path\":\"Player\",\"component\":\"Health\",\"property\":\"currentHP\"}]}")]
        [MCPParam("signals", Type = "array", Required = true, Description = "Signals [{id, type, path, component, property}]")]
        public static string Watch(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                if (!args.TryGetValue("signals", out var signalsObj) || signalsObj is not string signalsJson)
                    return ErrorJson("Missing required parameter: 'signals' (array)");

                var signals = ParseJsonArrayOfObjects(signalsJson);
                if (signals.Count == 0)
                    return ErrorJson("'signals' array is empty");

                var registered = new List<string>();
                var errors = new List<string>();

                foreach (var sig in signals)
                {
                    var id = GetRequiredString(sig, "id");
                    var type = GetString(sig, "type", "property");

                    var config = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["type"] = type,
                    };

                    switch (type)
                    {
                        case "property":
                        {
                            config["path"] = GetRequiredString(sig, "path");
                            config["component"] = GetRequiredString(sig, "component");
                            config["property"] = GetRequiredString(sig, "property");
                            break;
                        }
                        default:
                            errors.Add($"{id}: unknown type '{type}' (supported: property)");
                            continue;
                    }

                    if (GameWatchService.Register(config, out var err) != null)
                        registered.Add(id);
                    else if (err != null)
                        errors.Add(err);
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("registered", JsonHelper.BuildJsonArray(
                        registered.Select(JsonHelper.EscapeString).ToArray())),
                    ("count", registered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("errors", JsonHelper.BuildJsonArray(
                        errors.Select(JsonHelper.EscapeString).ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"Watch failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.GAME_GET_DELTA,
            "Get value changes since last game.get_delta call. " +
            "Only returns signals whose value changed. " +
            "Each change includes: id, value (new), old (previous). " +
            "Use game.watch to register signals first. " +
            "Changes are detected continuously by the bridge (every ~167ms / 10 frames) " +
            "and returned from cache — this call never blocks on reflection reads. " +
            "Returns empty when nothing changed.")]
        public static string GetDelta(string paramsJson)
        {
            try
            {
                var changes = GameWatchService.DrainChanges();
                if (changes.Count == 0)
                {
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("changes", "[]"),
                        ("count", "0")
                    );
                }

                var hasErrors = changes.Any(c => c.Contains("\"error\""));
                return JsonHelper.BuildJsonObject(
                    ("success", hasErrors ? "false" : "true"),
                    ("changes", JsonHelper.BuildJsonArray(changes.ToArray())),
                    ("count", changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetDelta failed: {ex.Message}");
            }
        }

        /// <summary>主线程每帧轮询入口（MCPBridge.Update / InstanceUpdate 调用）。</summary>
        public static void TickWatch()
        {
            GameWatchService.Tick();
        }
    }
}// touch compile-gate
