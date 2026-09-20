// Run as an Editor REPL submission. Fixtures stay in the consuming project's Temp/.
var recorder = typeof(Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler).Assembly.GetType("Zh1Zh1.CSharpConsole.Editor.Compiler.PlayerBuildRecorder");
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var readDefines = recorder.GetMethod("ReadPlayerDefines", flags);
var readLinker = recorder.GetMethod("ReadLinkerArguments", flags);
var root = System.IO.Path.GetFullPath("Temp/CSharpConsole/AgentScratch/build-record-" + System.Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(root);
var engineRoot = "C:/Player/Windows/";
var engine = engineRoot + "UnityEngine.CoreModule.dll";
System.Action<string, string, string, string, int> addCompiler = (name, path, backend, extra, age) => {
    var directory = System.IO.Path.Combine(root, name + ".dag");
    System.IO.Directory.CreateDirectory(directory);
    var rsp = System.IO.Path.Combine(directory, "Assembly-CSharp.rsp");
    System.IO.File.WriteAllText(rsp, "-define:DEVELOPMENT_BUILD\n-define:" + backend + "\n-define:" + extra + "\n-r:\"" + path + "\"\n");
    System.IO.File.SetLastWriteTimeUtc(rsp, System.DateTime.UtcNow.AddMinutes(age));
};
addCompiler("old-windows", engine, "ENABLE_IL2CPP", "EXPECTED_WINDOWS", -10);
addCompiler("new-android", "C:/Player/Android/UnityEngine.CoreModule.dll", "ENABLE_IL2CPP", "WRONG_ANDROID", -1);
addCompiler("new-mono", engine, "ENABLE_MONO", "WRONG_MONO", 0);
var actual = (string)readDefines.Invoke(null, new object[] { root, true, engine, "Il2Cpp" });
if (!actual.Contains("EXPECTED_WINDOWS") || actual.Contains("WRONG")) throw new System.Exception("Wrong target/backend: " + actual);
var missing = (string)readDefines.Invoke(null, new object[] { root, true, "C:/Missing/UnityEngine.CoreModule.dll", "Il2Cpp" });
if (missing != "") throw new System.Exception("Missing target fell back to unrelated defines");
var release = (string)readDefines.Invoke(null, new object[] { root, false, engine, "Il2Cpp" });
if (release != "") throw new System.Exception("Release build accepted Development defines");
var rspDirectory = System.IO.Path.Combine(root, "rsp");
System.IO.Directory.CreateDirectory(rspDirectory);
System.Action<string, string, string, int> addLinker = (name, reference, backend, age) => {
    var rsp = System.IO.Path.Combine(rspDirectory, name + ".rsp");
    System.IO.File.WriteAllText(rsp, "--out=\"" + name + "/ManagedStripped\" --allowed-assembly=\"" + reference + "\" --allowed-assembly=\"C:/Bcl/" + backend + "/mscorlib.dll\" --dotnetruntime=" + backend);
    System.IO.File.SetLastWriteTimeUtc(rsp, System.DateTime.UtcNow.AddMinutes(age));
};
addLinker("old-windows", engine, "Il2Cpp", -10);
addLinker("new-android", "C:/Player/Android/UnityEngine.CoreModule.dll", "Il2Cpp", -1);
addLinker("new-mono", engine, "Mono", 0);
var linker = (System.ValueTuple<string, string, string>)readLinker.Invoke(null, new object[] { root, root, engineRoot, "Il2Cpp" });
if (linker.Item1 != System.IO.Path.Combine(root, "old-windows", "ManagedStripped") || linker.Item2 != "C:/Bcl/Il2Cpp/mscorlib.dll" || linker.Item3 != engine)
    throw new System.Exception("Wrong linker arguments: " + linker);
"PASS: cached target/backend, Development filtering, missing match, and same-target backend linker selection"
