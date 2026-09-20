// Run as an Editor REPL submission. Uses a separate registry and unique store entries.
var storeType = typeof(Zh1Zh1.CSharpConsole.Service.CompileSetStore);
var nonPublicStatic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var registryType = storeType.Assembly.GetType("Zh1Zh1.CSharpConsole.Service.Internal.ReplServiceRegistry");
var registry = System.Activator.CreateInstance(registryType, true);
var root = (string)storeType.GetField("s_Root", nonPublicStatic).GetValue(null);
if (string.IsNullOrEmpty(root)) throw new System.Exception("Compile-set store is not initialized");
var build = System.Guid.NewGuid().ToString("N");
var nextBuild = System.Guid.NewGuid().ToString("N");
var first = System.IO.Path.Combine(root, "sets", "session-regression-a-" + build);
var second = System.IO.Path.Combine(root, "sets", "session-regression-b-" + build);
var decision = System.IO.Path.Combine(root, "builds", build);
System.IO.Directory.CreateDirectory(first);
System.IO.Directory.CreateDirectory(second);
System.Action<string, string, string> use = (id, path, guid) => registryType.GetMethod("UseRuntimeCompileSet").Invoke(registry, new object[] { id, path, guid });
System.Func<string, string> find = id => (string)registryType.GetMethod("FindRuntimeCompileSet").Invoke(registry, new object[] { id });
System.Action<string> register = path => storeType.GetMethod("Register", nonPublicStatic).Invoke(null, new object[] { build, path });
System.Action skip = () => storeType.GetMethod("Skip", nonPublicStatic).Invoke(null, new object[] { build });
System.Func<string, Zh1Zh1.CSharpConsole.Interface.IREPLCompiler> factory = path => new Zh1Zh1.CSharpConsole.Editor.Compiler.RuntimeREPLCompiler(path);
System.Func<string, string, object> compiler = (id, path) => registryType.GetMethod("FetchRuntimeREPLCompiler").Invoke(registry, new object[] { id, path, factory });
System.Action<string> requireDecision = id => {
    var refused = false;
    try { find(id); }
    catch (System.Reflection.TargetInvocationException e) {
        refused = e.InnerException is System.InvalidOperationException && e.InnerException.Message.Contains("[REPL ALIGNMENT REQUIRED]");
    }
    if (!refused) throw new System.Exception("Missing decision fell back to a compiler");
};
try {
    if (find("fresh") != null) throw new System.Exception("Fresh Editor session acquired a runtime set");
    use("first", null, build);
    requireDecision("first");
    register(first);
    if (find("first") != first) throw new System.Exception("First registration was not visible before resubmission");
    use("other", first, build);
    var originalCompiler = compiler("first", first);
    var otherCompiler = compiler("other", first);
    register(second);
    if (find("first") != second || find("other") != second) throw new System.Exception("Replacement did not reach every session");
    if (System.Object.ReferenceEquals(originalCompiler, compiler("first", first))
        || System.Object.ReferenceEquals(otherCompiler, compiler("other", first))) throw new System.Exception("Replacement retained old compilers");
    var replacementCompiler = compiler("first", second);
    if (find("first") != second || !System.Object.ReferenceEquals(replacementCompiler, compiler("first", second))) throw new System.Exception("Unchanged decision reset submission state");
    skip();
    if (find("first") != "") throw new System.Exception("Skip was not visible to completion");
    var skippedCompiler = compiler("first", "");
    storeType.GetMethod("Skip", nonPublicStatic).Invoke(null, new object[] { nextBuild });
    use("first", "", nextBuild);
    if (find("first") != "" || System.Object.ReferenceEquals(skippedCompiler, compiler("first", "")))
        throw new System.Exception("Changing skipped builds retained the old submission chain");
    use("first", "", build);
    register(first);
    if (find("first") != first) throw new System.Exception("Skipped session did not pick up registration");
    use("first", second, null);
    register(first);
    if (find("first") != second) throw new System.Exception("Explicit path was overwritten by a registered decision");
    System.IO.File.Delete(decision);
    System.IO.File.Delete(System.IO.Path.Combine(root, "builds", nextBuild));
    requireDecision("other");
    if (find("first") != second) throw new System.Exception("Explicit path depended on a store decision");
    register(first);
    System.IO.Directory.Delete(first);
    requireDecision("other");
    System.IO.Directory.CreateDirectory(first);
    registryType.GetMethod("RemoveCompilersForSession").Invoke(registry, new object[] { "other" });
    if (find("other") != null) throw new System.Exception("Runtime reset retained build binding");
    use("reset", first, build);
    registryType.GetMethod("ResetSessionState").Invoke(registry, new object[] { "reset" });
    if (find("reset") != null) throw new System.Exception("Session reset retained build binding");
    use("idle", first, build);
    compiler("idle", first);
    registryType.GetMethod("EvictIdleSessions").Invoke(registry, new object[] { -1.0 });
    if (find("idle") != null) throw new System.Exception("Idle eviction retained build binding");
    use("clear", first, build);
    registryType.GetMethod("ClearAll").Invoke(registry, null);
    if (find("clear") != null) throw new System.Exception("ClearAll retained build binding");
} finally {
    System.IO.File.Delete(decision);
    if (System.IO.Directory.Exists(first)) System.IO.Directory.Delete(first);
    if (System.IO.Directory.Exists(second)) System.IO.Directory.Delete(second);
}
"PASS: completion registration/replacement/skip, compiler invalidation, explicit overrides, missing decisions, reset and eviction"
