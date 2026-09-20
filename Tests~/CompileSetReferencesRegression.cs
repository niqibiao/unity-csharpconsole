// Run as an Editor REPL submission after a successful Player build exports its set.
var report = UnityEditor.Build.Reporting.BuildReport.GetLatestReport();
var zip = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(report.summary.outputPath), "CSharpConsoleCompileSet.zip");
var root = System.IO.Path.GetFullPath("Temp/CSharpConsole/AgentScratch/references-" + System.Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(root);
var aligned = System.IO.Path.Combine(root, "aligned");
System.IO.Directory.CreateDirectory(aligned);
var extract = typeof(Zh1Zh1.CSharpConsole.Service.CompileSetStore).GetMethod("ExtractCompileSet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
var extracted = (string)extract.Invoke(null, new object[] { System.IO.File.ReadAllBytes(zip) });
foreach (var file in System.IO.Directory.GetFiles(extracted))
    System.IO.File.Copy(file, System.IO.Path.Combine(aligned, System.IO.Path.GetFileName(file)));
var compiler = new Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler(aligned);
var declaration = compiler.Compile("string referenceProbe = \"hello\";");
if (declaration.assemblyBytes == null) throw new System.Exception(declaration.errorMsg);
var completions = compiler.GetCompletions("referenceProbe.", "referenceProbe.".Length, "", "");
if (!System.Linq.Enumerable.Any(completions, c => c.Label == "ToUpper")) throw new System.Exception("Lost submission completion");
var editorApi = compiler.Compile("UnityEditor.EditorApplication.isPlaying");
if (editorApi.assemblyBytes != null || string.IsNullOrEmpty(editorApi.errorMsg)) throw new System.Exception("Editor API compiled against a player set");
var editorCompletions = compiler.GetCompletions("UnityEditor.", "UnityEditor.".Length, "", "");
if (System.Linq.Enumerable.Any(editorCompletions, c => c.Label == "EditorApplication")) throw new System.Exception("Editor API leaked into completion");
var next = compiler.Compile("referenceProbe.ToUpper()");
if (next.assemblyBytes == null) throw new System.Exception("Compile failure lost previous state: " + next.errorMsg);
var skipped = new Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler("").Compile("UnityEditor.EditorApplication.isPlaying");
if (skipped.assemblyBytes == null) throw new System.Exception("Skip reference policy changed: " + skipped.errorMsg);
var plain = System.IO.Path.Combine(root, "plain");
System.IO.Directory.CreateDirectory(plain);
var overrideResult = new Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler(plain).Compile("UnityEditor.EditorApplication.isPlaying");
if (overrideResult.assemblyBytes == null) throw new System.Exception("Plain override policy changed: " + overrideResult.errorMsg);
System.Action<string, string> expectFailure = (directory, text) => {
    var failed = false;
    try {
        var strict = new Zh1Zh1.CSharpConsole.Editor.Compiler.BaseREPLCompiler("StrictProbe_", "", true, directory, useOnlyRuntimeReferences: true);
        var result = strict.Compile("1+2");
        failed = result.assemblyBytes == null && (result.errorMsg ?? "").Contains(text);
    } catch (System.Exception e) {
        failed = e.ToString().Contains(text);
    }
    if (!failed) throw new System.Exception("Expected actionable failure for: " + directory);
};
expectFailure(System.IO.Path.Combine(root, "missing"), "Compile-set directory does not exist");
expectFailure(plain, "Compile set contains no assemblies");
var corrupt = System.IO.Path.Combine(root, "corrupt");
System.IO.Directory.CreateDirectory(corrupt);
System.IO.File.WriteAllText(System.IO.Path.Combine(corrupt, "Broken.dll"), "not an assembly");
expectFailure(corrupt, "Broken.dll");
"PASS: strict compile/completion, submission recovery, skip/plain compatibility, missing/empty/corrupt sets"
