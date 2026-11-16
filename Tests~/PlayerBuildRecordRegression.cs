// Run as an Editor REPL submission. Fixtures stay in the consuming project's Temp/.
var recorder = typeof(Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler).Assembly.GetType("Zh1Zh1.CSharpConsole.Editor.Compiler.PlayerBuildRecorder");
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var readDefines = recorder.GetMethod("ReadPlayerDefines", flags);
var parseLinker = recorder.GetMethod("ParseLinkerArguments", flags);
var findGraph = recorder.GetMethod("FindPlayerGraph", flags);
var readCommandLine = recorder.GetMethod("ReadLinkerCommandLine", flags);
System.Func<System.Reflection.MethodInfo, object[], string> refusal = (method, arguments) =>
{
    try
    {
        method.Invoke(null, arguments);
    }
    catch (System.Reflection.TargetInvocationException e) when (e.InnerException is System.InvalidOperationException)
    {
        return e.InnerException.Message;
    }

    throw new System.Exception(method.Name + " accepted what it should refuse");
};
var root = System.IO.Path.GetFullPath("Temp/CSharpConsole/AgentScratch/build-record-" + System.Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(root);

// Defines: the compiler arguments of this target, backend and configuration, however recent another's are.
var engineRoot = System.IO.Path.Combine(root, "Support", "Windows").Replace('\\', '/') + "/";
var engine = engineRoot + "UnityEngine.CoreModule.dll";
System.Action<string, string, string, string, int> addCompiler = (name, path, backend, extra, age) =>
{
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
if (!actual.Contains("EXPECTED_WINDOWS") || actual.Contains("WRONG"))
{
    throw new System.Exception("Wrong target/backend: " + actual);
}

var missing = (string)readDefines.Invoke(null, new object[] { root, true, "C:/Missing/UnityEngine.CoreModule.dll", "Il2Cpp" });
if (missing != "")
{
    throw new System.Exception("Missing target fell back to unrelated defines");
}

var release = (string)readDefines.Invoke(null, new object[] { root, false, engine, "Il2Cpp" });
if (release != "")
{
    throw new System.Exception("Release build accepted Development defines");
}

// Linker arguments: read from one command line, refused when it is another backend's or writes elsewhere.
var commandLine = "--out=\"Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped\" --allowed-assembly=\"" + engine
    + "\" --allowed-assembly=\"C:/Bcl/Il2Cpp/mscorlib.dll\" --dotnetruntime=Il2Cpp";
var linker = (System.ValueTuple<string, string, string>)parseLinker.Invoke(null, new object[] { commandLine, "Il2Cpp" });
if (linker.Item1 != "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped" || linker.Item2 != "C:/Bcl/Il2Cpp/mscorlib.dll" || linker.Item3 != engine)
{
    throw new System.Exception("Wrong linker arguments: " + linker);
}

refusal(parseLinker, new object[] { commandLine, "Mono" });
refusal(parseLinker, new object[] { commandLine.Replace("ManagedStripped", "Managed"), "Il2Cpp" });

// Player graphs: the one this build's configuration names, and none other.
var bee = System.IO.Path.Combine(root, "Library", "Bee");
System.IO.Directory.CreateDirectory(System.IO.Path.Combine(bee, "artifacts", "rsp"));
System.Action<string, string, string, bool, bool, bool, string> addGraph = (name, destination, package, il2cpp, development, install, action) =>
{
    // Sibling sections carry their own DestinationPath-like fields; only the player build configuration counts.
    System.IO.File.WriteAllText(System.IO.Path.Combine(bee, name + "-inputdata.json"),
        "{\"BeeBuildProgramCommon.Data.ConfigurationData\":{\"Development\":" + (!development).ToString().ToLowerInvariant() + "},"
        + "\"PlayerBuildProgramLibrary.Data.PlayerBuildConfig\":{\"DestinationPath\":\"" + destination + "\",\"StagingArea\":\"Temp/StagingArea\","
        + "\"PlayerPackage\":\"" + package + "\",\"UseIl2Cpp\":" + il2cpp.ToString().ToLowerInvariant()
        + ",\"Development\":" + development.ToString().ToLowerInvariant() + ",\"InstallIntoBuildsFolder\":" + install.ToString().ToLowerInvariant()
        + ",\"ManagedAssemblies\":[\"C:/Player/Windows/UnityEngine.CoreModule.dll\"]},"
        + "\"PlayerBuildProgramLibrary.Data.LinkerConfig\":{\"Runtime\":\"il2cpp\"}}");
    System.IO.File.WriteAllText(System.IO.Path.Combine(bee, name + ".dag.json"),
        "{\"Nodes\":[{\"Annotation\":\"CopyFiles Build/x\",\"DisplayName\":\"Copying\",\"Inputs\":[\"a\"],\"ToBuildDependencies\":[1]}"
        + (action == null ? "" : ",{\"Annotation\":\"UnityLinker Library/Bee/artifacts/unitylinker_x.traceevents\",\"Action\":"
            + "\"" + action.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"Inputs\":[\"b\"]}")
        + "]}");
};
var linkerExe = "\"/Tools @2/UnityLinker.exe\"";
System.IO.File.WriteAllText(System.IO.Path.Combine(bee, "artifacts", "rsp", "with space.rsp"), commandLine);
addGraph("PlayerWin", "Build/Win/Game.exe", "Support/Windows", true, true, false, linkerExe + " @\"Library/Bee/artifacts/rsp/with space.rsp\"");
addGraph("PlayerWinMono", "Build/Win/Game.exe", "Support/Windows", false, true, false, linkerExe + " --out=Library/Mono/ManagedStripped --dotnetruntime=Mono");
addGraph("PlayerWinRelease", "Build/Win/Game.exe", "Support/Windows", true, false, false, null);
addGraph("PlayerWinInstalled", "Build/Win/Game.exe", "Support/Windows", true, true, true, null);
addGraph("PlayerAndroid", "Build/Win/Game.exe", "Support/Android", true, true, false, null);
addGraph("PlayerOther", "Build/Other/Game.exe", "Support/Windows", true, true, false, null);
addGraph("PlayerMac", "Build/Mac/Game.app", "Support/Mac", true, true, false, "\"/Tools/UnityLinker\" " + commandLine);

var windows = System.IO.Path.DirectorySeparatorChar == '\\';
var absoluteDestination = System.IO.Path.Combine(root, windows ? "BUILD" : "Build", "Win", "Game.exe").Replace('/', '\\');
var graph = (string)findGraph.Invoke(null, new object[] { root, windows ? engineRoot.ToLowerInvariant() : engineRoot, "Il2Cpp", true, absoluteDestination, false, false, false });
if (System.IO.Path.GetFileName(graph) != "PlayerWin.dag.json")
{
    throw new System.Exception("Wrong player graph: " + graph);
}

var fromResponseFile = (System.ValueTuple<string, string, string>)parseLinker.Invoke(null, new object[] { (string)readCommandLine.Invoke(null, new object[] { root, graph }), "Il2Cpp" });
if (fromResponseFile.Item1 != linker.Item1 || fromResponseFile.Item2 != linker.Item2)
{
    throw new System.Exception("Response file not read in place of its reference: " + fromResponseFile);
}

var mono = (string)findGraph.Invoke(null, new object[] { root, engineRoot, "Mono", true, "Build/Win/Game.exe", false, false, false });
if (System.IO.Path.GetFileName(mono) != "PlayerWinMono.dag.json")
{
    throw new System.Exception("Wrong Mono player graph: " + mono);
}

// Unity 6 names the backend instead of UseIl2Cpp, and an Android graph builds the Gradle
// project in Unity's staging directory, so its destination is never the requested APK.
System.Action<string, string, string> addStagedGraph = (name, stage, backend) =>
{
    System.IO.File.WriteAllText(System.IO.Path.Combine(bee, name + "-inputdata.json"),
        "{\"PlayerBuildProgramLibrary.Data.PlayerBuildConfig\":{\"DestinationPath\":\"Library/Bee/Android/Prj/" + stage + "/Gradle\","
        + "\"PlayerPackage\":\"Support/Android6\",\"ScriptingBackend\":\"" + backend + "\",\"Development\":true,\"InstallIntoBuildsFolder\":false}}");
    System.IO.File.WriteAllText(System.IO.Path.Combine(bee, name + ".dag.json"), "{\"Nodes\":[]}");
};
addStagedGraph("PlayerAndroidMono", "Mono2x", "Mono");
addStagedGraph("PlayerAndroidIl2Cpp", "IL2CPP", "IL2CPP");
foreach (var (backend, expected) in new[] { ("Mono", "PlayerAndroidMono.dag.json"), ("Il2Cpp", "PlayerAndroidIl2Cpp.dag.json") })
{
    var staged = (string)findGraph.Invoke(null, new object[] { root, "Support/Android6", backend, true, "Build/Android/Game.apk", false, false, true });
    if (System.IO.Path.GetFileName(staged) != expected)
    {
        throw new System.Exception("Wrong staged " + backend + " player graph: " + staged);
    }
}

refusal(findGraph, new object[] { root, "Support/Android6", "Mono", true, "Build/Android/Game.apk", false, false, false });

// Some platforms (including iOS) put the arguments inline. On macOS, also test
// that an output path without its .app extension still selects the right graph.
var mac = (string)findGraph.Invoke(null, new object[] { root, "Support/Mac", "Il2Cpp", true, "Build/Mac/Game", false, true, false });
var inline = (System.ValueTuple<string, string, string>)parseLinker.Invoke(null, new object[] { (string)readCommandLine.Invoke(null, new object[] { root, mac }), "Il2Cpp" });
if (inline.Item1 != linker.Item1 || inline.Item3 != engine)
{
    throw new System.Exception("Inline linker arguments not read: " + inline);
}

refusal(findGraph, new object[] { root, "Support/Mac", "Il2Cpp", true, "Build/Mac/Game", false, false, false });
if (!refusal(findGraph, new object[] { root, engineRoot, "Il2Cpp", true, "Build/Missing/Game.exe", false, false, false }).StartsWith("No player graph"))
{
    throw new System.Exception("Missing graph not reported as missing");
}

refusal(readCommandLine, new object[] { root, System.IO.Path.Combine(bee, "PlayerWinRelease.dag.json") });
addGraph("PlayerWinCopy", "Build/Win/Game.exe", "Support/Windows", true, true, false, null);
if (!refusal(findGraph, new object[] { root, engineRoot, "Il2Cpp", true, "Build/Win/Game.exe", false, false, false }).StartsWith("Several player graphs"))
{
    throw new System.Exception("Ambiguous graphs not refused");
}

System.IO.File.WriteAllText(System.IO.Path.Combine(bee, "PlayerOther.dag.json"),
    "{\"Nodes\":[{\"Annotation\":\"UnityLinker a\",\"Action\":\"x\"},{\"Annotation\":\"UnityLinker b\",\"Action\":\"y\"}]}");
refusal(readCommandLine, new object[] { root, System.IO.Path.Combine(bee, "PlayerOther.dag.json") });

addGraph("PlayerMissingRsp", "Build/MissingRsp/Game.exe", "Support/Windows", true, true, false, linkerExe + " @missing.rsp");
if (!refusal(readCommandLine, new object[] { root, System.IO.Path.Combine(bee, "PlayerMissingRsp.dag.json") }).Contains("missing.rsp"))
{
    throw new System.Exception("Missing response file was not identified");
}

"PASS: defines selection, linker arguments, player graph matching, response-file and inline arguments, and refusals"
