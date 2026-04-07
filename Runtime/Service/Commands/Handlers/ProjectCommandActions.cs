using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class ProjectCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(ProjectCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class SceneListResult
        {
            public string activeScenePath = "";
            public string[] scenes = Array.Empty<string>();
        }

        [Serializable]
        private sealed class SceneOpenResult
        {
            public string scenePath = "";
            public string openedPath = "";
            public bool additive;
        }

        [Serializable]
        private sealed class SceneSaveResult
        {
            public bool saved;
            public string scenePath = "";
            public bool saveAsCopy;
        }

        [Serializable]
        private sealed class SelectionGetResult
        {
            public int activeInstanceId;
            public int[] instanceIds = Array.Empty<int>();
            public string[] assetPaths = Array.Empty<string>();
        }

        [Serializable]
        private sealed class SelectionSetResult
        {
            public int count;
            public int activeInstanceId;
            public int[] instanceIds = Array.Empty<int>();
        }

        [Serializable]
        private sealed class AssetListResult
        {
            public string filter = "";
            public string[] folders = Array.Empty<string>();
            public string[] assetPaths = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AssetImportResult
        {
            public string assetPath = "";
            public bool imported;
            public bool exists;
        }

        [CommandAction("project", "scene.list", editorOnly: true, summary: "List all scenes in the project")]
        private static CommandResponse SceneList()
        {
            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var guids = AssetDatabase.FindAssets("t:Scene");
                var paths = new List<string>(guids.Length);
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        paths.Add(path);
                    }
                }

                paths.Sort(StringComparer.Ordinal);
                return new SceneListResult
                {
                    activeScenePath = EditorSceneManager.GetActiveScene().path ?? "",
                    scenes = paths.ToArray()
                };
            });

            return CommandResponseFactory.Ok($"Listed {result.scenes.Length} scene(s)", JsonUtility.ToJson(result));
        }

        [CommandAction("project", "scene.open", editorOnly: true, summary: "Open a scene by path")]
        private static CommandResponse SceneOpen(string scenePath, string mode = "single")
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return CommandResponseFactory.ValidationError("scenePath is required for project/scene.open");
            }

            var normalizedMode = (mode ?? "single").Trim();
            var isSingleMode = string.Equals(normalizedMode, "single", StringComparison.OrdinalIgnoreCase);
            var isAdditiveMode = string.Equals(normalizedMode, "additive", StringComparison.OrdinalIgnoreCase);
            if (!isSingleMode && !isAdditiveMode)
            {
                return CommandResponseFactory.ValidationError($"Unsupported scene open mode '{mode}'. Supported values: single, additive");
            }

            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                {
                    return (exists: false, hasDirtyScenesInSingleMode: false, opened: (SceneOpenResult)null);
                }

                var additive = isAdditiveMode;
                if (!additive)
                {
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var loadedScene = SceneManager.GetSceneAt(i);
                        if (loadedScene.IsValid() && loadedScene.isLoaded && loadedScene.isDirty)
                        {
                            return (exists: true, hasDirtyScenesInSingleMode: true, opened: (SceneOpenResult)null);
                        }
                    }
                }

                var scene = EditorSceneManager.OpenScene(scenePath, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
                return (exists: true, hasDirtyScenesInSingleMode: false, opened: new SceneOpenResult
                {
                    scenePath = scenePath,
                    openedPath = scene.path ?? "",
                    additive = additive
                });
            });

            if (!result.exists)
            {
                return CommandResponseFactory.ValidationError($"Scene was not found: {scenePath}");
            }

            if (result.hasDirtyScenesInSingleMode)
            {
                return CommandResponseFactory.ValidationError("Cannot open scene in single mode while loaded scenes have unsaved changes");
            }

            return CommandResponseFactory.Ok($"Opened scene '{result.opened.openedPath}'", JsonUtility.ToJson(result.opened));
        }

        [CommandAction("project", "scene.save", editorOnly: true, summary: "Save the current scene")]
        private static CommandResponse SceneSave(string scenePath = "", bool saveAsCopy = false)
        {
            var preflight = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                if (!string.IsNullOrEmpty(scenePath))
                {
                    return (canSave: true, scenePath: scenePath);
                }

                var active = EditorSceneManager.GetActiveScene();
                var activePath = active.path ?? "";
                if (string.IsNullOrEmpty(activePath))
                {
                    return (canSave: false, scenePath: "");
                }

                return (canSave: true, scenePath: activePath);
            });

            if (!preflight.canSave)
            {
                return CommandResponseFactory.ValidationError("scenePath is required for project/scene.save when active scene has no saved path");
            }

            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var active = EditorSceneManager.GetActiveScene();
                var saved = string.IsNullOrEmpty(scenePath)
                    ? EditorSceneManager.SaveScene(active)
                    : EditorSceneManager.SaveScene(active, scenePath, saveAsCopy);

                return new SceneSaveResult
                {
                    saved = saved,
                    scenePath = preflight.scenePath,
                    saveAsCopy = saveAsCopy
                };
            });

            return result.saved
                ? CommandResponseFactory.Ok($"Saved scene '{result.scenePath}'", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError($"Failed to save scene '{result.scenePath}'");
        }

        [CommandAction("project", "selection.get", editorOnly: true, summary: "Get the current editor selection")]
        private static CommandResponse SelectionGet()
        {
            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var objects = Selection.objects ?? Array.Empty<UnityEngine.Object>();
                var instanceIds = new int[objects.Length];
                var assetPaths = new string[objects.Length];
                for (var i = 0; i < objects.Length; i++)
                {
                    instanceIds[i] = objects[i] ? objects[i].GetInstanceID() : 0;
                    assetPaths[i] = objects[i] ? (AssetDatabase.GetAssetPath(objects[i]) ?? "") : "";
                }

                return new SelectionGetResult
                {
                    activeInstanceId = Selection.activeObject ? Selection.activeObject.GetInstanceID() : 0,
                    instanceIds = instanceIds,
                    assetPaths = assetPaths
                };
            });

            return CommandResponseFactory.Ok($"Selected {result.instanceIds.Length} object(s)", JsonUtility.ToJson(result));
        }

        [CommandAction("project", "selection.set", editorOnly: true, summary: "Set the editor selection by name or path")]
        private static CommandResponse SelectionSet(int[] instanceIds = null, string[] assetPaths = null)
        {
            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var selected = new List<UnityEngine.Object>();

                if (instanceIds != null)
                {
                    foreach (var id in instanceIds)
                    {
                        var obj = EditorUtility.InstanceIDToObject(id);
                        if (obj != null)
                        {
                            selected.Add(obj);
                        }
                    }
                }

                if (assetPaths != null)
                {
                    foreach (var path in assetPaths)
                    {
                        if (string.IsNullOrEmpty(path))
                        {
                            continue;
                        }

                        var obj = AssetDatabase.LoadMainAssetAtPath(path);
                        if (obj != null)
                        {
                            selected.Add(obj);
                        }
                    }
                }

                var distinct = selected.Distinct().ToArray();
                Selection.objects = distinct;

                var ids = new int[distinct.Length];
                for (var i = 0; i < distinct.Length; i++)
                {
                    ids[i] = distinct[i].GetInstanceID();
                }

                return new SelectionSetResult
                {
                    count = distinct.Length,
                    activeInstanceId = Selection.activeObject ? Selection.activeObject.GetInstanceID() : 0,
                    instanceIds = ids
                };
            });

            return CommandResponseFactory.Ok($"Selected {result.count} object(s)", JsonUtility.ToJson(result));
        }

        [CommandAction("project", "asset.list", editorOnly: true, summary: "List assets by type filter")]
        private static CommandResponse AssetList(string filter = "", string[] folders = null)
        {
            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var normalizedFilter = filter ?? "";
                string[] searchFolders = null;
                if (folders != null && folders.Length > 0)
                {
                    searchFolders = folders;
                }

                var guids = AssetDatabase.FindAssets(normalizedFilter, searchFolders);
                var paths = new string[guids.Length];
                for (var i = 0; i < guids.Length; i++)
                {
                    paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]) ?? "";
                }

                Array.Sort(paths, StringComparer.Ordinal);
                return new AssetListResult
                {
                    filter = normalizedFilter,
                    folders = searchFolders ?? Array.Empty<string>(),
                    assetPaths = paths
                };
            });

            return CommandResponseFactory.Ok($"Listed {result.assetPaths.Length} asset(s)", JsonUtility.ToJson(result));
        }

        [CommandAction("project", "asset.import", editorOnly: true, summary: "Import an asset by path")]
        private static CommandResponse AssetImport(string assetPath, bool forceSynchronousImport = false)
        {
            return BuildAssetImportResponse("asset.import", assetPath, forceSynchronousImport, forceReimport: false);
        }

        [CommandAction("project", "asset.reimport", editorOnly: true, summary: "Reimport an asset by path")]
        private static CommandResponse AssetReimport(string assetPath, bool forceSynchronousImport = false)
        {
            return BuildAssetImportResponse("asset.reimport", assetPath, forceSynchronousImport, forceReimport: true);
        }

        private static CommandResponse BuildAssetImportResponse(string actionName, string assetPath, bool forceSynchronousImport, bool forceReimport)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandResponseFactory.ValidationError($"assetPath is required for project/{actionName}");
            }

            var validationState = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var targetPath = assetPath;
                var hasDirtyLoadedScenes = false;
                var targetsLoadedSceneAsset = false;

                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var loadedScene = SceneManager.GetSceneAt(i);
                    if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                    {
                        continue;
                    }

                    if (loadedScene.isDirty)
                    {
                        hasDirtyLoadedScenes = true;
                    }

                    var loadedScenePath = loadedScene.path ?? "";
                    if (!string.IsNullOrEmpty(loadedScenePath)
                        && string.Equals(loadedScenePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        targetsLoadedSceneAsset = true;
                    }
                }

                return (hasDirtyLoadedScenes, targetsLoadedSceneAsset);
            });

            if (validationState.targetsLoadedSceneAsset)
            {
                return CommandResponseFactory.ValidationError($"Cannot run project/{actionName} on currently loaded scene asset: {assetPath}");
            }

            if (validationState.hasDirtyLoadedScenes && MayTriggerDomainReload(assetPath))
            {
                return CommandResponseFactory.ValidationError($"Cannot run project/{actionName} while loaded scenes have unsaved changes (importing scripts triggers domain reload)");
            }

            var result = MainThreadRequestRunner.RunOnMainThread(() =>
            {
                var options = ImportAssetOptions.Default;
                if (forceSynchronousImport)
                {
                    options |= ImportAssetOptions.ForceSynchronousImport;
                }

                if (forceReimport)
                {
                    options |= ImportAssetOptions.ForceUpdate;
                }

                AssetDatabase.ImportAsset(assetPath, options);
                var exists = AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
                return new AssetImportResult
                {
                    assetPath = assetPath,
                    imported = true,
                    exists = exists
                };
            });

            return result.exists
                ? CommandResponseFactory.Ok($"Imported asset '{result.assetPath}'", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError($"Asset was not found after import: {result.assetPath}");
        }

        private static bool MayTriggerDomainReload(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);
        }
#endif
    }
}
