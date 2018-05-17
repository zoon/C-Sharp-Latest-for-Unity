using System;
using UnityEditor;
using UnityEngine;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Text;
using UnityEngine.Assertions;
using JObject = System.Collections.Generic.Dictionary<string, object>;
using JArray = System.Collections.Generic.List<object>;

#if true
[assembly: AssemblyVersion("0.1.0.17")]
[assembly: AssemblyTitle("C# Latest for Unity")]
[assembly: AssemblyDescription("https://github.com/zoon/C-Sharp-Latest-for-Unity")]
#endif

namespace CSharpLatest
{
    public static class Bootstrap
    {
        [InitializeOnLoadMethod]
        static void Main()
        {
            if (s_unityCurrent < s_unityMinimal)
            {
                Debug.LogError(
                    $"Unsupported Unity version: {s_unityCurrent.Major}.{s_unityCurrent.Minor} ({s_unityMinimal}" +
                    "needed).");
                return;
            }
            Assembly[] loadedAssemblies = (Assembly[])s_loadedAssembliesGetter?.Invoke(null, s_zero);
            if (loadedAssemblies == null)
            {
                Debug.LogError("Error getting 'UnityEditor.EditorAssemblies.loadedAssemblies' by reflection");
                return;
            }
            if (loadedAssemblies.ShiftToLast(a => Equals(a, typeof(CsProjectPostprocessor).Assembly)))
            {
                CsProjectPostprocessor.OnGeneratedCSProjectFiles();
            }

            if (s_unityCurrent >= s_unityModern &&
                EditorApplication.scriptingRuntimeVersion > ScriptingRuntimeVersion.Legacy)
            {
                UPMManifestProcessor.AddIncrementalCompilerPackage();
            }
        }

        public static bool ShiftToLast<T>(this T[] list, Predicate<T> predicate)
        {
            int lastIdx = list.Length - 1;
            int idx = Array.FindIndex(list, predicate);
            if (lastIdx < 0 || idx < 0 || idx == lastIdx) return false;
            T temp = list[idx];
            Array.Copy(list, idx + 1, list, idx, lastIdx - idx);
            list[lastIdx] = temp;
            return true;
        }

        internal static readonly Version  s_unityCurrent = UnityEditorInternal.InternalEditorUtility.GetUnityVersion();
        internal static readonly Version  s_unityMinimal = new Version(2017, 1);
        internal static readonly Version  s_unityModern  = new Version(2018, 1);
        private static readonly  object[] s_zero         = new object[0];
        private static readonly MethodInfo s_loadedAssembliesGetter = typeof(EditorWindow)
           .Assembly.GetType("UnityEditor.EditorAssemblies")
          ?.GetProperty("loadedAssemblies", BindingFlags.Static | BindingFlags.NonPublic)
          ?.GetGetMethod(true);
    }

