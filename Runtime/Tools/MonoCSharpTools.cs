#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Singleton: provides in-process C# code evaluation via Mono.CSharp (reflection-loaded).
    /// One evaluator per thread — lazy-initialized on first access.
    /// All reflections happen once; subsequent calls use cached delegates (no MethodInfo.Invoke).
    /// </summary>
    public static class MonoCSharpTools
    {
        // ── Delegate type definitions ──
        // Mono.CSharp API on Unity 2022.3 (verified by runtime reflection):
        //   instance bool    Run(string code)
        //   instance string  Evaluate(string input, out object result, out bool resultSet)
        //
        // Closed delegates are used (bound to the evaluator instance after creation)
        // because open-instance delegates require exact declaring-type match, which
        // isn't possible when the type is loaded from a reflected assembly.
        private delegate string Eval3Del(string code, out object result, out bool resultSet);

        // ── Thread-static evaluator singleton + delegates ──
        [ThreadStatic]
        private static object _evaluator;
        [ThreadStatic]
        private static Func<string, bool> _run;       // closed: bool Run(string)
        [ThreadStatic]
        private static Eval3Del _eval3;               // closed: string Evaluate(string, out object, out bool)

        /// <summary>
        /// Gets the Mono.CSharp Evaluator for the current thread.
        /// Created once per thread, cached forever.
        /// </summary>
        public static object Evaluator
        {
            get
            {
                if (_evaluator == null)
                    _evaluator = CreateEvaluator();
                return _evaluator;
            }
        }

        // ── Config (shared across threads, read-only) ──

        private static readonly string MonoCSharpPath =
            Path.Combine(
                EditorApplication.applicationContentsPath,
                "MonoBleedingEdge", "lib", "mono", "4.5", "Mono.CSharp.dll"
            );

        // Type references (needed for Activator.CreateInstance, shared across threads)
        private static Type _tEvaluator, _tCompilerSettings, _tCompilerContext, _tStreamReportPrinter;

        // ── Singleton initializer ──

        private static object CreateEvaluator()
        {
            if (!File.Exists(MonoCSharpPath))
                throw new FileNotFoundException(
                    $"Mono.CSharp.dll not found at expected path:\n{MonoCSharpPath}\n" +
                    "editor.eval requires a Unity Editor installation with Mono.");

            var monoAssembly = Assembly.LoadFrom(MonoCSharpPath);

            // --- Resolve types ---
            _tEvaluator = monoAssembly.GetType("Mono.CSharp.Evaluator")
                ?? throw new Exception("Type 'Mono.CSharp.Evaluator' not found.");

            _tCompilerSettings = monoAssembly.GetType("Mono.CSharp.CompilerSettings")
                ?? throw new Exception("Type 'Mono.CSharp.CompilerSettings' not found.");

            _tCompilerContext = monoAssembly.GetType("Mono.CSharp.CompilerContext")
                ?? throw new Exception("Type 'Mono.CSharp.CompilerContext' not found.");

            _tStreamReportPrinter = monoAssembly.GetType("Mono.CSharp.StreamReportPrinter")
                ?? throw new Exception("Type 'Mono.CSharp.StreamReportPrinter' not found.");

            // --- Build CompilerSettings ---
            var settings = Activator.CreateInstance(_tCompilerSettings);
            var refContainer = ResolveAssemblyReferencesList(settings);
            refContainer.Add(typeof(UnityEngine.Object).Assembly.Location);
            refContainer.Add(typeof(UnityEditor.Editor).Assembly.Location);

            // Disable debug info / set warning level / load default refs
            _tCompilerSettings.GetProperty("GenerateDebugInfo")?.SetValue(settings, false);
            _tCompilerSettings.GetProperty("WarningLevel")?.SetValue(settings, 0);
            _tCompilerSettings.GetProperty("LoadDefaultReferences")?.SetValue(settings, true);

            // --- Create context + evaluator ---
            var writer = new StringWriter();
            var printer = Activator.CreateInstance(_tStreamReportPrinter, writer);
            var context = Activator.CreateInstance(_tCompilerContext, settings, printer);
            var eval = Activator.CreateInstance(_tEvaluator, context);

            // --- Create closed delegates (bound to this instance) ---
            var miRun = _tEvaluator.GetMethod("Run", new[] { typeof(string) });
            if (miRun != null)
            {
                try { _run = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), eval, miRun, throwOnBindFailure: true); }
                catch (Exception ex) { UnityEngine.Debug.Log($"[MonoCSharpTools] Run delegate failed ({ex.Message})"); }
            }
            if (_run == null)
                _run = code => (bool)miRun.Invoke(eval, new object[] { code });

            var miEval = _tEvaluator.GetMethod("Evaluate",
                new[] { typeof(string), typeof(object).MakeByRefType(), typeof(bool).MakeByRefType() });
            if (miEval != null)
            {
                try { _eval3 = (Eval3Del)Delegate.CreateDelegate(typeof(Eval3Del), eval, miEval, throwOnBindFailure: true); }
                catch (Exception ex) { UnityEngine.Debug.Log($"[MonoCSharpTools] Evaluate delegate failed ({ex.Message})"); }
            }
            if (_eval3 == null)
            {
                _eval3 = (string code, out object r, out bool rs) => {
                    var p = new object[] { code, null, null };
                    miEval.Invoke(eval, p);
                    r = p[1];
                    rs = p[2] != null && (bool)p[2];
                    return "";
                };
            }

            // --- Pre-import common namespaces ---
            foreach (var ns in new[] {
                "System", "System.Collections.Generic", "System.Linq",
                "UnityEngine", "UnityEditor",
                "UnityEngine.UI", "UnityEngine.EventSystems",
            })
                _run($"using {ns};");

            // Alias Object → UnityObject to avoid conflict
            _run("using UnityObject = UnityEngine.Object;");

            // Result holder for expression evaluation via Run
            _run("object __MCP_Result__ = null;");

            return eval;
        }

        /// <summary>
        /// Resolves the assembly-references list from CompilerSettings
        /// (property name varies by Mono.CSharp version; also supports fields).
        /// </summary>
        private static System.Collections.IList ResolveAssemblyReferencesList(object settings)
        {
            var refProp = _tCompilerSettings.GetProperty("AssemblyReferences")
                ?? _tCompilerSettings.GetProperty("References")
                ?? _tCompilerSettings.GetProperty("AdditionalReferences");
            object container = null;
            if (refProp != null)
                container = refProp.GetValue(settings);
            else
            {
                var refField = _tCompilerSettings.GetField("AssemblyReferences")
                    ?? _tCompilerSettings.GetField("References")
                    ?? _tCompilerSettings.GetField("AdditionalReferences");
                if (refField != null)
                    container = refField.GetValue(settings);
            }
            return container as System.Collections.IList
                ?? throw new Exception("No IList member for assembly references found on CompilerSettings.");
        }

        // ── Public eval API ──

        /// <summary>
        /// Evaluate a C# expression or execute a statement.
        /// Returns the result object (null for statements, null for void expressions).
        /// Strategy:
        ///   1. Try Mono.CSharp Evaluate() first — works for pure expressions,
        ///      does NOT corrupt evaluator state on failure.
        ///   2. If Evaluate() doesn't produce a result, use Run() to execute as statement.
        /// </summary>
        public static object Evaluate(string code)
        {
            var _ = Evaluator; // triggers lazy init (populates thread-static delegates)
            var cleanCode = code.TrimEnd().TrimEnd(';');

            // Step 1: try Evaluate as expression (non-destructive — doesn't corrupt state)
            try
            {
                _eval3(cleanCode, out var result, out var resultSet);
                if (resultSet)
                    return result;
            }
            catch
            {
                // Evaluate() threw — not a valid expression either
            }

            // Step 2: not an expression (or Evaluate unavailable) — run as statement
            _run(code);
            return null;
        }

        /// <summary>
        /// Serializes an arbitrary value to a compact JSON-friendly string.
        /// Handles Unity types (Vector3, Quaternion, Color, Transform, GameObject, Component).
        /// </summary>
        public static string SerializeEvalResult(object val)
        {
            if (val == null) return "null";
            if (val is string s) return s;
            if (val is Vector3 v3) return $"[{v3.x:0.###},{v3.y:0.###},{v3.z:0.###}]";
            if (val is Vector2 v2) return $"[{v2.x:0.###},{v2.y:0.###}]";
            if (val is Quaternion q) return $"[{q.x:0.###},{q.y:0.###},{q.z:0.###},{q.w:0.###}]";
            if (val is Color c) return $"[{c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###}]";
            if (val is Transform t) return t.name;
            if (val is GameObject g) return g.name;
            if (val is Component comp) return comp.gameObject.name + "/" + comp.GetType().Name;
            if (val is Array arr)
                return "[" + string.Join(",", arr.Cast<object>().Select(SerializeEvalResult)) + "]";
            return val.ToString();
        }
    }
}
#endif
