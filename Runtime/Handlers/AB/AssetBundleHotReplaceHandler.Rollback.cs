using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using SimpleMCPBridge.Runtime;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // AssetBundleHotReplaceHandler 回滚器（partial）：assetbundle.rollback + Restore*（Step 3）
    public partial class AssetBundleHotReplaceHandler
    {
[MCPTool(MCPMethodConst.ASSETBUNDLE_ROLLBACK, "Rollback a previous assetbundle.hot_replace operation. "
            + "Requires that the original call had saveBackup:true. "
            + "Params: id (req — the replaceId returned by hot_replace). "
            + "Returns per-type rollback counts and any errors. "
            + "Note: the AssetBundle itself stays loaded — call assetbundle.unload_all to free it afterward.",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string Rollback(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var id = GetRequiredString(args, "id");
            if (string.IsNullOrEmpty(id))
                return ErrorJson("Missing required parameter: 'id'");

            HotReplaceBackup backup;
            lock (_stateLock)
            {
                if (!_backups.TryGetValue(id, out backup))
                    return ErrorJson("No backup found for id '" + id + "'. "
                        + "Make sure the original assetbundle.hot_replace call had saveBackup:true " +
                        "and the operation completed.");
            }

            int shaderRestored = 0, matRestored = 0, texRestored = 0,
                audioRestored = 0, meshRestored = 0, soRestored = 0,
                prefabDestroyed = 0;
            var errors = new List<string>();
            var invariant = CultureInfo.InvariantCulture;

            RestoreShaders(backup, errors, ref shaderRestored);
            RestoreRenderers(backup, errors, ref matRestored);
            RestoreMaterialSlots(backup, errors, ref matRestored);
            RestoreTextures(backup, errors, ref texRestored);
            RestoreAudioClips(backup, errors, ref audioRestored);
            RestoreMeshes(backup, errors, ref meshRestored);
            RestoreScriptableObjects(backup, errors, ref soRestored);
            DestroyPrefabs(backup, errors, ref prefabDestroyed);

            lock (_stateLock) { _backups.Remove(id); }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("replaceId", JsonHelper.EscapeString(id)),
                ("shadersRestored", shaderRestored.ToString(invariant)),
                ("materialsRestored", matRestored.ToString(invariant)),
                ("texturesRestored", texRestored.ToString(invariant)),
                ("audioClipsRestored", audioRestored.ToString(invariant)),
                ("meshesRestored", meshRestored.ToString(invariant)),
                ("scriptableObjectsRestored", soRestored.ToString(invariant)),
                ("prefabsDestroyed", prefabDestroyed.ToString(invariant)),
                ("errors", JsonHelper.BuildJsonArray(
                    errors.Count > 0 ? errors.Select(e => JsonHelper.EscapeString(e)).ToArray() : new string[0]))
            );
        }

        // ─── Rollback helpers ──────────────────────────────────────────────

        private static void RestoreShaders(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.shaderBackup)
            {
                if (kv.Key == null) { errors.Add("shader: material was destroyed"); continue; }
                kv.Key.shader = kv.Value;
                count++;
            }
        }

        private static void RestoreRenderers(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.rendererSnapshot)
            {
                if (kv.Key == null) { errors.Add("renderer was destroyed"); continue; }
                kv.Key.sharedMaterials = kv.Value;
                count += kv.Value.Length;
            }
        }

        private static void RestoreMaterialSlots(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.materialSlots)
            {
                var r = kv.Key.Item1;
                var slot = kv.Key.Item2;
                if (r == null) { errors.Add("material slot: renderer was destroyed"); continue; }
                var sm = r.sharedMaterials;
                if (slot < 0 || slot >= sm.Length)
                { errors.Add("material slot " + slot + " out of range on renderer"); continue; }
                sm[slot] = kv.Value;
                r.sharedMaterials = sm;
                count++;
            }
        }

        private static void RestoreTextures(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.textureBackup)
            {
                var mat = kv.Key.Item1;
                if (mat == null) { errors.Add("texture: material was destroyed"); continue; }
                mat.SetTexture(kv.Key.Item2, kv.Value);
                count++;
            }
        }

        private static void RestoreAudioClips(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.audioBackup)
            {
                if (kv.Key == null) { errors.Add("audioSource was destroyed"); continue; }
                kv.Key.clip = kv.Value;
                count++;
            }
        }

        private static void RestoreMeshes(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var kv in backup.meshBackup)
            {
                if (kv.Key == null) { errors.Add("meshFilter was destroyed"); continue; }
                kv.Key.sharedMesh = kv.Value;
                count++;
            }
        }

        private static void RestoreScriptableObjects(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            for (int i = 0; i < backup.soComponents.Count; i++)
            {
                var comp = backup.soComponents[i];
                if (comp == null) { errors.Add("SO component was destroyed"); continue; }
                var field = comp.GetType().GetField(backup.soFieldNames[i],
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                { errors.Add("SO field '" + backup.soFieldNames[i] + "' not found on " + comp.GetType().FullName); continue; }
                try { field.SetValue(comp, backup.soOriginals[i]); count++; }
                catch (Exception ex) { errors.Add("SO restore failed: " + ex.Message); }
            }
        }

        private static void DestroyPrefabs(HotReplaceBackup backup, List<string> errors, ref int count)
        {
            foreach (var go in backup.prefabInstances)
            {
                if (go == null) continue;
                try { GameObject.Destroy(go); count++; }
                catch (Exception ex) { errors.Add("prefab destroy failed: " + ex.Message); }
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Find a GameObject by path, get its Renderer component.
        /// Uses GameObject.Find + recursive fallback (mirrors ShaderHotReplaceHandler).
        /// </summary>
        
    }
}
