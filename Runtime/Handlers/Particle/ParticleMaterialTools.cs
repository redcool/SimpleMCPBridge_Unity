using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimpleMCPBridge.Runtime.Handlers.Particle
{
    // Particle 材质/Shader 资产化组（ParticleHandler 拆分 Step 3）：Editor 资产化 + shader 解析
    public static partial class ParticleMaterialTools
    {
public static Shader ResolveShader(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var trimmed = spec.Trim();
#if UNITY_EDITOR
            if (trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(trimmed);
                if (asset != null) return asset;
            }
#endif
            var found = Shader.Find(trimmed);
            if (found != null) return found;
            throw new ArgumentException($"Shader '{trimmed}' not found. Use a shader name (Shader.Find) or an Assets/... path");
        }

        /// <summary>
        /// 解析材质参数并赋值给 ParticleSystemRenderer。
        /// 创作路径(Editor,非 Play):必须是磁盘资产 —— Assets/... 路径直接加载;
        /// 内置名(如 Default-Particle)在特效资产同目录创建/复用 `<对象名>_<材质名>.mat`(写入磁盘,
        /// 特效 prefab 引用资产,绝不使用材质实例)。Runtime(Player 或 Editor Play Mode)允许材质实例。
        /// shaderSpec 可选:创建材质时指定 shader(缺省 = URP Simple Lit 链)。
        /// </summary>
        public static Material ResolveMaterial(ParticleSystem ps, string spec, string materialDir, string shaderSpec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var trimmed = spec.Trim();
#if UNITY_EDITOR
            if (trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(trimmed);
                if (asset == null)
                    throw new ArgumentException($"Material asset not found: '{trimmed}'");
                return asset;
            }
            if (!Application.isPlaying)
                return EnsureMaterialAsset(ps.gameObject, trimmed, materialDir, shaderSpec);
#endif
            // Runtime(Player 或 Editor Play Mode):材质实例允许(播放态特效本来就是临时的)
            var shader = ResolveShader(shaderSpec) ?? DefaultParticleShader();
            if (shader == null)
                throw new ArgumentException(
                    "No particle shader available (Universal Render Pipeline/Particles/Simple Lit or Legacy Shaders/Particles/Alpha Blended)");
            var mat = new Material(shader);
            mat.name = trimmed;
            return mat;
        }

        /// <summary>
        /// 默认粒子 shader 链:URP Simple Lit → Legacy Particles Alpha Blended。
        /// </summary>
        public static Shader DefaultParticleShader()
        {
            return Shader.Find("Universal Render Pipeline/Particles/Simple Lit")
                   ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        }

        /// <summary>
        /// Editor 下按 Assets/... 路径加载资产(Mesh/Sprite/Texture2D 等);非 Editor 抛错。
        /// </summary>
        public static T LoadAsset<T>(object raw) where T : UnityEngine.Object
        {
            var path = raw?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Asset path required (Assets/...)");
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path.Trim());
            if (asset == null)
                throw new ArgumentException($"Asset not found at path: '{path.Trim()}'");
            return asset;
#else
            throw new ArgumentException($"Asset path '{path}' requires Editor (runtime asset loading not supported for this property)");
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor 创作路径:把内置材质名落地为磁盘资产(特效资产同目录或显式 materialDir),
        /// 已存在则复用(复用不换 shader —— 资产语义)。特效 prefab / GameObject 一律引用资产,不用材质实例。
        /// shaderSpec 可选:仅创建时生效,缺省 = DefaultParticleShader()。
        /// </summary>
        public static Material EnsureMaterialAsset(GameObject go, string spec, string materialDir, string shaderSpec)
        {
            var dir = materialDir;
            if (string.IsNullOrEmpty(dir))
            {
                // 优先:特效 prefab 源资产同目录(修改已有特效 prefab 的场景)
                var prefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (prefab != null)
                {
                    var p = UnityEditor.AssetDatabase.GetAssetPath(prefab);
                    if (!string.IsNullOrEmpty(p))
                        dir = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/');
                }
            }
            if (string.IsNullOrEmpty(dir)) dir = "Assets";

            var safeName = (go.name + "_" + spec).Replace("/", "_").Replace("\\", "_").Replace(":", "_");
            var fullPath = $"{dir.TrimEnd('/')}/{safeName}.mat";

            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(fullPath);
            if (existing != null) return existing;

            var shader = ResolveShader(shaderSpec) ?? DefaultParticleShader();
            if (shader == null)
                throw new ArgumentException("No particle shader available (Universal Render Pipeline/Particles/Simple Lit or Legacy Shaders/Particles/Alpha Blended)");

            System.IO.Directory.CreateDirectory(System.IO.Path.GetFullPath(dir));
            var mat = new Material(shader);
            mat.name = spec;
            UnityEditor.AssetDatabase.CreateAsset(mat, fullPath);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();
            return mat;
        }
#endif

        public static void ApplyMaterial(ParticleSystem ps, string materialSpec, string materialDir, string shaderSpec)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r == null) throw new ArgumentException("No ParticleSystemRenderer on this system");
            var mat = ResolveMaterial(ps, materialSpec, materialDir, shaderSpec);
            SceneObjectTools.UndoRecord(r, "Set particle material");
            r.sharedMaterial = mat;
        }

        /// <summary>
        /// 换特效材质的 shader(particle.set renderer.shader)。
        /// Editor 下材质是资产时,修改资产并持久化(AssetDatabase.Contains → SetDirty + SaveAssets);
        /// runtime 材质实例直接改。仅改 shader 字段,不动材质引用。
        /// </summary>
        public static void SetRendererShader(ParticleSystemRenderer r, string shaderSpec)
        {
            var shader = ResolveShader(shaderSpec);
            if (shader == null)
                throw new ArgumentException($"Shader '{shaderSpec}' not found");
            var mat = r.sharedMaterial;
            if (mat == null)
                throw new ArgumentException("No material on renderer — set material first (renderer.material)");
            SceneObjectTools.UndoRecord(mat, "Set material shader");
            mat.shader = shader;
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.Contains(mat))
            {
                UnityEditor.EditorUtility.SetDirty(mat);
                UnityEditor.AssetDatabase.SaveAssets();
            }
#endif
        }

    }
}