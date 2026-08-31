using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
using SimpleMCPBridge.Runtime;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // AssetBundleHotReplaceHandler 分类部署器（partial）：按类型分发 AB 资产（Step 2）
    public partial class AssetBundleHotReplaceHandler
    {
private static void DispatchShader(ReplaceContext ctx)
        {
            if (ctx.isolated)
            {
                foreach (var path in ctx.paths)
                {
                    var r = FindRenderer(path, ctx.errors);
                    if (r == null) continue;

                    if (ctx.saveBackup)
                    {
                        if (!ctx.backup.rendererSnapshot.ContainsKey(r))
                            ctx.backup.rendererSnapshot[r] = r.sharedMaterials;
                    }

                    if (ctx.dryRun)
                    {
                        var sm = r.sharedMaterials;
                        for (int i = 0; i < sm.Length; i++)
                        {
                            if (sm[i] == null) continue;
                            foreach (var ns in ctx.shaders)
                            {
                                var targetName = ctx.oldShaderName ?? ns.name;
                                if (sm[i].shader != null && sm[i].shader.name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                                    ctx.shadersSwapped++;
                            }
                        }
                    }
                    else
                    {
                        var m = r.materials;
                        bool touched = false;
                        for (int i = 0; i < m.Length; i++)
                        {
                            if (m[i] == null) continue;
                            foreach (var ns in ctx.shaders)
                            {
                                var targetName = ctx.oldShaderName ?? ns.name;
                                if (m[i].shader != null && m[i].shader.name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    m[i].shader = ns;
                                    ctx.shadersSwapped++;
                                    touched = true;
                                }
                            }
                        }
                        if (touched) r.materials = m;
                    }
                }
            }
            else
            {
                var allMats = Resources.FindObjectsOfTypeAll<Material>();
                foreach (var mat in allMats)
                {
                    if (mat == null || mat.shader == null) continue;
                    foreach (var ns in ctx.shaders)
                    {
                        var targetName = ctx.oldShaderName ?? ns.name;
                        if (mat.shader.name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (ctx.saveBackup)
                            {
                                if (!ctx.backup.shaderBackup.ContainsKey(mat))
                                    ctx.backup.shaderBackup[mat] = mat.shader;
                            }
                            if (!ctx.dryRun) mat.shader = ns;
                            ctx.shadersSwapped++;
                        }
                    }
                }
            }
        }

        private static void DispatchMaterial(ReplaceContext ctx)
        {
            if (ctx.isolated)
            {
                foreach (var path in ctx.paths)
                {
                    var r = FindRenderer(path, ctx.errors);
                    if (r == null) continue;

                    if (ctx.saveBackup)
                    {
                        if (!ctx.backup.rendererSnapshot.ContainsKey(r))
                            ctx.backup.rendererSnapshot[r] = r.sharedMaterials;
                    }

                    if (ctx.dryRun)
                    {
                        var sm = r.sharedMaterials;
                        for (int i = 0; i < sm.Length; i++)
                        {
                            if (sm[i] == null) continue;
                            foreach (var nm in ctx.materials)
                            {
                                if (sm[i].name == nm.name) ctx.materialsReplaced++;
                            }
                        }
                    }
                    else
                    {
                        var m = r.materials;
                        bool touched = false;
                        for (int i = 0; i < m.Length; i++)
                        {
                            if (m[i] == null) continue;
                            foreach (var nm in ctx.materials)
                            {
                                if (m[i].name == nm.name)
                                {
                                    m[i] = nm;
                                    ctx.materialsReplaced++;
                                    touched = true;
                                }
                            }
                        }
                        if (touched) r.materials = m;
                    }
                }
            }
            else
            {
                var allR = Resources.FindObjectsOfTypeAll<Renderer>();
                foreach (var r in allR)
                {
                    if (r == null) continue;
                    var sm = r.sharedMaterials;
                    bool touched = false;
                    for (int i = 0; i < sm.Length; i++)
                    {
                        if (sm[i] == null) continue;
                        foreach (var nm in ctx.materials)
                        {
                            if (sm[i].name == nm.name)
                            {
                                if (ctx.saveBackup)
                                {
                                    var key = (r, i);
                                    if (!ctx.backup.materialSlots.ContainsKey(key))
                                        ctx.backup.materialSlots[key] = sm[i];
                                }
                                if (!ctx.dryRun) sm[i] = nm;
                                ctx.materialsReplaced++;
                                touched = true;
                            }
                        }
                    }
                    if (touched && !ctx.dryRun) r.sharedMaterials = sm;
                }
            }
        }

        private static void DispatchTexture(ReplaceContext ctx)
        {
            var allMats = Resources.FindObjectsOfTypeAll<Material>();
            foreach (var mat in allMats)
            {
                if (mat == null || mat.shader == null) continue;
                var sdr = mat.shader;
                int cnt = sdr.GetPropertyCount();
                for (int p = 0; p < cnt; p++)
                {
                    if (sdr.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                    string pn = sdr.GetPropertyName(p);
                    var cur = mat.GetTexture(pn);
                    if (cur == null) continue;
                    foreach (var nt in ctx.textures)
                    {
                        if (cur.name == nt.name)
                        {
                            if (ctx.saveBackup)
                            {
                                var key = (mat, pn);
                                if (!ctx.backup.textureBackup.ContainsKey(key))
                                    ctx.backup.textureBackup[key] = cur;
                            }
                            if (!ctx.dryRun) mat.SetTexture(pn, nt);
                            ctx.texturesSwapped++;
                        }
                    }
                }
            }
        }

        private static void DispatchAudioClip(ReplaceContext ctx)
        {
            var srcs = Resources.FindObjectsOfTypeAll<AudioSource>();
            foreach (var src in srcs)
            {
                if (src == null || src.clip == null) continue;
                foreach (var nc in ctx.audioClips)
                {
                    if (src.clip.name == nc.name)
                    {
                        if (ctx.saveBackup)
                        {
                            if (!ctx.backup.audioBackup.ContainsKey(src))
                                ctx.backup.audioBackup[src] = src.clip;
                        }
                        if (!ctx.dryRun) src.clip = nc;
                        ctx.audioClipsReplaced++;
                    }
                }
            }
        }

        private static void DispatchMesh(ReplaceContext ctx)
        {
            var mfs = Resources.FindObjectsOfTypeAll<MeshFilter>();
            foreach (var mf in mfs)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                foreach (var nmh in ctx.meshes)
                {
                    if (mf.sharedMesh.name == nmh.name)
                    {
                        if (ctx.saveBackup)
                        {
                            if (!ctx.backup.meshBackup.ContainsKey(mf))
                                ctx.backup.meshBackup[mf] = mf.sharedMesh;
                        }
                        if (!ctx.dryRun) mf.sharedMesh = nmh;
                        ctx.meshesReplaced++;
                    }
                }
            }
        }

        private static void DispatchScriptableObject(ReplaceContext ctx)
        {
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var go in roots)
            {
                if (go == null) continue;
                var comps = go.GetComponentsInChildren<Component>(true);
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (!typeof(ScriptableObject).IsAssignableFrom(f.FieldType)) continue;
                        object val = null;
                        try { val = f.GetValue(comp); }
                        catch { continue; }
                        if (!(val is ScriptableObject sov)) continue;
                        foreach (var nso in ctx.scriptableObjects)
                        {
                            if (nso.name == sov.name && nso.GetType() == sov.GetType())
                            {
                                if (ctx.saveBackup)
                                {
                                    ctx.backup.soComponents.Add(comp);
                                    ctx.backup.soFieldNames.Add(f.Name);
                                    ctx.backup.soOriginals.Add(sov);
                                }
                                if (!ctx.dryRun)
                                {
                                    try { f.SetValue(comp, nso); }
                                    catch (Exception ex) { ctx.errors.Add("SO set failed: " + ex.Message); }
                                }
                                ctx.scriptableObjectsReplaced++;
                            }
                        }
                    }
                }
            }
        }

        private static void DispatchPrefab(ReplaceContext ctx)
        {
            foreach (var pf in ctx.prefabs)
            {
                if (ctx.dryRun)
                {
                    ctx.prefabsInstantiated++;
                    continue;
                }
                GameObject inst = null;
                try
                {
                    inst = ctx.parent != null
                        ? UnityEngine.Object.Instantiate(pf, ctx.position, ctx.rotation, ctx.parent)
                        : UnityEngine.Object.Instantiate(pf, ctx.position, ctx.rotation);
                }
                catch (Exception ex)
                {
                    ctx.errors.Add("instantiate failed: " + ex.Message);
                    continue;
                }
                ctx.prefabsInstantiated++;
                var instId = inst.GetInstanceID();
                ctx.instanceIds.Add(instId.ToString(CultureInfo.InvariantCulture));
                if (ctx.saveBackup)
                    ctx.backup.prefabInstances.Add(inst);
            }
        }

        
    }
}
