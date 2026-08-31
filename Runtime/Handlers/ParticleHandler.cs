using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
using static SimpleMCPBridge.Runtime.Handlers.Particle.ParticleValueParser;


using static SimpleMCPBridge.Runtime.Handlers.Particle.ParticleJsonTools;
using static SimpleMCPBridge.Runtime.Handlers.Particle.ParticleMaterialTools;
namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// ParticleSystem 运行时控制工具 (particle.*)。
    ///
    /// 设计要点（见 Docs 调研）：
    /// - 模块 struct 是值类型句柄：属性 setter 直接调 native 立即生效，无需写回；
    ///   但 C# 禁止链式修改返回的 struct（CS1612），必须先存局部变量再改属性。
    /// - 全部走 typed 代码路径（直接引用 MainModule 等类型），不做反射：
    ///   反射对嵌套模块 struct 结构性不可用（get-only 访问器 + ToString 无数据），
    ///   且 Android il2cpp 会裁剪模块属性（同 Known Issue #11），typed 引用天然免疫。
    /// - 使用模型：Editor 创作（AI 创建/编辑特效资产）+ Runtime 播放为主。
    ///   创作/预览工具（create/set/simulate）全平台可用 —— Edit Mode 是创作主路径，
    ///   模块修改走 Undo 可撤销；生命周期工具（play/pause/stop/clear/emit）RequirePlayMode = true，
    ///   仅 Play Mode 注册（运行时播放控制）；只读工具（get_systems/get_state）全平台可用。
    /// </summary>
    [MCPToolClass]
    public class ParticleHandler
    {
        // ── 目标解析 ──

        private static GameObject ResolveTarget(Dictionary<string, object> args)
        {
            if (args.TryGetValue("instanceId", out var idObj) && idObj != null)
            {
                var id = Convert.ToInt32(idObj, CultureInfo.InvariantCulture);
                var all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                foreach (var go in all)
                    if (go.GetInstanceID() == id) return go;
                return null;
            }

            if (args.TryGetValue("path", out var pathObj) && pathObj is string path && !string.IsNullOrEmpty(path))
            {
                var all = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                foreach (var ps in all)
                    if (string.Equals(GetObjectPath(ps.gameObject), path, StringComparison.OrdinalIgnoreCase))
                        return ps.gameObject;
                return null;
            }

            return null;
        }

        private static ParticleSystem ResolveParticleSystem(Dictionary<string, object> args)
        {
            var go = ResolveTarget(args);
            if (go == null) return null;
            return go.GetComponent<ParticleSystem>();
        }

        // ── 值解析辅助 ──

        private static float? GetOptionalFloat(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return null;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is double d) return (float)d;
            if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
            return null;
        }

                [MCPTool(MCPMethodConst.PARTICLE_GET_SYSTEMS,
            "List all ParticleSystems in the scene with live state. " +
            "Params: 'nameContains' (string, optional), 'onlyPlaying' (bool, optional), 'maxResults' (int, default 50, max 200). " +
            "Each system returns: name, path, instanceId, isPlaying, isPaused, isStopped, isEmitting, particleCount, " +
            "time, totalTime, duration, loop, playOnAwake, maxParticles, simulationSpace, enabledModules, renderer info.")]
        [MCPParam("nameContains", Type = "string", Description = "Filter by object name substring")]
        [MCPParam("onlyPlaying", Type = "boolean", Description = "Only include systems that are playing")]
        [MCPParam("maxResults", Type = "integer", Description = "Max systems to return (default 50, max 200)")]
