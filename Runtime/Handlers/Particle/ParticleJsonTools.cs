using SimpleMCPBridge.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers.Particle
{
    // Particle JSON 序列化组（ParticleHandler 拆分 Step 2）：模块状态 → wire JSON
    public static partial class ParticleJsonTools
    {
public static string CurveJson(ParticleSystem.MinMaxCurve c)
        {
            if (c.mode == ParticleSystemCurveMode.Constant)
                return c.constant.ToString("G", CultureInfo.InvariantCulture);
            if (c.mode == ParticleSystemCurveMode.TwoConstants)
                return $"[{c.constantMin.ToString("G", CultureInfo.InvariantCulture)},{c.constantMax.ToString("G", CultureInfo.InvariantCulture)}]";
            if (c.mode == ParticleSystemCurveMode.Curve)
                return CurveKeysJson(c.curveMin);
            // TwoCurves / RandomConstant:只返回模式名(曲线数据不在单个 curve 上)
            return JsonHelper.EscapeString(c.mode.ToString());
        }

        /// <summary>AnimationCurve → [[t,v],...] JSON 数组(与 ToCurve 输入格式一致,可直接写回)。</summary>
        public static string CurveKeysJson(AnimationCurve curve)
        {
            if (curve == null || curve.keys == null || curve.keys.Length == 0)
                return JsonHelper.EscapeString("Curve");
            var parts = new List<string>();
            foreach (var k in curve.keys)
                parts.Add($"[{k.time.ToString("G", CultureInfo.InvariantCulture)},{k.value.ToString("G", CultureInfo.InvariantCulture)}]");
            return "[" + string.Join(",", parts) + "]";
        }

        public static string ColorHex(Color c)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(c)}";
        }

        public static string GradientJson(ParticleSystem.MinMaxGradient g)
        {
            if (g.mode == ParticleSystemGradientMode.Color)
                return JsonHelper.EscapeString(ColorHex(g.color));
            if (g.mode == ParticleSystemGradientMode.TwoColors)
                return JsonHelper.EscapeString($"[{ColorHex(g.colorMin)},{ColorHex(g.colorMax)}]");
            if (g.mode == ParticleSystemGradientMode.Gradient)
                return GradientKeysJson(g.gradient);
            if (g.mode == ParticleSystemGradientMode.TwoGradients)
                return GradientKeysJson(g.gradientMin);
            // RandomColor:只返回模式名
            return JsonHelper.EscapeString(g.mode.ToString());
        }

        /// <summary>Gradient → [[t,"#RRGGBBAA"],...] JSON 数组(与 ToGradient 输入格式一致,可直接写回)。</summary>
        public static string GradientKeysJson(Gradient gradient)
        {
            if (gradient == null || gradient.colorKeys == null || gradient.colorKeys.Length == 0)
                return JsonHelper.EscapeString("Gradient");
            var parts = new List<string>();
            foreach (var ck in gradient.colorKeys)
                parts.Add($"[{ck.time.ToString("G", CultureInfo.InvariantCulture)},{JsonHelper.EscapeString(ColorHex(ck.color))}]");
            return "[" + string.Join(",", parts) + "]";
        }

        /// <summary>模块 JSON:{"enabled":bool, ...键值对}。</summary>
        public static string ModuleJson(bool enabled, params (string, string)[] kv)
        {
            var entries = new List<(string, string)> { ("enabled", JsonHelper.BoolJson(enabled)) };
            entries.AddRange(kv);
            return JsonHelper.BuildJsonObject(entries.ToArray());
        }

        /// <summary>子发射器 JSON:{"enabled":bool, "birth0":"Name", ..., "death1":...} — 6 槽逐槽读回(set 侧同名可写)。</summary>
        public static string SubEmittersJson(ParticleSystem ps)
        {
            var se = ps.subEmitters;
            var entries = new List<(string, string)> { ("enabled", JsonHelper.BoolJson(se.enabled)) };
            string[] names = { "birth0", "birth1", "collision0", "collision1", "death0", "death1" };
            int count = Math.Min(se.subEmittersCount, names.Length);
            for (int i = 0; i < count; i++)
            {
                var sub = se.GetSubEmitterSystem(i);
                if (sub != null)
                    entries.Add((names[i], JsonHelper.EscapeString(sub.gameObject.name)));
            }
            return JsonHelper.BuildJsonObject(entries.ToArray());
        }

        /// <summary>emission bursts → [[count,time],...] JSON 数组(与 set 侧 burst 语法一致,可直接写回)。</summary>
        public static string EmissionBurstsJson(ParticleSystem.EmissionModule e)
        {
            var bursts = new ParticleSystem.Burst[e.burstCount];
            e.GetBursts(bursts);
            if (bursts.Length == 0) return "[]";
            var parts = new List<string>();
            foreach (var b in bursts)
            {
                var count = b.count.constant.ToString("G", CultureInfo.InvariantCulture);
                parts.Add($"[{count},{b.time.ToString("G", CultureInfo.InvariantCulture)}]");
            }
            return "[" + string.Join(",", parts) + "]";
        }

        public static string GetEnabledModules(ParticleSystem ps)
        {
            var list = new List<string>();
            if (ps.emission.enabled) list.Add("emission");
            if (ps.shape.enabled) list.Add("shape");
            if (ps.velocityOverLifetime.enabled) list.Add("velocityOverLifetime");
            if (ps.limitVelocityOverLifetime.enabled) list.Add("limitVelocityOverLifetime");
            if (ps.inheritVelocity.enabled) list.Add("inheritVelocity");
            if (ps.lifetimeByEmitterSpeed.enabled) list.Add("lifetimeByEmitterSpeed");
            if (ps.forceOverLifetime.enabled) list.Add("forceOverLifetime");
            if (ps.colorOverLifetime.enabled) list.Add("colorOverLifetime");
            if (ps.colorBySpeed.enabled) list.Add("colorBySpeed");
            if (ps.sizeOverLifetime.enabled) list.Add("sizeOverLifetime");
            if (ps.sizeBySpeed.enabled) list.Add("sizeBySpeed");
            if (ps.rotationOverLifetime.enabled) list.Add("rotationOverLifetime");
            if (ps.rotationBySpeed.enabled) list.Add("rotationBySpeed");
            if (ps.externalForces.enabled) list.Add("externalForces");
            if (ps.noise.enabled) list.Add("noise");
            if (ps.collision.enabled) list.Add("collision");
            if (ps.trigger.enabled) list.Add("trigger");
            if (ps.subEmitters.enabled) list.Add("subEmitters");
            if (ps.textureSheetAnimation.enabled) list.Add("textureSheetAnimation");
            if (ps.lights.enabled) list.Add("lights");
            if (ps.trails.enabled) list.Add("trails");
            if (ps.customData.enabled) list.Add("customData");
            return string.Join(",", list);
        }

        public static string BuildSystemJson(ParticleSystem ps, bool detailed)
        {
            var m = ps.main;
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var entries = new List<(string, string)>
            {
                ("name", JsonHelper.EscapeString(ps.gameObject.name)),
                ("path", JsonHelper.EscapeString(GetObjectPath(ps.gameObject))),
                ("instanceId", ps.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("isPlaying", JsonHelper.BoolJson(ps.isPlaying)),
                ("isPaused", JsonHelper.BoolJson(ps.isPaused)),
                ("isStopped", JsonHelper.BoolJson(ps.isStopped)),
                ("isEmitting", JsonHelper.BoolJson(ps.isEmitting)),
                ("particleCount", ps.particleCount.ToString(CultureInfo.InvariantCulture)),
                ("time", ps.time.ToString("G", CultureInfo.InvariantCulture)),
                ("totalTime", ps.totalTime.ToString("G", CultureInfo.InvariantCulture)),
                ("duration", m.duration.ToString("G", CultureInfo.InvariantCulture)),
                ("loop", JsonHelper.BoolJson(m.loop)),
                ("playOnAwake", JsonHelper.BoolJson(m.playOnAwake)),
                ("maxParticles", m.maxParticles.ToString(CultureInfo.InvariantCulture)),
                ("simulationSpace", JsonHelper.EscapeString(m.simulationSpace.ToString())),
                ("enabledModules", JsonHelper.EscapeString(GetEnabledModules(ps)))
            };

            if (renderer != null)
            {
                entries.Add(("rendererEnabled", JsonHelper.BoolJson(renderer.enabled)));
                entries.Add(("renderMode", JsonHelper.EscapeString(renderer.renderMode.ToString())));
                entries.Add(("sortingLayerName", JsonHelper.EscapeString(renderer.sortingLayerName)));
                entries.Add(("sortingOrder", renderer.sortingOrder.ToString(CultureInfo.InvariantCulture)));
            }

            if (detailed)
            {
                entries.Add(("prewarm", JsonHelper.BoolJson(m.prewarm)));
                entries.Add(("startDelay", CurveJson(m.startDelay)));
                entries.Add(("startLifetime", CurveJson(m.startLifetime)));
                entries.Add(("startSpeed", CurveJson(m.startSpeed)));
                entries.Add(("startSize", CurveJson(m.startSize)));
                entries.Add(("startRotation", CurveJson(m.startRotation)));
                entries.Add(("startColor", GradientJson(m.startColor)));
                entries.Add(("gravityModifier", CurveJson(m.gravityModifier)));
                entries.Add(("simulationSpeed", m.simulationSpeed.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("useUnscaledTime", JsonHelper.BoolJson(m.useUnscaledTime)));
                entries.Add(("scalingMode", JsonHelper.EscapeString(m.scalingMode.ToString())));
                entries.Add(("stopAction", JsonHelper.EscapeString(m.stopAction.ToString())));
                entries.Add(("startSize3D", JsonHelper.BoolJson(m.startSize3D)));
                entries.Add(("startRotation3D", JsonHelper.BoolJson(m.startRotation3D)));
                entries.Add(("cullingMode", JsonHelper.EscapeString(m.cullingMode.ToString())));
                entries.Add(("ringBufferMode", JsonHelper.EscapeString(m.ringBufferMode.ToString())));
                entries.Add(("emitterVelocityMode", JsonHelper.EscapeString(m.emitterVelocityMode.ToString())));
                entries.Add(("gravitySource", JsonHelper.EscapeString(m.gravitySource.ToString())));
                entries.Add(("rateOverTime", CurveJson(ps.emission.rateOverTime)));
                entries.Add(("rateOverDistance", CurveJson(ps.emission.rateOverDistance)));
                entries.Add(("burstCount", ps.emission.burstCount.ToString(CultureInfo.InvariantCulture)));
                entries.Add(("bursts", EmissionBurstsJson(ps.emission)));
                entries.Add(("shapeType", JsonHelper.EscapeString(ps.shape.shapeType.ToString())));
                entries.Add(("shapeRadius", ps.shape.radius.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeAngle", ps.shape.angle.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeMeshShapeType", JsonHelper.EscapeString(ps.shape.meshShapeType.ToString())));
                entries.Add(("shapeRandomDirectionAmount", ps.shape.randomDirectionAmount.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeSphericalDirectionAmount", ps.shape.sphericalDirectionAmount.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeAlignToDirection", JsonHelper.BoolJson(ps.shape.alignToDirection)));
                entries.Add(("shapeArcMode", JsonHelper.EscapeString(ps.shape.arcMode.ToString())));
                entries.Add(("shapeArc", ps.shape.arc.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeBoxThickness", $"[{ps.shape.boxThickness.x.ToString("G", CultureInfo.InvariantCulture)},{ps.shape.boxThickness.y.ToString("G", CultureInfo.InvariantCulture)},{ps.shape.boxThickness.z.ToString("G", CultureInfo.InvariantCulture)}]"));
                entries.Add(("shapeLength", ps.shape.length.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeDonutRadius", ps.shape.donutRadius.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeRadiusThickness", ps.shape.radiusThickness.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeArcSpread", ps.shape.arcSpread.ToString("G", CultureInfo.InvariantCulture)));
                entries.Add(("shapeArcSpeed", CurveJson(ps.shape.arcSpeed)));
                entries.Add(("velocityOverLifetime", ModuleJson(
                    ps.velocityOverLifetime.enabled,
                    ("x", CurveJson(ps.velocityOverLifetime.x)),
                    ("y", CurveJson(ps.velocityOverLifetime.y)),
                    ("z", CurveJson(ps.velocityOverLifetime.z)),
                    ("space", JsonHelper.EscapeString(ps.velocityOverLifetime.space.ToString())))));
                entries.Add(("forceOverLifetime", ModuleJson(
                    ps.forceOverLifetime.enabled,
                    ("x", CurveJson(ps.forceOverLifetime.x)),
                    ("y", CurveJson(ps.forceOverLifetime.y)),
                    ("z", CurveJson(ps.forceOverLifetime.z)),
                    ("space", JsonHelper.EscapeString(ps.forceOverLifetime.space.ToString())))));
                entries.Add(("colorOverLifetime", ModuleJson(
                    ps.colorOverLifetime.enabled,
                    ("color", GradientJson(ps.colorOverLifetime.color)))));
                entries.Add(("colorBySpeed", ModuleJson(
                    ps.colorBySpeed.enabled,
                    ("color", GradientJson(ps.colorBySpeed.color)))));
                entries.Add(("sizeOverLifetime", ModuleJson(
                    ps.sizeOverLifetime.enabled,
                    ("size", CurveJson(ps.sizeOverLifetime.size)))));
                entries.Add(("sizeBySpeed", ModuleJson(
                    ps.sizeBySpeed.enabled,
                    ("size", CurveJson(ps.sizeBySpeed.size)))));
                entries.Add(("rotationOverLifetime", ModuleJson(
                    ps.rotationOverLifetime.enabled,
                    ("separateAxes", JsonHelper.BoolJson(ps.rotationOverLifetime.separateAxes)),
                    ("x", CurveJson(ps.rotationOverLifetime.x)),
                    ("y", CurveJson(ps.rotationOverLifetime.y)),
                    ("z", CurveJson(ps.rotationOverLifetime.z)))));
                entries.Add(("rotationBySpeed", ModuleJson(
                    ps.rotationBySpeed.enabled,
                    ("separateAxes", JsonHelper.BoolJson(ps.rotationBySpeed.separateAxes)),
                    ("x", CurveJson(ps.rotationBySpeed.x)),
                    ("y", CurveJson(ps.rotationBySpeed.y)),
                    ("z", CurveJson(ps.rotationBySpeed.z)))));
                entries.Add(("lifetimeByEmitterSpeed", ModuleJson(
                    ps.lifetimeByEmitterSpeed.enabled,
                    ("curveMultiplier", ps.lifetimeByEmitterSpeed.curveMultiplier.ToString("G", CultureInfo.InvariantCulture)),
                    ("curve", CurveJson(ps.lifetimeByEmitterSpeed.curve)),
                    ("range", $"[{ps.lifetimeByEmitterSpeed.range.x.ToString("G", CultureInfo.InvariantCulture)},{ps.lifetimeByEmitterSpeed.range.y.ToString("G", CultureInfo.InvariantCulture)}]"))));
                entries.Add(("noise", ModuleJson(
                    ps.noise.enabled,
                    ("strength", CurveJson(ps.noise.strength)),
                    ("frequency", ps.noise.frequency.ToString("G", CultureInfo.InvariantCulture)),
                    ("damping", JsonHelper.BoolJson(ps.noise.damping)),
                    ("octaves", ps.noise.octaveCount.ToString(CultureInfo.InvariantCulture)),
                    ("octaveMultiplier", ps.noise.octaveMultiplier.ToString("G", CultureInfo.InvariantCulture)),
                    ("octaveScale", ps.noise.octaveScale.ToString("G", CultureInfo.InvariantCulture)),
                    ("quality", JsonHelper.EscapeString(ps.noise.quality.ToString())),
                    ("scrollSpeed", CurveJson(ps.noise.scrollSpeed)))));
                entries.Add(("trails", ModuleJson(
                    ps.trails.enabled,
                    ("lifetime", CurveJson(ps.trails.lifetime)),
                    ("widthOverTrail", CurveJson(ps.trails.widthOverTrail)),
                    ("colorOverTrail", GradientJson(ps.trails.colorOverTrail)),
                    ("minVertexDistance", ps.trails.minVertexDistance.ToString("G", CultureInfo.InvariantCulture)),
                    ("dieWithParticles", JsonHelper.BoolJson(ps.trails.dieWithParticles)),
                    ("ribbonCount", ps.trails.ribbonCount.ToString(CultureInfo.InvariantCulture)),
                    ("textureMode", JsonHelper.EscapeString(ps.trails.textureMode.ToString())),
                    ("worldSpace", JsonHelper.BoolJson(ps.trails.worldSpace)),
                    ("generateLightingData", JsonHelper.BoolJson(ps.trails.generateLightingData)))));
                entries.Add(("textureSheetAnimation", ModuleJson(
                    ps.textureSheetAnimation.enabled,
                    ("mode", JsonHelper.EscapeString(ps.textureSheetAnimation.mode.ToString())),
                    ("fps", ps.textureSheetAnimation.fps.ToString("G", CultureInfo.InvariantCulture)),
                    ("numTilesX", ps.textureSheetAnimation.numTilesX.ToString(CultureInfo.InvariantCulture)),
                    ("numTilesY", ps.textureSheetAnimation.numTilesY.ToString(CultureInfo.InvariantCulture)),
                    ("rowIndex", ps.textureSheetAnimation.rowIndex.ToString(CultureInfo.InvariantCulture)),
                    ("startFrame", CurveJson(ps.textureSheetAnimation.startFrame)),
                    ("frameOverTime", CurveJson(ps.textureSheetAnimation.frameOverTime)),
                    ("flipU", ps.textureSheetAnimation.flipU.ToString("G", CultureInfo.InvariantCulture)),
                    ("flipV", ps.textureSheetAnimation.flipV.ToString("G", CultureInfo.InvariantCulture)),
                    ("rowMode", JsonHelper.EscapeString(ps.textureSheetAnimation.rowMode.ToString())),
                    ("timeMode", JsonHelper.EscapeString(ps.textureSheetAnimation.timeMode.ToString())),
                    ("speedRange", $"[{ps.textureSheetAnimation.speedRange.x.ToString("G", CultureInfo.InvariantCulture)},{ps.textureSheetAnimation.speedRange.y.ToString("G", CultureInfo.InvariantCulture)}]"))));
                entries.Add(("lights", ModuleJson(
                    ps.lights.enabled,
                    ("ratio", ps.lights.ratio.ToString("G", CultureInfo.InvariantCulture)),
                    ("intensityMultiplier", ps.lights.intensityMultiplier.ToString("G", CultureInfo.InvariantCulture)),
                    ("rangeMultiplier", ps.lights.rangeMultiplier.ToString("G", CultureInfo.InvariantCulture)),
                    ("maxLights", ps.lights.maxLights.ToString(CultureInfo.InvariantCulture)),
                    ("useParticleColor", JsonHelper.BoolJson(ps.lights.useParticleColor)))));
                entries.Add(("collision", ModuleJson(
                    ps.collision.enabled,
                    ("type", JsonHelper.EscapeString(ps.collision.type.ToString())),
                    ("mode", JsonHelper.EscapeString(ps.collision.mode.ToString())),
                    ("bounce", CurveJson(ps.collision.bounce)),
                    ("lifetimeLoss", CurveJson(ps.collision.lifetimeLoss)),
                    ("minKillSpeed", ps.collision.minKillSpeed.ToString("G", CultureInfo.InvariantCulture)),
                    ("maxKillSpeed", ps.collision.maxKillSpeed.ToString("G", CultureInfo.InvariantCulture)),
                    ("colliderForce", ps.collision.colliderForce.ToString("G", CultureInfo.InvariantCulture)),
                    ("dampen", CurveJson(ps.collision.dampen)),
                    ("quality", JsonHelper.EscapeString(ps.collision.quality.ToString())),
                    ("collidesWith", ps.collision.collidesWith.value.ToString(CultureInfo.InvariantCulture)),
                    ("maxCollisionShapes", ps.collision.maxCollisionShapes.ToString(CultureInfo.InvariantCulture)),
                    ("multiplyColliderForceByCollisionAngle", JsonHelper.BoolJson(ps.collision.multiplyColliderForceByCollisionAngle)),
                    ("multiplyColliderForceByParticleSpeed", JsonHelper.BoolJson(ps.collision.multiplyColliderForceByParticleSpeed)),
                    ("multiplyColliderForceByParticleSize", JsonHelper.BoolJson(ps.collision.multiplyColliderForceByParticleSize)),
                    ("sendCollisionMessages", JsonHelper.BoolJson(ps.collision.sendCollisionMessages)),
                    ("voxelSize", ps.collision.voxelSize.ToString("G", CultureInfo.InvariantCulture)),
                    ("enableDynamicColliders", JsonHelper.BoolJson(ps.collision.enableDynamicColliders)),
                    ("enableInteriorCollisions", JsonHelper.BoolJson(ps.collision.enableInteriorCollisions)))));
                entries.Add(("externalForces", ModuleJson(
                    ps.externalForces.enabled,
                    ("multiplier", ps.externalForces.multiplier.ToString("G", CultureInfo.InvariantCulture)),
                    ("influenceFilter", JsonHelper.EscapeString(ps.externalForces.influenceFilter.ToString())),
                    ("influenceMask", ps.externalForces.influenceMask.value.ToString(CultureInfo.InvariantCulture)))));
                entries.Add(("subEmitters", SubEmittersJson(ps)));
                entries.Add(("inheritVelocity", ModuleJson(
                    ps.inheritVelocity.enabled,
                    ("mode", JsonHelper.EscapeString(ps.inheritVelocity.mode.ToString())),
                    ("curve", CurveJson(ps.inheritVelocity.curve)))));
                entries.Add(("limitVelocityOverLifetime", ModuleJson(
                    ps.limitVelocityOverLifetime.enabled,
                    ("limit", CurveJson(ps.limitVelocityOverLifetime.limit)),
                    ("limitX", CurveJson(ps.limitVelocityOverLifetime.limitX)),
                    ("limitY", CurveJson(ps.limitVelocityOverLifetime.limitY)),
                    ("limitZ", CurveJson(ps.limitVelocityOverLifetime.limitZ)),
                    ("dampen", ps.limitVelocityOverLifetime.dampen.ToString("G", CultureInfo.InvariantCulture)),
                    ("separateAxes", JsonHelper.BoolJson(ps.limitVelocityOverLifetime.separateAxes)),
                    ("space", JsonHelper.EscapeString(ps.limitVelocityOverLifetime.space.ToString())))));
                entries.Add(("customData", ModuleJson(
                    ps.customData.enabled,
                    ("mode0", JsonHelper.EscapeString(ps.customData.GetMode(ParticleSystemCustomData.Custom1).ToString())),
                    ("mode1", JsonHelper.EscapeString(ps.customData.GetMode(ParticleSystemCustomData.Custom2).ToString())),
                    ("vector0", CurveJson(ps.customData.GetVector(ParticleSystemCustomData.Custom1, 0))),
                    ("vector1", CurveJson(ps.customData.GetVector(ParticleSystemCustomData.Custom2, 0))),
                    ("color0", GradientJson(ps.customData.GetColor(ParticleSystemCustomData.Custom1))),
                    ("color1", GradientJson(ps.customData.GetColor(ParticleSystemCustomData.Custom2))))));
                entries.Add(("trigger", ModuleJson(
                    ps.trigger.enabled,
                    ("inside", JsonHelper.EscapeString(ps.trigger.inside.ToString())),
                    ("outside", JsonHelper.EscapeString(ps.trigger.outside.ToString())),
                    ("enter", JsonHelper.EscapeString(ps.trigger.enter.ToString())),
                    ("exit", JsonHelper.EscapeString(ps.trigger.exit.ToString())))));
                if (renderer != null)
                {
                    entries.Add(("rendererMaterial", JsonHelper.EscapeString(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "")));
                    entries.Add(("rendererTrailMaterial", JsonHelper.EscapeString(renderer.trailMaterial != null ? renderer.trailMaterial.name : "")));
                    entries.Add(("rendererMesh", JsonHelper.EscapeString(renderer.mesh != null ? renderer.mesh.name : "")));
                    entries.Add(("rendererMinParticleSize", renderer.minParticleSize.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererMaxParticleSize", renderer.maxParticleSize.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererNormalDirection", renderer.normalDirection.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererReceiveShadows", JsonHelper.BoolJson(renderer.receiveShadows)));
                    entries.Add(("rendererSortingFudge", renderer.sortingFudge.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererPivot", JsonHelper.EscapeString(renderer.pivot.ToString("G", CultureInfo.InvariantCulture))));
                    entries.Add(("rendererVelocityScale", renderer.velocityScale.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererLengthScale", renderer.lengthScale.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererCameraVelocityScale", renderer.cameraVelocityScale.ToString("G", CultureInfo.InvariantCulture)));
                    entries.Add(("rendererAllowRoll", JsonHelper.BoolJson(renderer.allowRoll)));
                    entries.Add(("rendererMaskInteraction", JsonHelper.EscapeString(renderer.maskInteraction.ToString())));
                    entries.Add(("rendererLightProbeUsage", JsonHelper.EscapeString(renderer.lightProbeUsage.ToString())));
                    entries.Add(("rendererReflectionProbeUsage", JsonHelper.EscapeString(renderer.reflectionProbeUsage.ToString())));
                }
            }

            return JsonHelper.BuildJsonObject(entries.ToArray());
        }

        // ── 材质解析 ──

        /// <summary>
        /// 解析 shader 参数:shader 名(Shader.Find)或 Assets/... 路径(Editor 下 AssetDatabase 加载资产)。
        /// 找不到抛异常(不静默回退,显式指定必须命中)。
        /// </summary>
        
    }
}
