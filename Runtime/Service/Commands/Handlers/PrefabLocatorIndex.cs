using System;
using System.Collections.Generic;
using System.Globalization;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
#if UNITY_EDITOR
    internal sealed class PrefabLocatorIndex
    {
        private const string LocatorPrefix = "gid:";
        private readonly Dictionary<long, GameObject> gameObjectsByLocalId;
        private readonly Dictionary<GameObject, long> localIdsByGameObject;

        private PrefabLocatorIndex(
            GameObject root,
            string assetPath,
            string assetGuid,
            long rootLocalId,
            Dictionary<long, GameObject> gameObjectsByLocalId,
            Dictionary<GameObject, long> localIdsByGameObject)
        {
            Root = root;
            AssetPath = assetPath;
            AssetGuid = assetGuid;
            RootLocalId = rootLocalId;
            this.gameObjectsByLocalId = gameObjectsByLocalId;
            this.localIdsByGameObject = localIdsByGameObject;
        }

        internal GameObject Root { get; }
        internal string AssetPath { get; }
        internal string AssetGuid { get; }
        internal long RootLocalId { get; }

        internal static PrefabLocatorIndex Create(
            GameObject root,
            string assetPath,
            out string error)
        {
            error = null;
            if (root == null)
            {
                error = $"No prefab asset found at '{assetPath}'";
                return null;
            }

            if (!TryGetAssetIdentity(
                    assetPath,
                    out var canonicalAssetPath,
                    out var assetGuid,
                    out error))
            {
                return null;
            }

            var byLocalId = new Dictionary<long, GameObject>();
            var byGameObject = new Dictionary<GameObject, long>();
            var pending = new Stack<Transform>();
            pending.Push(root.transform);
            while (pending.Count > 0)
            {
                var candidate = pending.Pop().gameObject;
                if (!TryGetSourceIdentity(
                        candidate,
                        canonicalAssetPath,
                        assetGuid,
                        out var localId,
                        out error))
                {
                    return null;
                }

                if (byLocalId.TryGetValue(localId, out var existing) &&
                    existing != candidate)
                {
                    error = $"Prefab asset '{assetPath}' maps multiple GameObjects to local file ID {localId}";
                    return null;
                }
                if (byGameObject.ContainsKey(candidate))
                {
                    error = $"Prefab hierarchy '{assetPath}' contains the same GameObject more than once";
                    return null;
                }

                byLocalId[localId] = candidate;
                byGameObject[candidate] = localId;

                var transform = candidate.transform;
                for (var index = transform.childCount - 1; index >= 0; index--)
                    pending.Push(transform.GetChild(index));
            }

            if (!byGameObject.TryGetValue(root, out var rootLocalId))
            {
                error = $"Prefab root identity could not be indexed for '{assetPath}'";
                return null;
            }

            return new PrefabLocatorIndex(
                root,
                canonicalAssetPath,
                assetGuid,
                rootLocalId,
                byLocalId,
                byGameObject);
        }

        internal GameObject Resolve(string locator, out string error)
        {
            error = null;
            if (locator == "")
                return Root;

            if (!TryParseLocator(locator, out var guid, out var localId, out error))
                return null;
            if (!string.Equals(guid, AssetGuid, StringComparison.Ordinal))
            {
                error =
                    $"Prefab GameObject locator belongs to asset GUID '{guid}', "
                    + $"not '{AssetGuid}' for '{AssetPath}'";
                return null;
            }
            if (localId == RootLocalId)
            {
                error = "The prefab root must use an empty gameObjectPath";
                return null;
            }
            if (!gameObjectsByLocalId.TryGetValue(localId, out var gameObject))
            {
                error = $"No GameObject with local file ID {localId} exists in prefab '{AssetPath}'";
                return null;
            }

            return gameObject;
        }

        internal string GetLocator(GameObject gameObject, out string error)
        {
            error = null;
            if (gameObject == null ||
                !localIdsByGameObject.TryGetValue(gameObject, out var localId))
            {
                error = $"GameObject is not part of the request-scoped prefab index for '{AssetPath}'";
                return null;
            }

            if (localId == RootLocalId)
            {
                if (gameObject != Root)
                {
                    error = $"Prefab asset '{AssetPath}' maps a non-root GameObject to the root local file ID";
                    return null;
                }
                return "";
            }

            return FormatLocator(AssetGuid, localId);
        }

        internal bool TryGetLocalId(GameObject gameObject, out long localId)
        {
            if (gameObject != null &&
                localIdsByGameObject.TryGetValue(gameObject, out localId))
            {
                return true;
            }

            localId = 0;
            return false;
        }

        internal bool TryGetGameObject(long localId, out GameObject gameObject)
        {
            return gameObjectsByLocalId.TryGetValue(localId, out gameObject);
        }

        internal HashSet<long> CopyLocalIds()
        {
            return new HashSet<long>(gameObjectsByLocalId.Keys);
        }

        internal long[] GetDirectChildIds(GameObject parent, out string error)
        {
            error = null;
            if (!TryGetLocalId(parent, out _))
            {
                error = $"Parent is not part of the prefab index for '{AssetPath}'";
                return null;
            }

            var transform = parent.transform;
            var ids = new long[transform.childCount];
            for (var index = 0; index < transform.childCount; index++)
            {
                var child = transform.GetChild(index).gameObject;
                if (!TryGetLocalId(child, out ids[index]))
                {
                    error = $"Child '{child.name}' is missing from the prefab index for '{AssetPath}'";
                    return null;
                }
            }

            return ids;
        }

        internal HashSet<long> GetSubtreeIds(GameObject subtreeRoot, out string error)
        {
            error = null;
            if (!TryGetLocalId(subtreeRoot, out _))
            {
                error = $"GameObject is not part of the prefab index for '{AssetPath}'";
                return null;
            }

            var ids = new HashSet<long>();
            var pending = new Stack<Transform>();
            pending.Push(subtreeRoot.transform);
            while (pending.Count > 0)
            {
                var transform = pending.Pop();
                if (!TryGetLocalId(transform.gameObject, out var localId))
                {
                    error = $"Subtree GameObject '{transform.name}' is missing from the prefab index for '{AssetPath}'";
                    return null;
                }
                if (!ids.Add(localId))
                {
                    error = $"Subtree in '{AssetPath}' contains duplicate local file ID {localId}";
                    return null;
                }

                for (var index = transform.childCount - 1; index >= 0; index--)
                    pending.Push(transform.GetChild(index));
            }

            return ids;
        }

        internal static bool TryParseLocator(
            string locator,
            out string guid,
            out long localId,
            out string error)
        {
            guid = null;
            localId = 0;
            error = null;
            const int guidStart = 4;
            const int guidLength = 32;
            const int idSeparator = guidStart + guidLength;
            if (locator == null ||
                locator.Length <= idSeparator + 1 ||
                !locator.StartsWith(LocatorPrefix, StringComparison.Ordinal) ||
                locator[idSeparator] != ':' ||
                locator.IndexOf(':', idSeparator + 1) >= 0)
            {
                error =
                    $"Invalid prefab GameObject locator '{locator}': expected "
                    + "'gid:<32 lowercase hex>:<canonical Int64>'";
                return false;
            }

            guid = locator.Substring(guidStart, guidLength);
            var localIdText = locator.Substring(idSeparator + 1);
            if (!IsCanonicalGuid(guid) ||
                !long.TryParse(
                    localIdText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out localId) ||
                !string.Equals(
                    localId.ToString(CultureInfo.InvariantCulture),
                    localIdText,
                    StringComparison.Ordinal))
            {
                error = $"Invalid prefab GameObject locator '{locator}': GUID and local file ID must be canonical";
                return false;
            }

            return true;
        }

        private static string FormatLocator(string guid, long localId)
        {
            return $"{LocatorPrefix}{guid}:{localId.ToString(CultureInfo.InvariantCulture)}";
        }

        private static bool TryGetAssetIdentity(
            string requestedAssetPath,
            out string canonicalAssetPath,
            out string assetGuid,
            out string error)
        {
            canonicalAssetPath = null;
            assetGuid = (AssetDatabase.AssetPathToGUID(requestedAssetPath) ?? "")
                .ToLowerInvariant();
            if (!IsCanonicalGuid(assetGuid))
            {
                error = $"No canonical asset GUID exists for prefab '{requestedAssetPath}'";
                return false;
            }

            canonicalAssetPath = (AssetDatabase.GUIDToAssetPath(assetGuid) ?? "")
                .Replace('\\', '/');
            if (string.IsNullOrEmpty(canonicalAssetPath) ||
                !string.Equals(
                    (AssetDatabase.AssetPathToGUID(canonicalAssetPath) ?? "")
                        .ToLowerInvariant(),
                    assetGuid,
                    StringComparison.Ordinal))
            {
                error =
                    $"Prefab GUID '{assetGuid}' does not resolve to one canonical asset path";
                canonicalAssetPath = null;
                assetGuid = null;
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryGetSourceIdentity(
            GameObject candidate,
            string assetPath,
            string expectedGuid,
            out long localId,
            out string error)
        {
            localId = 0;
            error = null;
            if (candidate == null)
            {
                error = $"Cannot map a missing GameObject to prefab '{assetPath}'";
                return false;
            }

            // The explicit target path keeps nested Prefab and Variant mapping
            // on the requested asset.
            var source = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(
                candidate,
                assetPath);
            string guid = null;
            if (source == null &&
                EditorUtility.IsPersistent(candidate) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    candidate,
                    out var candidateGuid,
                    out long candidateLocalId) &&
                string.Equals(
                    (candidateGuid ?? "").ToLowerInvariant(),
                    expectedGuid,
                    StringComparison.Ordinal) &&
                string.Equals(
                    (AssetDatabase.GetAssetPath(candidate) ?? "").Replace('\\', '/'),
                    assetPath,
                    StringComparison.Ordinal))
            {
                source = candidate;
                guid = expectedGuid;
                localId = candidateLocalId;
            }

            if (source == null || !EditorUtility.IsPersistent(source))
            {
                error = $"GameObject '{candidate.name}' has no persistent source in prefab '{assetPath}'";
                return false;
            }

            var sourceAssetPath = (AssetDatabase.GetAssetPath(source) ?? "")
                .Replace('\\', '/');
            if (!string.Equals(
                    sourceAssetPath,
                    assetPath,
                    StringComparison.Ordinal))
            {
                error =
                    $"GameObject '{candidate.name}' maps to '{sourceAssetPath}', "
                    + $"not prefab asset '{assetPath}'";
                return false;
            }

            if (guid == null &&
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out localId))
            {
                error = $"Cannot read the persistent identity of GameObject '{candidate.name}' in prefab '{assetPath}'";
                return false;
            }

            guid = (guid ?? "").ToLowerInvariant();
            if (!IsCanonicalGuid(guid) ||
                !string.Equals(guid, expectedGuid, StringComparison.Ordinal))
            {
                error = $"GameObject '{candidate.name}' maps outside prefab asset '{assetPath}'";
                return false;
            }

            return true;
        }

        private static bool IsCanonicalGuid(string guid)
        {
            if (guid == null || guid.Length != 32)
                return false;

            for (var index = 0; index < guid.Length; index++)
            {
                var character = guid[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
#endif
}