public static string GetSystems(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var nameContains = GetString(args, "nameContains");
                var onlyPlaying = GetOptionalBool(args, "onlyPlaying").GetValueOrDefault(false);
                var maxResults = (int)GetOptionalInt(args, "maxResults").GetValueOrDefault(50);
                maxResults = Mathf.Clamp(maxResults, 1, 200);

                var all = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                var jsons = new List<string>();
                int counted = 0;
                foreach (var ps in all)
                {
                    if (counted >= maxResults) break;
                    if (!string.IsNullOrEmpty(nameContains) &&
                        !ps.gameObject.name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (onlyPlaying && !ps.isPlaying) continue;
                    jsons.Add(BuildSystemJson(ps, false));
                    counted++;
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", counted.ToString(CultureInfo.InvariantCulture)),
                    ("total", all.Length.ToString(CultureInfo.InvariantCulture)),
                    ("systems", JsonHelper.BuildJsonArray(jsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.get_systems failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_GET_STATE,
            "Get detailed state and configuration of a single ParticleSystem (by instanceId or path). " +
            "Includes lifecycle state, main module key values (duration/loop/startSpeed/startSize/startColor/...), " +
            "emission rates, burst count, shape info and renderer info.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'FX/Explosion'")]
        public static string GetState(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null)
                    return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("system", BuildSystemJson(ps, true))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.get_state failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_CREATE,
            "Create a new GameObject with a ParticleSystem (by optional parent instanceId/path). " +
            "Params: 'name' (string, default 'ParticleSystem'), 'parentInstanceId'/'parentPath' (optional), " +
            "optional initial config: 'startSpeed', 'startSize', 'startLifetime' (numbers), " +
            "'startColor' ([r,g,b,a] | #RRGGBB | color name), 'rateOverTime' (number), 'shapeType' (enum name), " +
            "'loop' (bool), 'playOnAwake' (bool), 'material' (string — builtin name like Default-Particle, " +
            "or Assets/... path; assign to the ParticleSystemRenderer so particles are visible). " +
            "Returns instanceId + path. Works in Edit Mode (undoable).")]
        [MCPParam("name", Type = "string", Description = "Object name (default 'ParticleSystem')")]
        [MCPParam("parentInstanceId", Type = "integer", Description = "Parent object instanceId (optional)")]
        [MCPParam("parentPath", Type = "string", Description = "Parent transform path (optional)")]
        [MCPParam("startSpeed", Type = "number", Description = "Initial startSpeed")]
        [MCPParam("startSize", Type = "number", Description = "Initial startSize")]
        [MCPParam("startLifetime", Type = "number", Description = "Initial startLifetime")]
        [MCPParam("startColor", Type = "string", Description = "Initial startColor: [r,g,b,a], #RRGGBB, or color name")]
        [MCPParam("rateOverTime", Type = "number", Description = "Initial emission rateOverTime")]
        [MCPParam("shapeType", Type = "string", Description = "Initial shape type (e.g. Cone, Sphere, Box, Circle)")]
        [MCPParam("loop", Type = "boolean", Description = "Initial loop (default true)")]
        [MCPParam("playOnAwake", Type = "boolean", Description = "Initial playOnAwake (default true)")]
        [MCPParam("material", Type = "string", Description = "Renderer material: builtin name (Default-Particle, Sprites-Default) or Assets/... path (Editor)")]
        [MCPParam("materialDir", Type = "string", Description = "Editor only: asset folder for builtin-name materials (default: effect prefab's folder, fallback Assets/)")]
        [MCPParam("shader", Type = "string", Description = "Shader for created material: shader name (Shader.Find, e.g. Universal Render Pipeline/Particles/Unlit) or Assets/... path (Editor). Default: URP Particles/Simple Lit")]
        public static string Create(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var name = GetString(args, "name", "ParticleSystem");
                var go = new GameObject(name);
                SceneObjectTools.UndoRegisterCreated(go, "Create ParticleSystem");

                // Optional parent
                if (args.TryGetValue("parentInstanceId", out var pidObj) && pidObj != null)
                {
                    var pid = Convert.ToInt32(pidObj, CultureInfo.InvariantCulture);
                    var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                    foreach (var t in all)
                        if (t.gameObject.GetInstanceID() == pid) { go.transform.SetParent(t, false); break; }
                }
                else if (args.TryGetValue("parentPath", out var ppObj) && ppObj is string pp && !string.IsNullOrEmpty(pp))
                {
                    var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                    foreach (var t in all)
                        if (string.Equals(GetObjectPath(t.gameObject), pp, StringComparison.OrdinalIgnoreCase))
                        { go.transform.SetParent(t, false); break; }
                }

                var ps = go.AddComponent<ParticleSystem>();
                SceneObjectTools.UndoRecord(ps, "Create ParticleSystem config");

                var m = ps.main;
                if (GetOptionalFloat(args, "startSpeed") is float ss) m.startSpeed = new ParticleSystem.MinMaxCurve(ss);
                if (GetOptionalFloat(args, "startSize") is float sz) m.startSize = new ParticleSystem.MinMaxCurve(sz);
                if (GetOptionalFloat(args, "startLifetime") is float sl) m.startLifetime = new ParticleSystem.MinMaxCurve(sl);
                if (GetOptionalBool(args, "loop") is bool lp) m.loop = lp;
                if (GetOptionalBool(args, "playOnAwake") is bool poa) m.playOnAwake = poa;
                var colorRaw = GetRawValue(args, "startColor");
                if (colorRaw != null) m.startColor = new ParticleSystem.MinMaxGradient(ToColor(colorRaw));
                if (GetOptionalFloat(args, "rateOverTime") is float rot)
                {
                    var e = ps.emission;
                    e.rateOverTime = new ParticleSystem.MinMaxCurve(rot);
                }
                var shapeRaw = GetString(args, "shapeType");
                if (!string.IsNullOrEmpty(shapeRaw))
                {
                    var s = ps.shape;
                    s.shapeType = ToEnum<ParticleSystemShapeType>(shapeRaw);
                }
                var materialRaw = GetString(args, "material");
                if (!string.IsNullOrEmpty(materialRaw))
                    ApplyMaterial(ps, materialRaw, GetString(args, "materialDir"), GetString(args, "shader"));

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go)))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.create failed: {ex.Message}");
            }
        }

        // ── 工具：生命周期 ──

        [MCPTool(MCPMethodConst.PARTICLE_PLAY,
            "Play a ParticleSystem (by instanceId or path). " +
            "Params: 'withChildren' (bool, default true), 'restart' (bool, default false — if true, stops then plays to restart from the beginning).",
            RequirePlayMode = true)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("withChildren", Type = "boolean", Description = "Also play child systems (default true)")]
        [MCPParam("restart", Type = "boolean", Description = "Restart from the beginning even if already playing (default false)")]
        public static string Play(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var withChildren = GetOptionalBool(args, "withChildren").GetValueOrDefault(true);
                var restart = GetOptionalBool(args, "restart").GetValueOrDefault(false);
                if (restart && ps.isPlaying)
                    ps.Stop(withChildren, ParticleSystemStopBehavior.StopEmitting);
                ps.Play(withChildren);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.play failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_PAUSE,
            "Pause a ParticleSystem (by instanceId or path). Existing particles freeze in place. " +
            "Params: 'withChildren' (bool, default true).",
            RequirePlayMode = true)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("withChildren", Type = "boolean", Description = "Also pause child systems (default true)")]
        public static string Pause(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var withChildren = GetOptionalBool(args, "withChildren").GetValueOrDefault(true);
                ps.Pause(withChildren);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.pause failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_STOP,
            "Stop a ParticleSystem (by instanceId or path). " +
            "Params: 'withChildren' (bool, default true), 'clear' (bool, default false — true destroys existing particles immediately, false lets them finish).",
            RequirePlayMode = true)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("withChildren", Type = "boolean", Description = "Also stop child systems (default true)")]
        [MCPParam("clear", Type = "boolean", Description = "Clear existing particles immediately (default false)")]
        public static string Stop(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var withChildren = GetOptionalBool(args, "withChildren").GetValueOrDefault(true);
                var clear = GetOptionalBool(args, "clear").GetValueOrDefault(false);
                ps.Stop(withChildren, clear
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.stop failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_CLEAR,
            "Clear all particles from a ParticleSystem (by instanceId or path) without stopping it. " +
            "Params: 'withChildren' (bool, default true).",
            RequirePlayMode = true)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("withChildren", Type = "boolean", Description = "Also clear child systems (default true)")]
        public static string Clear(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var withChildren = GetOptionalBool(args, "withChildren").GetValueOrDefault(true);
                ps.Clear(withChildren);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.clear failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_EMIT,
            "Emit a one-shot burst of particles from a ParticleSystem (by instanceId or path) — works without playing. " +
            "Params: 'count' (int, default 1), 'position' ([x,y,z] world, default system position), 'velocity' ([x,y,z]), " +
            "'startLifetime' (number), 'startSize' (number), 'startColor' ([r,g,b,a] | #RRGGBB | color name).",
            RequirePlayMode = true)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("count", Type = "integer", Description = "Particle count to emit (default 1, max 10000)")]
        [MCPParam("position", Type = "array", Description = "World position [x,y,z] (default: system position)")]
        [MCPParam("velocity", Type = "array", Description = "Initial velocity [x,y,z]")]
        [MCPParam("startLifetime", Type = "number", Description = "Override lifetime (default: system startLifetime)")]
        [MCPParam("startSize", Type = "number", Description = "Override size (default: system startSize)")]
        [MCPParam("startColor", Type = "string", Description = "Override color: [r,g,b,a], #RRGGBB, or color name")]
        public static string Emit(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");

                var count = (int)GetOptionalInt(args, "count").GetValueOrDefault(1);
                count = Mathf.Clamp(count, 1, 10000);

                var ep = new ParticleSystem.EmitParams();
                var pos = GetOptionalFloatArray(args, "position");
                if (pos != null && pos.Length >= 3)
                    ep.position = new Vector3(pos[0], pos[1], pos[2]);
                var vel = GetOptionalFloatArray(args, "velocity");
                if (vel != null && vel.Length >= 3)
                    ep.velocity = new Vector3(vel[0], vel[1], vel[2]);
                if (GetOptionalFloat(args, "startLifetime") is float lt)
                    ep.startLifetime = lt;
                if (GetOptionalFloat(args, "startSize") is float sz)
                    ep.startSize = sz;
                var colorRaw = GetRawValue(args, "startColor");
                if (colorRaw != null)
                    ep.startColor = ToColor(colorRaw);

                ps.Emit(ep, count);
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("emitted", count.ToString(CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.emit failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.PARTICLE_SIMULATE,
            "Simulate a ParticleSystem to a given time (by instanceId or path) — deterministic preview. " +
            "Works in Edit Mode too (editor preview uses the same API). " +
            "Params: 'time' (number, required, seconds), 'withChildren' (bool, default true), " +
            "'restart' (bool, default false), 'fixedTimeStep' (bool, default true).")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("time", Type = "number", Required = true, Description = "Simulate to this time in seconds")]
        [MCPParam("withChildren", Type = "boolean", Description = "Also simulate child systems (default true)")]
        [MCPParam("restart", Type = "boolean", Description = "Restart simulation from the beginning (default false)")]
        [MCPParam("fixedTimeStep", Type = "boolean", Description = "Use fixed time step (default true)")]
        public static string Simulate(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var time = GetOptionalFloat(args, "time");
                if (time == null) return ErrorJson("Missing required parameter: 'time'");
                var withChildren = GetOptionalBool(args, "withChildren").GetValueOrDefault(true);
                var restart = GetOptionalBool(args, "restart").GetValueOrDefault(false);
                var fixedTimeStep = GetOptionalBool(args, "fixedTimeStep").GetValueOrDefault(true);
                ps.Simulate(time.Value, withChildren, restart, fixedTimeStep);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.simulate failed: {ex.Message}");
            }
        }

        // ── 工具：模块属性设置 ──

        [MCPTool(MCPMethodConst.PARTICLE_SET,
            "Set a module property on a ParticleSystem (by instanceId or path) via typed code path (no reflection). " +
            "Works in Edit Mode (undoable) and Play Mode. " +
            "Params: 'module' (required: main, emission, shape, velocityOverLifetime, forceOverLifetime, colorOverLifetime, " +
            "sizeOverLifetime, rotationOverLifetime, noise, trails, textureSheetAnimation, lights, collision, renderer), " +
            "'property' (required, e.g. loop, rateOverTime, burst, startSpeed, startSize, startColor, radius, maxParticles, renderMode), " +
            "'value' (required: number, bool, [x,y,z], [r,g,b,a], #RRGGBB, or enum name). " +
            "MinMaxCurve properties accept a single number (constant), [min,max] (random), " +
            "or [[t,v],...] (animation curve, e.g. [[0,1],[1,0]] shrinks over lifetime). " +
            "Color properties accept a single color, [c1,c2] (two-color gradient — time gradient on " +
            "colorOverLifetime, random on startColor), or [[t,c],...] (full gradient keys). " +
            "Note: when main.startSize3D is on, 'startSize' only affects X — use startSizeX/Y/Z. " +
             "Renderer material: use module 'renderer' property 'material' (builtin name or Assets/... path, " +
             "optional 'shader' param controls the shader used to create it). " +
             "Change material shader: use module 'renderer' property 'shader' (shader name or Assets/... path; " +
             "Editor: persists to the material asset). " +
             "Material color/texture: use scene.set_material (ParticleSystemRenderer is a Renderer).")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path")]
        [MCPParam("module", Type = "string", Required = true, Description = "Module name: main, emission, shape, velocityOverLifetime, forceOverLifetime, colorOverLifetime, sizeOverLifetime, rotationOverLifetime, noise, trails, textureSheetAnimation, lights, collision, subEmitters, inheritVelocity, limitVelocityOverLifetime, externalForces, customData, trigger, renderer")]
        [MCPParam("property", Type = "string", Required = true, Description = "Property name within the module (e.g. 'loop', 'rateOverTime', 'startSpeed', 'startColor', 'radius', 'renderMode')")]
        [MCPParam("value", Required = true, Description = "New value: number, bool, [x,y,z], [r,g,b,a], #RRGGBB, or enum name")]
        [MCPParam("materialDir", Type = "string", Description = "Editor only (renderer.material): asset folder for builtin-name materials (default: effect prefab's folder, fallback Assets/)")]
        [MCPParam("shader", Type = "string", Description = "Shader name/path — used when creating a material (renderer.material) or changing it (renderer.shader). Default: URP Particles/Simple Lit")]
        public static string Set(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var ps = ResolveParticleSystem(args);
                if (ps == null) return ErrorJson("ParticleSystem not found. Provide 'instanceId' or 'path'.");
                var module = GetRequiredString(args, "module");
                var property = GetRequiredString(args, "property");
                var raw = GetRawValue(args, "value");
                if (raw == null) return ErrorJson("Missing required parameter: 'value'");

                SceneObjectTools.UndoRecord(ps, $"Set {module}.{property}");

                string result;
                switch (module.ToLowerInvariant())
                {
                    case "main": result = SetMain(ps, property, raw); break;
                    case "emission": result = SetEmission(ps, property, raw); break;
                    case "shape": result = SetShape(ps, property, raw); break;
                    case "velocityoverlifetime": result = SetVelocityOverLifetime(ps, property, raw); break;
                    case "forceoverlifetime": result = SetForceOverLifetime(ps, property, raw); break;
                    case "coloroverlifetime": result = SetColorOverLifetime(ps, property, raw); break;
                    case "colorbyspeed": result = SetColorBySpeed(ps, property, raw); break;
                    case "sizeoverlifetime": result = SetSizeOverLifetime(ps, property, raw); break;
                    case "sizebyspeed": result = SetSizeBySpeed(ps, property, raw); break;
                    case "rotationoverlifetime": result = SetRotationOverLifetime(ps, property, raw); break;
                    case "rotationbyspeed": result = SetRotationBySpeed(ps, property, raw); break;
                    case "lifetimebyemitterspeed": result = SetLifetimeByEmitterSpeed(ps, property, raw); break;
                    case "noise": result = SetNoise(ps, property, raw); break;
                    case "trails": result = SetTrails(ps, property, raw); break;
                    case "texturesheetanimation": result = SetTextureSheetAnimation(ps, property, raw); break;
                    case "lights": result = SetLights(ps, property, raw); break;
                    case "collision": result = SetCollision(ps, property, raw); break;
                    case "subemitters": result = SetSubEmitters(ps, property, raw, args); break;
                    case "inheritvelocity": result = SetInheritVelocity(ps, property, raw); break;
                    case "limitvelocityoverlifetime": result = SetLimitVelocityOverLifetime(ps, property, raw); break;
                    case "externalforces": result = SetExternalForces(ps, property, raw); break;
                    case "customdata": result = SetCustomData(ps, property, raw); break;
                    case "trigger": result = SetTrigger(ps, property, raw); break;
                    case "renderer": result = SetRenderer(ps, property, raw, GetString(args, "materialDir"), GetString(args, "shader")); break;
                    default:
                        return ErrorJson($"Unknown module '{module}'. Supported: main, emission, shape, velocityOverLifetime, forceOverLifetime, colorOverLifetime, colorBySpeed, sizeOverLifetime, sizeBySpeed, rotationOverLifetime, rotationBySpeed, lifetimeByEmitterSpeed, noise, trails, textureSheetAnimation, lights, collision, subEmitters, inheritVelocity, limitVelocityOverLifetime, externalForces, customData, trigger, renderer");
                }

                if (result == null)
                    return ErrorJson($"Property '{property}' not supported on module '{module}'");

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("module", JsonHelper.EscapeString(module)),
                    ("property", JsonHelper.EscapeString(property)),
                    ("value", JsonHelper.EscapeString(raw.ToString()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"particle.set failed: {ex.Message}");
            }
        }

        // ── 模块 setter（返回 null = 属性不支持） ──

        private static string SetMain(ParticleSystem ps, string property, object raw)
        {
            var m = ps.main;
            switch (property.ToLowerInvariant())
            {
                case "duration": m.duration = ToFloat(raw); break;
                case "loop": m.loop = ToBool(raw); break;
                case "prewarm": m.prewarm = ToBool(raw); break;
                case "startdelay": m.startDelay = ToCurve(raw); break;
                case "startlifetime": m.startLifetime = ToCurve(raw); break;
                case "startspeed": m.startSpeed = ToCurve(raw); break;
                case "startsize": m.startSize = ToCurve(raw); break;
                case "startsizex": m.startSizeX = ToCurve(raw); break;
                case "startsizey": m.startSizeY = ToCurve(raw); break;
                case "startsizez": m.startSizeZ = ToCurve(raw); break;
                case "startrotation": m.startRotation = ToCurve(raw); break;
                case "startrotationx": m.startRotationX = ToCurve(raw); break;
                case "startrotationy": m.startRotationY = ToCurve(raw); break;
                case "startrotationz": m.startRotationZ = ToCurve(raw); break;
                case "startcolor": m.startColor = ToGradient(raw); break;
                case "gravitymodifier": m.gravityModifier = ToCurve(raw); break;
                case "gravitysource": m.gravitySource = ToEnum<ParticleSystemGravitySource>(raw); break;
                case "simulationspeed": m.simulationSpeed = ToFloat(raw); break;
                case "useunscaledtime": m.useUnscaledTime = ToBool(raw); break;
                case "simulationspace": m.simulationSpace = ToEnum<ParticleSystemSimulationSpace>(raw); break;
                case "scalingmode": m.scalingMode = ToEnum<ParticleSystemScalingMode>(raw); break;
                case "playonawake": m.playOnAwake = ToBool(raw); break;
                case "maxparticles": m.maxParticles = ToInt(raw); break;
                case "stopaction": m.stopAction = ToEnum<ParticleSystemStopAction>(raw); break;
                case "fliprotation": m.flipRotation = ToFloat(raw); break;
                case "emittervelocity": m.emitterVelocity = ToVector3(raw); break;
                case "startsize3d": m.startSize3D = ToBool(raw); break;
                case "startrotation3d": m.startRotation3D = ToBool(raw); break;
                case "cullingmode": m.cullingMode = ToEnum<ParticleSystemCullingMode>(raw); break;
                case "ringbuffermode": m.ringBufferMode = ToEnum<ParticleSystemRingBufferMode>(raw); break;
                case "emittervelocitymode": m.emitterVelocityMode = ToEnum<ParticleSystemEmitterVelocityMode>(raw); break;
                case "customsimulationspace":
                {
                    var tmp = new Dictionary<string, object>();
                    if (raw is long || raw is int) tmp["instanceId"] = raw;
                    else tmp["path"] = raw.ToString();
                    var target = ResolveParticleSystem(tmp);
                    if (target == null)
                        throw new ArgumentException("customSimulationSpace target ParticleSystem not found (provide instanceId or path)");
                    m.customSimulationSpace = target.transform;
                    break;
                }
                default: return null;
            }
            return "ok";
        }

        private static string SetEmission(ParticleSystem ps, string property, object raw)
        {
            var e = ps.emission;
            switch (property.ToLowerInvariant())
            {
                case "enabled": e.enabled = ToBool(raw); break;
                case "rateovertime": e.rateOverTime = ToCurve(raw); break;
                case "rateoverdistance": e.rateOverDistance = ToCurve(raw); break;
                case "burst":
                {
                    // [count, time] 单 burst;[[c,t],...] 多 burst(替换现有 bursts)
                    var list = NormalizeArray(raw);
                    if (list != null && list.Count > 0 && IsNestedEntry(list[0]))
                    {
                        var bursts = new ParticleSystem.Burst[list.Count];
                        for (int i = 0; i < list.Count; i++)
                        {
                            var bt = ToFloatArray(NormalizeArray(list[i]) ?? list[i]);
                            if (bt == null || bt.Length < 2)
                                throw new ArgumentException("Burst must be [count, time]");
                            bursts[i] = new ParticleSystem.Burst(bt[1], (short)bt[0]);
                        }
                        e.SetBursts(bursts);
                    }
                    else
                    {
                        var bt = ToFloatArray(raw);
                        if (bt == null || bt.Length < 2)
                            throw new ArgumentException("Burst must be [count, time]");
                        e.SetBursts(new[] { new ParticleSystem.Burst(bt[1], (short)bt[0]) });
                    }
                    break;
                }
                default: return null;
            }
            return "ok";
        }

        private static string SetShape(ParticleSystem ps, string property, object raw)
        {
            var s = ps.shape;
            switch (property.ToLowerInvariant())
            {
                case "enabled": s.enabled = ToBool(raw); break;
                case "shapetype": s.shapeType = ToEnum<ParticleSystemShapeType>(raw); break;
                case "radius": s.radius = ToFloat(raw); break;
                case "angle": s.angle = ToFloat(raw); break;
                case "length": s.length = ToFloat(raw); break;
                case "boxthickness": s.boxThickness = ToVector3(raw); break;
                case "arc": s.arc = ToFloat(raw); break;
                case "position": s.position = ToVector3(raw); break;
                case "rotation": s.rotation = ToVector3(raw); break;
                case "scale": s.scale = ToVector3(raw); break;
                case "meshshapetype": s.meshShapeType = ToEnum<ParticleSystemMeshShapeType>(raw); break;
                case "mesh": s.mesh = LoadAsset<Mesh>(raw); break;
                case "sprite": s.sprite = LoadAsset<Sprite>(raw); break;
                case "texture": s.texture = LoadAsset<Texture2D>(raw); break;
                case "textureclipchannel": s.textureClipChannel = (ParticleSystemShapeTextureChannel)ToInt(raw); break;
                case "textureclipthreshold": s.textureClipThreshold = ToFloat(raw); break;
                case "randomdirectionamount": s.randomDirectionAmount = ToFloat(raw); break;
                case "sphericaldirectionamount": s.sphericalDirectionAmount = ToFloat(raw); break;
                case "aligntodirection": s.alignToDirection = ToBool(raw); break;
                case "radiusthickness": s.radiusThickness = ToFloat(raw); break;
                case "donutradius": s.donutRadius = ToFloat(raw); break;
                case "arcmode": s.arcMode = ToEnum<ParticleSystemShapeMultiModeValue>(raw); break;
                case "arcspread": s.arcSpread = ToFloat(raw); break;
                case "arcspeed": s.arcSpeed = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetVelocityOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var v = ps.velocityOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": v.enabled = ToBool(raw); break;
                case "x": v.x = ToCurve(raw); break;
                case "y": v.y = ToCurve(raw); break;
                case "z": v.z = ToCurve(raw); break;
                case "space": v.space = ToEnum<ParticleSystemSimulationSpace>(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetForceOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var f = ps.forceOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": f.enabled = ToBool(raw); break;
                case "x": f.x = ToCurve(raw); break;
                case "y": f.y = ToCurve(raw); break;
                case "z": f.z = ToCurve(raw); break;
                case "space": f.space = ToEnum<ParticleSystemSimulationSpace>(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetColorOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var c = ps.colorOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": c.enabled = ToBool(raw); break;
                case "color": c.color = ToGradient(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetSizeOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var s = ps.sizeOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": s.enabled = ToBool(raw); break;
                case "size": s.size = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetRotationOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var r = ps.rotationOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": r.enabled = ToBool(raw); break;
                case "separateaxes": r.separateAxes = ToBool(raw); break;
                case "x": r.x = ToCurve(raw); break;
                case "y": r.y = ToCurve(raw); break;
                case "z": r.z = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetRotationBySpeed(ParticleSystem ps, string property, object raw)
        {
            var r = ps.rotationBySpeed;
            switch (property.ToLowerInvariant())
            {
                case "enabled": r.enabled = ToBool(raw); break;
                case "separateaxes": r.separateAxes = ToBool(raw); break;
                case "x": r.x = ToCurve(raw); break;
                case "y": r.y = ToCurve(raw); break;
                case "z": r.z = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetColorBySpeed(ParticleSystem ps, string property, object raw)
        {
            var c = ps.colorBySpeed;
            switch (property.ToLowerInvariant())
            {
                case "enabled": c.enabled = ToBool(raw); break;
                case "color": c.color = ToGradient(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetSizeBySpeed(ParticleSystem ps, string property, object raw)
        {
            var s = ps.sizeBySpeed;
            switch (property.ToLowerInvariant())
            {
                case "enabled": s.enabled = ToBool(raw); break;
                case "size": s.size = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetLifetimeByEmitterSpeed(ParticleSystem ps, string property, object raw)
        {
            var l = ps.lifetimeByEmitterSpeed;
            switch (property.ToLowerInvariant())
            {
                case "enabled": l.enabled = ToBool(raw); break;
                case "curvemultiplier": l.curveMultiplier = ToFloat(raw); break;
                case "curve":
                {
                    l.curve = ToCurve(raw);
                    break;
                }
                case "range": l.range = ToVector2(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetNoise(ParticleSystem ps, string property, object raw)
        {
            var n = ps.noise;
            switch (property.ToLowerInvariant())
            {
                case "enabled": n.enabled = ToBool(raw); break;
                case "strength": n.strength = ToCurve(raw); break;
                case "frequency": n.frequency = ToFloat(raw); break;
                case "damping": n.damping = ToBool(raw); break;
                case "octaves": n.octaveCount = ToInt(raw); break;
                case "octavemultiplier": n.octaveMultiplier = ToFloat(raw); break;
                case "octavescale": n.octaveScale = ToFloat(raw); break;
                case "quality": n.quality = ToEnum<ParticleSystemNoiseQuality>(raw); break;
                case "scrollspeed": n.scrollSpeed = ToCurve(raw); break;
                case "positionamount": n.positionAmount = ToCurve(raw); break;
                case "rotationamount": n.rotationAmount = ToCurve(raw); break;
                case "sizeamount": n.sizeAmount = ToCurve(raw); break;
                case "remap": n.remap = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetTrails(ParticleSystem ps, string property, object raw)
        {
            var t = ps.trails;
            switch (property.ToLowerInvariant())
            {
                case "enabled": t.enabled = ToBool(raw); break;
                case "lifetime": t.lifetime = ToCurve(raw); break;
                case "widthovertrail": t.widthOverTrail = ToCurve(raw); break;
                case "colorovertrail": t.colorOverTrail = ToGradient(raw); break;
                case "minvertexdistance": t.minVertexDistance = ToFloat(raw); break;
                case "diewithparticles": t.dieWithParticles = ToBool(raw); break;
                case "ribboncount": t.ribbonCount = ToInt(raw); break;
                case "texturemode": t.textureMode = ToEnum<ParticleSystemTrailTextureMode>(raw); break;
                case "worldspace": t.worldSpace = ToBool(raw); break;
                case "generatelightingdata": t.generateLightingData = ToBool(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetTextureSheetAnimation(ParticleSystem ps, string property, object raw)
        {
            var t = ps.textureSheetAnimation;
            switch (property.ToLowerInvariant())
            {
                case "enabled": t.enabled = ToBool(raw); break;
                case "fps": t.fps = ToFloat(raw); break;
                case "numtilesx": t.numTilesX = ToInt(raw); break;
                case "numtilesy": t.numTilesY = ToInt(raw); break;
                case "rowindex": t.rowIndex = ToInt(raw); break;
                case "mode": t.mode = ToEnum<ParticleSystemAnimationMode>(raw); break;
                case "startframe": t.startFrame = ToCurve(raw); break;
                case "frameovertime": t.frameOverTime = ToCurve(raw); break;
                case "flipu": t.flipU = ToFloat(raw); break;
                case "flipv": t.flipV = ToFloat(raw); break;
                case "rowmode": t.rowMode = ToEnum<ParticleSystemAnimationRowMode>(raw); break;
                case "timemode": t.timeMode = ToEnum<ParticleSystemAnimationTimeMode>(raw); break;
                case "speedrange": t.speedRange = ToVector2(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetLights(ParticleSystem ps, string property, object raw)
        {
            var l = ps.lights;
            switch (property.ToLowerInvariant())
            {
                case "enabled": l.enabled = ToBool(raw); break;
                case "ratio": l.ratio = ToFloat(raw); break;
                case "intensitymultiplier": l.intensityMultiplier = ToFloat(raw); break;
                case "rangemultiplier": l.rangeMultiplier = ToFloat(raw); break;
                case "maxlights": l.maxLights = ToInt(raw); break;
                case "useparticlecolor": l.useParticleColor = ToBool(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetCollision(ParticleSystem ps, string property, object raw)
        {
            var c = ps.collision;
            switch (property.ToLowerInvariant())
            {
                case "enabled": c.enabled = ToBool(raw); break;
                case "type": c.type = ToEnum<ParticleSystemCollisionType>(raw); break;
                case "mode": c.mode = ToEnum<ParticleSystemCollisionMode>(raw); break;
                case "bounce": c.bounce = ToCurve(raw); break;
                case "lifetimeloss": c.lifetimeLoss = ToCurve(raw); break;
                case "minkillspeed": c.minKillSpeed = ToFloat(raw); break;
                case "maxkillspeed": c.maxKillSpeed = ToFloat(raw); break;
                case "colliderforce": c.colliderForce = ToFloat(raw); break;
                case "multiplycolliderforcebycollisionangle": c.multiplyColliderForceByCollisionAngle = ToBool(raw); break;
                case "multiplycolliderforcebyparticlespeed": c.multiplyColliderForceByParticleSpeed = ToBool(raw); break;
                case "multiplycolliderforcebyparticlesize": c.multiplyColliderForceByParticleSize = ToBool(raw); break;
                case "sendcollisionmessages": c.sendCollisionMessages = ToBool(raw); break;
                case "dampen": c.dampen = ToCurve(raw); break;
                case "quality": c.quality = ToEnum<ParticleSystemCollisionQuality>(raw); break;
                case "voxelsize": c.voxelSize = ToFloat(raw); break;
                case "collideswith": c.collidesWith = (LayerMask)ToInt(raw); break;
                case "maxcollisionshapes": c.maxCollisionShapes = ToInt(raw); break;
                case "enabledynamiccolliders": c.enableDynamicColliders = ToBool(raw); break;
                case "enableinteriorcollisions": c.enableInteriorCollisions = ToBool(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetSubEmitters(ParticleSystem ps, string property, object raw, Dictionary<string, object> args)
        {
            var se = ps.subEmitters;
            switch (property.ToLowerInvariant())
            {
                case "enabled": se.enabled = ToBool(raw); break;
                case "birth0": SetSubEmitterSlot(se, 0, raw); break;
                case "birth1": SetSubEmitterSlot(se, 1, raw); break;
                case "collision0": SetSubEmitterSlot(se, 2, raw); break;
                case "collision1": SetSubEmitterSlot(se, 3, raw); break;
                case "death0": SetSubEmitterSlot(se, 4, raw); break;
                case "death1": SetSubEmitterSlot(se, 5, raw); break;
                default: return null;
            }
            return "ok";
        }

        private static void SetSubEmitterSlot(ParticleSystem.SubEmittersModule se, int index, object raw)
        {
            var tmp = new Dictionary<string, object>();
            if (raw is long || raw is int) tmp["instanceId"] = raw;
            else tmp["path"] = raw.ToString();
            var sub = ResolveParticleSystem(tmp);
            if (sub == null)
                throw new ArgumentException("Sub-emitter ParticleSystem not found (provide instanceId or path)");
            var type = index < 2 ? ParticleSystemSubEmitterType.Birth
                : index < 4 ? ParticleSystemSubEmitterType.Collision
                : ParticleSystemSubEmitterType.Death;
            if (index < se.subEmittersCount)
            {
                // 覆盖已有槽位
                se.SetSubEmitterSystem(index, sub);
                se.SetSubEmitterType(index, type);
            }
            else
            {
                // 追加新子发射器(首个/后续创建)
                se.AddSubEmitter(sub, type, ParticleSystemSubEmitterProperties.InheritNothing);
            }
        }

        private static string SetInheritVelocity(ParticleSystem ps, string property, object raw)
        {
            var iv = ps.inheritVelocity;
            switch (property.ToLowerInvariant())
            {
                case "enabled": iv.enabled = ToBool(raw); break;
                case "mode": iv.mode = ToEnum<ParticleSystemInheritVelocityMode>(raw); break;
                case "curve": iv.curve = ToCurve(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetLimitVelocityOverLifetime(ParticleSystem ps, string property, object raw)
        {
            var lv = ps.limitVelocityOverLifetime;
            switch (property.ToLowerInvariant())
            {
                case "enabled": lv.enabled = ToBool(raw); break;
                case "limit": lv.limit = ToCurve(raw); break;
                case "limitx": lv.limitX = ToCurve(raw); break;
                case "limity": lv.limitY = ToCurve(raw); break;
                case "limitz": lv.limitZ = ToCurve(raw); break;
                case "dampen": lv.dampen = ToFloat(raw); break;
                case "separateaxes": lv.separateAxes = ToBool(raw); break;
                case "space": lv.space = ToEnum<ParticleSystemSimulationSpace>(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetExternalForces(ParticleSystem ps, string property, object raw)
        {
            var ef = ps.externalForces;
            switch (property.ToLowerInvariant())
            {
                case "enabled": ef.enabled = ToBool(raw); break;
                case "multiplier": ef.multiplier = ToFloat(raw); break;
                case "influencefilter": ef.influenceFilter = ToEnum<ParticleSystemGameObjectFilter>(raw); break;
                case "influencemask": ef.influenceMask = (LayerMask)ToInt(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetCustomData(ParticleSystem ps, string property, object raw)
        {
            var cd = ps.customData;
            switch (property.ToLowerInvariant())
            {
                case "enabled": cd.enabled = ToBool(raw); break;
                case "mode0": cd.SetMode(ParticleSystemCustomData.Custom1, ToEnum<ParticleSystemCustomDataMode>(raw)); break;
                case "mode1": cd.SetMode(ParticleSystemCustomData.Custom2, ToEnum<ParticleSystemCustomDataMode>(raw)); break;
                case "setvector0": cd.SetVector(ParticleSystemCustomData.Custom1, 0, ToCurve(raw)); break;
                case "setvector1": cd.SetVector(ParticleSystemCustomData.Custom2, 0, ToCurve(raw)); break;
                case "setcolor0": cd.SetColor(ParticleSystemCustomData.Custom1, ToGradient(raw)); break;
                case "setcolor1": cd.SetColor(ParticleSystemCustomData.Custom2, ToGradient(raw)); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetTrigger(ParticleSystem ps, string property, object raw)
        {
            var tr = ps.trigger;
            switch (property.ToLowerInvariant())
            {
                case "enabled": tr.enabled = ToBool(raw); break;
                case "inside": tr.inside = ToEnum<ParticleSystemOverlapAction>(raw); break;
                case "outside": tr.outside = ToEnum<ParticleSystemOverlapAction>(raw); break;
                case "enter": tr.enter = ToEnum<ParticleSystemOverlapAction>(raw); break;
                case "exit": tr.exit = ToEnum<ParticleSystemOverlapAction>(raw); break;
                default: return null;
            }
            return "ok";
        }

        private static string SetRenderer(ParticleSystem ps, string property, object raw, string materialDir, string shaderSpec)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r == null) throw new ArgumentException("No ParticleSystemRenderer on this system");
            switch (property.ToLowerInvariant())
            {
                case "enabled": r.enabled = ToBool(raw); break;
                case "material":
                    ApplyMaterial(ps, raw.ToString(), materialDir, shaderSpec);
                    break;
                case "shader":
                    SetRendererShader(r, raw.ToString());
                    break;
                case "trailmaterial":
                    r.trailMaterial = ResolveMaterial(ps, raw.ToString(), materialDir, null);
                    break;
                case "mesh": r.mesh = LoadAsset<Mesh>(raw); break;
                case "rendermode": r.renderMode = ToEnum<ParticleSystemRenderMode>(raw); break;
                case "sortinglayername": r.sortingLayerName = raw.ToString(); break;
                case "sortingorder": r.sortingOrder = ToInt(raw); break;
                case "shadowcastingmode": r.shadowCastingMode = ToEnum<UnityEngine.Rendering.ShadowCastingMode>(raw); break;
                case "minparticlesize": r.minParticleSize = ToFloat(raw); break;
                case "maxparticlesize": r.maxParticleSize = ToFloat(raw); break;
                case "normaldirection": r.normalDirection = ToFloat(raw); break;
                case "receiveshadows": r.receiveShadows = ToBool(raw); break;
                case "sortingfudge": r.sortingFudge = ToFloat(raw); break;
                case "pivot": r.pivot = ToVector3(raw); break;
                case "velocityscale": r.velocityScale = ToFloat(raw); break;
                case "lengthscale": r.lengthScale = ToFloat(raw); break;
                case "cameravelocityscale": r.cameraVelocityScale = ToFloat(raw); break;
                case "allowroll": r.allowRoll = ToBool(raw); break;
                case "maskinteraction": r.maskInteraction = ToEnum<SpriteMaskInteraction>(raw); break;
                case "lightprobeusage": r.lightProbeUsage = ToEnum<UnityEngine.Rendering.LightProbeUsage>(raw); break;
                case "reflectionprobeusage": r.reflectionProbeUsage = ToEnum<UnityEngine.Rendering.ReflectionProbeUsage>(raw); break;
                default: return null;
            }
            return "ok";
        }
    }
}