using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class AssetCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(AssetCommandActions));
#endif
        }

#if UNITY_EDITOR
        // ── move ──

        [Serializable]
        private sealed class MoveResult
        {
            public string oldPath = "";
            public string newPath = "";
        }

        [CommandAction("asset", "move", editorOnly: true, summary: "Move or rename an asset")]
        private static CommandResponse Move(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrEmpty(sourcePath))
                return CommandResponseFactory.ValidationError("sourcePath is required for asset/move");
            if (string.IsNullOrEmpty(destinationPath))
                return CommandResponseFactory.ValidationError("destinationPath is required for asset/move");

            return CommandHelpers.RunCommand<MoveResult>(
                () =>
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(sourcePath) == null && !AssetDatabase.IsValidFolder(sourcePath))
                        return (error: $"Source asset not found: {sourcePath}", result: (MoveResult)null);

                    EnsureParentFolderExists(destinationPath);
                    var validateMsg = AssetDatabase.ValidateMoveAsset(sourcePath, destinationPath);
                    if (!string.IsNullOrEmpty(validateMsg))
                        return (error: validateMsg, result: (MoveResult)null);

                    var moveMsg = AssetDatabase.MoveAsset(sourcePath, destinationPath);
                    if (!string.IsNullOrEmpty(moveMsg))
                        return (error: moveMsg, result: (MoveResult)null);

                    return (error: (string)null, result: new MoveResult
                    {
                        oldPath = sourcePath,
                        newPath = destinationPath
                    });
                },
                r => $"Moved '{r.oldPath}' -> '{r.newPath}'"
            );
        }

        // ── copy ──

        [Serializable]
        private sealed class CopyResult
        {
            public string sourcePath = "";
            public string destinationPath = "";
        }

        [CommandAction("asset", "copy", editorOnly: true, summary: "Copy an asset to a new path")]
        private static CommandResponse Copy(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrEmpty(sourcePath))
                return CommandResponseFactory.ValidationError("sourcePath is required for asset/copy");
            if (string.IsNullOrEmpty(destinationPath))
                return CommandResponseFactory.ValidationError("destinationPath is required for asset/copy");

            return CommandHelpers.RunCommand<CopyResult>(
                () =>
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(sourcePath) == null && !AssetDatabase.IsValidFolder(sourcePath))
                        return (error: $"Source asset not found: {sourcePath}", result: (CopyResult)null);

                    EnsureParentFolderExists(destinationPath);
                    if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
                        return (error: $"Failed to copy '{sourcePath}' to '{destinationPath}'", result: (CopyResult)null);

                    return (error: (string)null, result: new CopyResult
                    {
                        sourcePath = sourcePath,
                        destinationPath = destinationPath
                    });
                },
                r => $"Copied '{r.sourcePath}' -> '{r.destinationPath}'"
            );
        }

        // ── delete ──

        [Serializable]
        private sealed class DeleteResult
        {
            public string[] deletedPaths = Array.Empty<string>();
            public string[] failedPaths = Array.Empty<string>();
        }

        [CommandAction("asset", "delete", editorOnly: true, summary: "Delete one or more assets")]
        private static CommandResponse Delete(string assetPath = "", string[] assetPaths = null)
        {
            var paths = BuildPathList(assetPath, assetPaths);
            if (paths.Count == 0)
                return CommandResponseFactory.ValidationError("assetPath or assetPaths is required for asset/delete");

            return CommandHelpers.RunCommand<DeleteResult>(
                () =>
                {
                    var deleted = new List<string>();
                    var failed = new List<string>();
                    var failedAssets = new List<string>();

                    foreach (var path in paths)
                    {
                        if (AssetDatabase.GetMainAssetTypeAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
                        {
                            failed.Add(path);
                            continue;
                        }
                        failedAssets.Clear();
                        AssetDatabase.DeleteAssets(new[] { path }, failedAssets);
                        if (failedAssets.Count > 0)
                            failed.Add(path);
                        else
                            deleted.Add(path);
                    }

                    if (deleted.Count == 0 && failed.Count > 0)
                        return (error: $"All {failed.Count} asset(s) could not be deleted", result: (DeleteResult)null);

                    return (error: (string)null, result: new DeleteResult
                    {
                        deletedPaths = deleted.ToArray(),
                        failedPaths = failed.ToArray()
                    });
                },
                r => r.failedPaths.Length == 0
                    ? $"Deleted {r.deletedPaths.Length} asset(s)"
                    : $"Deleted {r.deletedPaths.Length}, failed {r.failedPaths.Length} asset(s)"
            );
        }

        // ── create_folder ──

        [Serializable]
        private sealed class CreateFolderResult
        {
            public string folderPath = "";
            public string guid = "";
        }

        [CommandAction("asset", "create_folder", editorOnly: true, summary: "Create a folder in the Asset Database")]
        private static CommandResponse CreateFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return CommandResponseFactory.ValidationError("folderPath is required for asset/create_folder");

            return CommandHelpers.RunCommand<CreateFolderResult>(
                () =>
                {
                    if (AssetDatabase.IsValidFolder(folderPath))
                        return (error: (string)null, result: new CreateFolderResult
                        {
                            folderPath = folderPath,
                            guid = AssetDatabase.AssetPathToGUID(folderPath)
                        });

                    var created = CreateFolderRecursive(folderPath);
                    if (string.IsNullOrEmpty(created))
                        return (error: $"Failed to create folder: {folderPath}", result: (CreateFolderResult)null);

                    return (error: (string)null, result: new CreateFolderResult
                    {
                        folderPath = folderPath,
                        guid = created
                    });
                },
                r => $"Folder '{r.folderPath}'"
            );
        }

        // ── helpers ──

        private static List<string> BuildPathList(string singlePath, string[] multiplePaths)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(singlePath))
                list.Add(singlePath);
            if (multiplePaths != null)
            {
                foreach (var p in multiplePaths)
                {
                    if (!string.IsNullOrEmpty(p) && !list.Contains(p))
                        list.Add(p);
                }
            }
            return list;
        }

        private static string CreateFolderRecursive(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return AssetDatabase.AssetPathToGUID(folderPath);

            var parent = System.IO.Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            var folderName = System.IO.Path.GetFileName(folderPath);

            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName))
                return null;

            if (!AssetDatabase.IsValidFolder(parent))
            {
                var parentGuid = CreateFolderRecursive(parent);
                if (string.IsNullOrEmpty(parentGuid))
                    return null;
            }

            var guid = AssetDatabase.CreateFolder(parent, folderName);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        private static void EnsureParentFolderExists(string filePath)
        {
            var parent = System.IO.Path.GetDirectoryName(filePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                CreateFolderRecursive(parent);
        }
#endif
    }
}
