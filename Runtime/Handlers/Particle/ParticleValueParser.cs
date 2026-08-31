using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers.Particle
{
    // Particle 值转换器（partial）：JSON 原始值 → Unity typed 值（ParticleHandler 拆分 Step 1）
    public static partial class ParticleValueParser
    {
public static float ToFloat(object raw)
        {
            if (raw is float f) return f;
            if (raw is int i) return i;
            if (raw is double d) return (float)d;
            return float.Parse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static int ToInt(object raw)
        {
            if (raw is int i) return i;
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        public static bool ToBool(object raw)
        {
            if (raw is bool b) return b;
            return bool.Parse(raw.ToString());
        }

        public static T ToEnum<T>(object raw) where T : struct
        {
            var s = raw.ToString();
            if (Enum.TryParse<T>(s, true, out var result)) return result;
            throw new ArgumentException($"Invalid enum value '{s}' for {typeof(T).Name}");
        }

        public static Vector3 ToVector3(object raw)
        {
            var arr = ToFloatArray(raw);
            if (arr == null || arr.Length < 3)
                throw new ArgumentException("Vector3 value must be [x,y,z]");
            return new Vector3(arr[0], arr[1], arr[2]);
        }

        public static Vector2 ToVector2(object raw)
        {
            var arr = ToFloatArray(raw);
            if (arr == null || arr.Length < 2)
                throw new ArgumentException("Vector2 value must be [x,y]");
            return new Vector2(arr[0], arr[1]);
        }

        public static Color ToColor(object raw)
        {
            if (raw is float[] arr && arr.Length >= 3)
                return arr.Length >= 4
                    ? new Color(arr[0], arr[1], arr[2], arr[3])
                    : new Color(arr[0], arr[1], arr[2], 1f);
            var s = raw.ToString();
            if (ColorUtility.TryParseHtmlString(s, out var c))
                return c;
            throw new ArgumentException($"Invalid color '{s}'. Use [r,g,b,a], #RRGGBB, or a color name");
        }

        /// <summary>数字 → 常量 MinMaxCurve；[min,max] → 双常量随机；[[t,v],...] → 动画曲线。</summary>
        public static ParticleSystem.MinMaxCurve ToCurve(object raw)
        {
            var list = NormalizeArray(raw);
            if (list != null && list.Count > 0 && IsNestedEntry(list[0]))
            {
                var curve = new AnimationCurve();
                foreach (var entry in list)
                {
                    var normalized = NormalizeArray(entry);
                    var kv = ToFloatArray(normalized ?? (object)entry);
                    if (kv == null || kv.Length < 2)
                        throw new ArgumentException("Curve key must be [time, value]");
                    curve.AddKey(kv[0], kv[1]);
                }
                return new ParticleSystem.MinMaxCurve(1f, curve);
            }
            var arr = ToFloatArray(raw);
            if (arr != null && arr.Length >= 2)
                return new ParticleSystem.MinMaxCurve(arr[0], arr[1]);
            return new ParticleSystem.MinMaxCurve(ToFloat(raw));
        }

        /// <summary>
        /// 颜色 → 单色 MinMaxGradient；[c1,c2] → 双色渐变（colorOverLifetime 下为时间渐变，
        /// startColor 下为随机）；[[t,c],...] → 完整时间渐变 Gradient。
        /// </summary>
        public static ParticleSystem.MinMaxGradient ToGradient(object raw)
        {
            var list = NormalizeArray(raw);
            if (list != null && list.Count > 0 && IsNestedEntry(list[0]))
            {
                // [[t,c],...] 完整渐变
                var colorKeys = new GradientColorKey[list.Count];
                var alphaKeys = new GradientAlphaKey[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    var entry = NormalizeArray(list[i]);
                    if (entry == null || entry.Count < 2)
                        throw new ArgumentException("Gradient key must be [time, color]");
                    var t = Convert.ToSingle(entry[0], CultureInfo.InvariantCulture);
                    var c = ToColor(entry[1]);
                    colorKeys[i] = new GradientColorKey(c, t);
                    alphaKeys[i] = new GradientAlphaKey(c.a, t);
                }
                var g = new Gradient();
                g.SetKeys(colorKeys, alphaKeys);
                return new ParticleSystem.MinMaxGradient(g);
            }
            if (list != null && list.Count == 2 && !IsNumeric(list[0]))
            {
                // [c1, c2] → TwoColors（colorOverLifetime = 时间渐变；startColor = 随机）
                return new ParticleSystem.MinMaxGradient(ToColor(list[0]), ToColor(list[1]));
            }
            return new ParticleSystem.MinMaxGradient(ToColor(raw));
        }

        /// <summary>
        /// 将原始值统一为 List&lt;object&gt;：JSON 数组字符串（ParseJsonValue 对含引号/嵌套数组
        /// 返回原始字符串）按顶层元素拆分，IList 直接转 List，其他返回 null。
        /// </summary>
        public static List<object> NormalizeArray(object raw)
        {
            if (raw is string s && s.TrimStart().StartsWith("["))
            {
                var result = new List<object>();
                // Strip exactly ONE outer bracket pair (Trim('[',']') would corrupt inner brackets)
                var inner = s.Trim();
                if (inner.Length >= 2 && inner[0] == '[' && inner[inner.Length - 1] == ']')
                    inner = inner.Substring(1, inner.Length - 2);
                if (string.IsNullOrWhiteSpace(inner)) return result;
                foreach (var part in HandlerUtils.SplitJsonTopLevel(inner))
                    result.Add(HandlerUtils.ParseJsonValue(part));
                return result;
            }
            if (raw is System.Collections.IList list)
            {
                var result = new List<object>(list.Count);
                foreach (var item in list) result.Add(item);
                return result;
            }
            return null;
        }

        public static bool IsNestedEntry(object v)
        {
            return v is System.Collections.IList || (v is string str && str.TrimStart().StartsWith("["));
        }

        public static bool IsNumeric(object v)
        {
            return v is float || v is int || v is double || v is long;
        }

        // ── 输出辅助 ──

        
    }
}
