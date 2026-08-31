using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Handlers.Game
{
    /// <summary>
    /// 从 GameHandler 抽离的 Watch/Delta 服务。
    /// 单一职责：信号注册、轮询缓存、增量读取。原 GameHandler 通过委派保持 API 不变。
    /// </summary>
    public static class GameWatchService
    {
        private static readonly Dictionary<string, Dictionary<string, object>> _watchConfigs = new();
        private static readonly Dictionary<string, string> _watchBaselines = new();
        private const int MaxWatchEntries = 100;
        private static readonly Dictionary<string, WatchPropertyCache> _watchPropertyCache = new();
        private const int MaxWatchPropertyCacheEntries = MaxWatchEntries * 2;
        private class WatchPropertyCache { public Component Component; public MemberInfo Member; }
        private static readonly List<string> _watchChanges = new();
        private const int WatchTickIntervalFrames = 10;
        private static int _watchFrameCounter;

        public static IReadOnlyDictionary<string, Dictionary<string, object>> Configs => _watchConfigs;
        public static int Count => _watchConfigs.Count;

        public static string Register(Dictionary<string, object> sig, out string error)
        {
            error = null;
            var id = sig["id"] as string;
            var type = sig.TryGetValue("type", out var t) ? t as string : "property";
            string baseline = "";
            string err = null;
            if (type == "property")
            {
                var path = sig["path"] as string;
                var comp = sig["component"] as string;
                var prop = sig["property"] as string;
                baseline = ReadPropertyValue(path, comp, prop, out err);
            }
            else err = $"unknown type '{type}'";
            if (err != null) { error = $"{id}: {err}"; return null; }
            _watchConfigs[id] = sig;
            _watchBaselines[id] = baseline;
            while (_watchConfigs.Count > MaxWatchEntries)
            {
                var oldest = _watchConfigs.Keys.First();
                _watchConfigs.Remove(oldest); _watchBaselines.Remove(oldest);
            }
            if (_watchPropertyCache.Count > MaxWatchPropertyCacheEntries) _watchPropertyCache.Clear();
            return id;
        }

        public static List<string> DrainChanges()
        {
            if (_watchChanges.Count == 0) return new List<string>();
            var copy = new List<string>(_watchChanges);
            _watchChanges.Clear();
            return copy;
        }

        public static void Tick()
        {
            if (_watchConfigs.Count == 0) return;
            if (++_watchFrameCounter < WatchTickIntervalFrames) return;
            _watchFrameCounter = 0;
            var stale = new List<string>();
            foreach (var kvp in _watchConfigs)
            {
                var id = kvp.Key; var cfg = kvp.Value;
                var type = cfg["type"] as string;
                string cur; string err = null;
                if (type == "property") cur = ReadPropertyValue(cfg["path"] as string, cfg["component"] as string, cfg["property"] as string, out err);
                else { cur = ""; err = $"unknown watch type '{type}'"; }
                if (err != null)
                {
                    _watchChanges.Add(SimpleMCPBridge.Runtime.JsonHelper.BuildJsonObject(("id", SimpleMCPBridge.Runtime.JsonHelper.EscapeString(id)), ("error", SimpleMCPBridge.Runtime.JsonHelper.EscapeString(err))));
                    if (err.Contains("not found")) stale.Add(id);
                    continue;
                }
                var old = _watchBaselines.TryGetValue(id, out var v) ? v : "";
                if (cur != old)
                {
                    _watchChanges.Add(SimpleMCPBridge.Runtime.JsonHelper.BuildJsonObject(("id", SimpleMCPBridge.Runtime.JsonHelper.EscapeString(id)), ("value", SimpleMCPBridge.Runtime.JsonHelper.EscapeString(cur)), ("old", SimpleMCPBridge.Runtime.JsonHelper.EscapeString(old))));
                    _watchBaselines[id] = cur;
                }
            }
            foreach (var k in stale) { _watchConfigs.Remove(k); _watchBaselines.Remove(k); }
            if (_watchPropertyCache.Count > MaxWatchPropertyCacheEntries) _watchPropertyCache.Clear();
        }

        private static string ReadPropertyValue(string objectPath, string componentType, string propertyName, out string error)
        {
            error = null;
            try
            {
                var key = objectPath + "|" + componentType + "|" + propertyName;
                if (!_watchPropertyCache.TryGetValue(key, out var cache)) { cache = new WatchPropertyCache(); _watchPropertyCache[key] = cache; }
                if (cache.Component == null)
                {
                    var go = GameObject.Find(objectPath);
                    if (go == null) { error = $"Object '{objectPath}' not found"; return ""; }
                    cache.Component = go.GetComponent(componentType);
                    if (cache.Component == null) { error = $"Component '{componentType}' not found on '{objectPath}'"; return ""; }
                    cache.Member = Resolve(cache.Component.GetType(), propertyName);
                }
                if (cache.Member == null) { error = $"Property/field '{propertyName}' not found on '{componentType}'"; return ""; }
                if (cache.Member is PropertyInfo p) return p.GetValue(cache.Component)?.ToString() ?? "null";
                return ((FieldInfo)cache.Member).GetValue(cache.Component)?.ToString() ?? "null";
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }
        private static MemberInfo Resolve(Type t, string name)
        {
            var p = t.GetProperty(name); if (p != null) return p; return t.GetField(name);
        }
    }
}
