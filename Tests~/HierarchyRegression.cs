// Execute in the Editor; validate the JSON files with test_hierarchy_results.py.
var output = System.IO.Path.GetFullPath("Temp/CSharpConsole/AgentScratch/hierarchy-regression");
System.IO.Directory.CreateDirectory(output);
var assembly = typeof(Zh1Zh1.CSharpConsole.Service.Commands.Handlers.CommandHelpers).Assembly;
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var sceneMethod = assembly.GetType("Zh1Zh1.CSharpConsole.Service.Commands.Handlers.SceneCommandActions").GetMethod("Hierarchy", flags);
var prefabMethod = assembly.GetType("Zh1Zh1.CSharpConsole.Service.Commands.Handlers.PrefabCommandActions").GetMethod("AssetHierarchy", flags);
var originalScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
var assetFolder = "Assets/__HierarchyRegression_" + System.Guid.NewGuid().ToString("N");
var warnings = new System.Collections.Generic.List<string>();
UnityEngine.Application.LogCallback capture = (text, trace, kind) => {
    if (text.Contains("Serialization depth limit")) warnings.Add(text);
};
UnityEngine.Application.logMessageReceived += capture;
System.Action<string, object> save = (name, response) => {
    var result = (Zh1Zh1.CSharpConsole.Service.Commands.Core.CommandResponse)response;
    if (!result.ok) throw new System.Exception(result.summary);
    System.IO.File.WriteAllText(System.IO.Path.Combine(output, name + ".json"), result.resultJson);
};
try {
    save("scene-empty", sceneMethod.Invoke(null, new object[] { -1, false }));
    var root = new UnityEngine.GameObject("HierarchyRegression");
    var current = root.transform;
    for (var i = 1; i < 32; i++) {
        var child = new UnityEngine.GameObject("node-" + i);
        child.transform.SetParent(current, false);
        current = child.transform;
    }
    current.name = "leaf\"\\中文\nline";
    save("scene-deep", sceneMethod.Invoke(null, new object[] { -1, true }));
    save("scene-depth", sceneMethod.Invoke(null, new object[] { 1, true }));
    UnityEditor.AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(assetFolder));
    var prefab = assetFolder + "/deep.prefab";
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefab);
    save("prefab-deep", prefabMethod.Invoke(null, new object[] { prefab, -1, true }));
    save("prefab-depth", prefabMethod.Invoke(null, new object[] { prefab, 1, true }));
    for (var i = 32; i < 160; i++) {
        var child = new UnityEngine.GameObject("node-" + i);
        child.transform.SetParent(current, false);
        current = child.transform;
    }
    save("scene-safety-depth", sceneMethod.Invoke(null, new object[] { -1, false }));
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefab);
    save("prefab-safety-depth", prefabMethod.Invoke(null, new object[] { prefab, -1, false }));
    root.AddComponent<UnityEngine.BoxCollider>();
    var objectMethod = assembly.GetType("Zh1Zh1.CSharpConsole.Service.Commands.Handlers.GameObjectCommandActions").GetMethod("Get", flags);
    var componentMethod = assembly.GetType("Zh1Zh1.CSharpConsole.Service.Commands.Handlers.ComponentCommandActions").GetMethod("Get", flags);
    save("gameobject-get", objectMethod.Invoke(null, new object[] { "", root.GetInstanceID() }));
    save("component-get", componentMethod.Invoke(null, new object[] { typeof(UnityEngine.BoxCollider).FullName, "", root.GetInstanceID(), 0 }));
    UnityEngine.Object.DestroyImmediate(root);
    root = new UnityEngine.GameObject("HierarchyRegression");
    for (var i = 0; i < 5000; i++) {
        var child = new UnityEngine.GameObject("wide-" + i);
        child.transform.SetParent(root.transform, false);
    }
    save("scene-budget", sceneMethod.Invoke(null, new object[] { -1, false }));
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefab);
    save("prefab-budget", prefabMethod.Invoke(null, new object[] { prefab, -1, false }));
    UnityEngine.Object.DestroyImmediate(root.transform.GetChild(root.transform.childCount - 1).gameObject);
    save("scene-exact-budget", sceneMethod.Invoke(null, new object[] { -1, false }));
} finally {
    UnityEngine.Application.logMessageReceived -= capture;
    System.IO.File.WriteAllText(System.IO.Path.Combine(output, "warnings.json"), "[" + string.Join(",", System.Linq.Enumerable.Select(warnings, message => "\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"")) + "]");
    UnityEngine.SceneManagement.SceneManager.SetActiveScene(originalScene);
    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
    if (UnityEditor.AssetDatabase.IsValidFolder(assetFolder)) UnityEditor.AssetDatabase.DeleteAsset(assetFolder);
}
"Generated hierarchy regression JSON under " + output