    internal class CsProjectPostprocessor : AssetPostprocessor
    {
        public static void OnGeneratedCSProjectFiles()
        {
            if (Bootstrap.s_unityCurrent < Bootstrap.s_unityMinimal) return;

            // NOTE: Rider 2018.1 thinks that IF .Net 2.0 THEN "latest" == 4
            // NOTE: 'old' mcs supports only C# 6 (and not 'latest'), IDEs don't support mcs's 'experimental'.
            var csver = "6";
            if (Bootstrap.s_unityCurrent >= Bootstrap.s_unityModern)
            {
                csver = EditorApplication.scriptingRuntimeVersion == ScriptingRuntimeVersion.Legacy
                    ? "7.2"
                    : "latest";
            }
            UpdateRsp(csver);
            foreach (string csproj in Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj"))
            {
                UpdateProjectFile(csproj, csver);
            }
        }
        private static void UpdateRsp(string csver)
        {
            try
            {
                const string LANGVERSION = "-langversion:";
                string rsp = Path.Combine("Assets", "mcs.rsp");
                string temp = Path.ChangeExtension(Path.GetTempFileName(), ".rsp");

                if (File.Exists(rsp))
                {
                    string[] lines = File.ReadAllLines(rsp);
                    int i = 0, idx = -1;
                    for (; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        idx = line.IndexOf(LANGVERSION, StringComparison.Ordinal);
                        if (idx >= 0) break;
                    }
                    if (idx >= 0)
                    {
                        string ver = lines[i].Substring(idx + LANGVERSION.Length);
                        if (ver == csver) return;
                        lines[i] = LANGVERSION + csver;
                        File.WriteAllLines(temp, lines);
                    }
                    else
                    {
                        File.WriteAllLines(temp, new[] {LANGVERSION + csver});
                        File.WriteAllLines(temp, lines);
                    }
                    File.Copy(temp, rsp, true);
                }
                else
                {
                    File.WriteAllLines(rsp, new[] {LANGVERSION + csver});
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private static void UpdateProjectFile(string csproj, string csver)
        {
            try
            {
                XDocument xdoc = XDocument.Load(csproj);
                if (ChangeOrSetProperty(xdoc.Root, xdoc.Root?.Name.NamespaceName, "LangVersion", csver))
                {
                    xdoc.Save(csproj);
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private static bool ChangeOrSetProperty(XContainer root, XNamespace ns, string name, string val)
        {
            XElement node = root.Elements(ns + "PropertyGroup").Elements(ns + name).FirstOrDefault()
                         ?? new XElement(ns + name, "?");
            if (node.Value == val) return false;
            node.Value = val;
            if (node.Parent == null)
            {
                var propertyGroup = new XElement(ns + "PropertyGroup");
                root.AddFirst(propertyGroup);
                propertyGroup.Add(node);
            }
            return true;
        }
    }

    public static class UPMManifestProcessor
    {
        public const string DEPENDENCIES        = "dependencies";
        public const string REGISTRY            = "registry";
        public const string TESTABLES           = "testables";
        public const string INCREMENTALCOMPILER = "com.unity.incrementalcompiler";

        private static MethodInfo s_JsonDeserializeInfo = typeof(EditorWindow)
           .Assembly
           .GetType("UnityEditor.Json")
          ?.GetMethod("Deserialize");
        public static Func<string, object> JsonDeserialize = s_JsonDeserializeInfo != null
            ? (Func<string, object>)Delegate.CreateDelegate(typeof(Func<string, object>), s_JsonDeserializeInfo)
            : _ => throw new MissingMethodException("UnityEditor.Json", "Deserialize");

        public static void SerializeUPMManifest(JObject manifest, ref StringBuilder sb)
        {
            const char DQ = '"';
            const char LF = '\n';
            const string INDENT = "  ";

            sb.Append("{");
            // dependencies
            sb.Append(LF).Append(INDENT).Append(DQ).Append(DEPENDENCIES).Append(DQ).Append(": {");
            manifest.TryGetValue(DEPENDENCIES, out object deps);
            if (deps is JObject dependencies && dependencies.Count > 0)
            {
                foreach (var d in dependencies)
                {
                    sb.Append(LF).Append(INDENT).Append(INDENT).Append(DQ).Append(d.Key).Append(DQ).Append(": ");
                    sb.Append(DQ).Append(d.Value).Append(DQ).Append(',');
                }
                sb.Length -= 1; // rm trailing comma
                sb.Append(LF).Append(INDENT);
            }
            sb.Append("}");
            // registry
            manifest.TryGetValue(REGISTRY, out object reg);
            if (reg is string registry && !string.IsNullOrEmpty(registry))
            {
                sb.Append(',');
                sb.Append(LF).Append(INDENT).Append(DQ).Append(REGISTRY).Append(DQ).Append(": ");
                sb.Append(DQ).Append(registry).Append(DQ);
            }
            // testables
            manifest.TryGetValue(TESTABLES, out object tests);
            if (tests is JArray testables && testables.Count > 0)
            {
                sb.Append(',');
                sb.Append(LF).Append(INDENT).Append(DQ).Append(TESTABLES).Append(DQ).Append(": [");
                foreach (string testable in testables)
                {
                    sb.Append(LF).Append(INDENT).Append(INDENT).Append(DQ).Append(testable).Append(DQ).Append(',');
                }
                sb.Length -= 1; // rm trailing comma
                sb.Append(LF).Append(INDENT).Append(']');
            }
            sb.Append(LF).Append('}');
        }

        internal static void AddIncrementalCompilerPackage()
        {
            try
            {
                string manPath = Path.Combine("Packages", "manifest.json");
                string manifestJson = File.ReadAllText(manPath);
                JObject manifest = JsonDeserialize(manifestJson) as JObject;
                Assert.IsNotNull(manifest);
                manifest.TryGetValue(DEPENDENCIES, out object obj);
                JObject dependencies = obj as JObject ?? new JObject();
                if (dependencies.ContainsKey(INCREMENTALCOMPILER))
                {
                    return;
                }
                dependencies[INCREMENTALCOMPILER] = "0.0.41";
                manifest[DEPENDENCIES]            = dependencies;
                manifest[REGISTRY]                = "https://staging-packages.unity.com";
                var sb = new StringBuilder(1024);
                SerializeUPMManifest(manifest, ref sb);
                File.WriteAllText(manPath, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError($"AddIncrementalCompilerPackage: {e}");
            }
        }
    }
}
