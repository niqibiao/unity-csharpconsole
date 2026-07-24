using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
#if UNITY_EDITOR
    /// <summary>
    /// Owns the Editor-log marker protocol and bounded diagnostic reads.
    /// The command interface stays small while file snapshot, marker lookup,
    /// encoding, record boundaries, and response budgeting remain local here.
    /// </summary>
    internal static class EditorConsoleDiagnostics
    {
        private const string MarkerPrefix = "[C#Console][ConsoleMark] id=";
        private const int MaxMarkerSearchBytes = 8 * 1024 * 1024;
        private const int TailReadBytes = 64 * 1024;
        private const int MaxResultJsonBytes = 16 * 1024;
        private static readonly Encoding s_StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        [Serializable]
        private sealed class ConsoleMarkResult
        {
            public string logPath = "";
            public string id = "";
            public string label = "";
            public string timestampUtc = "";
            public string markerText = "";
        }

        [Serializable]
        private sealed class ConsoleGetResult
        {
            public string text = "";
            public bool truncated;
        }

        internal static CommandResponse Mark(string label)
        {
            var markerId = Guid.NewGuid().ToString("N");
            var timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var trimmedLabel = (label ?? "").Trim();
            if (trimmedLabel.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                return CommandResponseFactory.ValidationError(
                    "editor/console.mark label must be a single line");
            }
            if (trimmedLabel.Length > 200)
            {
                return CommandResponseFactory.ValidationError(
                    "editor/console.mark label must not exceed 200 characters");
            }
            if (trimmedLabel.IndexOf(MarkerPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CommandResponseFactory.ValidationError(
                    "editor/console.mark label cannot contain the reserved marker prefix");
            }

            var markerText = string.IsNullOrEmpty(trimmedLabel)
                ? $"{MarkerPrefix}{markerId} utc={timestampUtc}"
                : $"{MarkerPrefix}{markerId} utc={timestampUtc} label={trimmedLabel}";

            Debug.Log(markerText);

            var result = new ConsoleMarkResult
            {
                logPath = ResolveEditorLogPath(),
                id = markerId,
                label = trimmedLabel,
                timestampUtc = timestampUtc,
                markerText = markerText
            };

            return CommandResponseFactory.Ok(
                $"Wrote console marker '{markerId}'",
                JsonUtility.ToJson(result));
        }

        internal static CommandResponse Get(string afterMarkerId)
        {
            var markerText = (afterMarkerId ?? "").Trim();
            var hasMarker = !string.IsNullOrEmpty(markerText);
            var markerId = Guid.Empty;
            if (hasMarker && !Guid.TryParseExact(markerText, "N", out markerId))
            {
                return CommandResponseFactory.ValidationError(
                    "afterMarkerId must be a 32-character hexadecimal id returned by editor/console.mark");
            }

            var logPath = ResolveEditorLogPath();
            if (string.IsNullOrEmpty(logPath))
            {
                return CommandResponseFactory.SystemError(
                    "Unity 2022 Editor log path is unavailable");
            }

            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var snapshotLength = stream.Length;

                if (hasMarker)
                {
                    return ReadAfterMarker(stream, snapshotLength, markerId.ToString("N"));
                }

                return ReadTail(stream, snapshotLength);
            }
            catch (FileNotFoundException)
            {
                return CommandResponseFactory.SystemError(
                    "Unity 2022 Editor log does not exist");
            }
            catch (DirectoryNotFoundException)
            {
                return CommandResponseFactory.SystemError(
                    "Unity 2022 Editor log directory does not exist");
            }
            catch (Exception e)
            {
                return CommandResponseFactory.SystemError(
                    $"Could not read the Unity 2022 Editor log: {e.Message}");
            }
        }

        private static CommandResponse ReadAfterMarker(
            FileStream stream,
            long snapshotLength,
            string markerId)
        {
            var readLength = (int)Math.Min(snapshotLength, MaxMarkerSearchBytes);
            var readStart = snapshotLength - readLength;
            var window = ReadUtf8Window(stream, readStart, readLength);
            var markerToken = MarkerPrefix + markerId + " utc=";
            // The generated GUID cannot be known before the real marker is
            // written. Select its first complete marker prefix so later user
            // logs that echo markerText cannot move the causal boundary.
            var markerIndex = window.IndexOf(
                markerToken,
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return CommandResponseFactory.SystemError(
                    $"Console marker '{markerId}' is unavailable; do not infer a clean result or repeat an unresolved operation. Write a new editor/console.mark before a new diagnostic window");
            }

            var contentStart = FindRecordEnd(window, markerIndex + markerToken.Length);
            var text = contentStart >= window.Length
                ? ""
                : window.Substring(contentStart).TrimStart('\n');
            return BuildBoundedResponse(text, preferStart: true, sourceTruncated: false);
        }

        private static CommandResponse ReadTail(FileStream stream, long snapshotLength)
        {
            var readLength = (int)Math.Min(snapshotLength, TailReadBytes);
            var readStart = snapshotLength - readLength;
            var text = ReadUtf8Window(stream, readStart, readLength);
            var sourceTruncated = readStart > 0;
            if (sourceTruncated)
            {
                text = TrimPartialLeadingRecord(text);
            }

            return BuildBoundedResponse(
                text.TrimStart('\n'),
                preferStart: false,
                sourceTruncated: sourceTruncated);
        }

        private static CommandResponse BuildBoundedResponse(
            string sourceText,
            bool preferStart,
            bool sourceTruncated)
        {
            var normalized = NormalizeNewlines(sourceText ?? "").TrimEnd('\n');
            var completeResult = new ConsoleGetResult
            {
                text = normalized,
                truncated = sourceTruncated
            };
            var completeJson = JsonUtility.ToJson(completeResult);
            if (Encoding.UTF8.GetByteCount(completeJson) <= MaxResultJsonBytes)
            {
                return CommandResponseFactory.Ok(
                    "Read bounded Unity Editor log output",
                    completeJson);
            }

            var low = 0;
            var high = normalized.Length;
            var best = "";
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = preferStart
                    ? SafePrefix(normalized, middle)
                    : SafeSuffix(normalized, middle);
                var candidateJson = JsonUtility.ToJson(new ConsoleGetResult
                {
                    text = candidate,
                    truncated = true
                });
                if (Encoding.UTF8.GetByteCount(candidateJson) <= MaxResultJsonBytes)
                {
                    best = candidate;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            best = preferStart
                ? TrimPartialTrailingLine(best)
                : TrimPartialLeadingLine(best);
            var boundedJson = JsonUtility.ToJson(new ConsoleGetResult
            {
                text = best,
                truncated = true
            });
            return CommandResponseFactory.Ok(
                "Read truncated Unity Editor log output",
                boundedJson);
        }

        private static string ReadUtf8Window(
            FileStream stream,
            long start,
            int requestedLength)
        {
            if (requestedLength <= 0)
            {
                return "";
            }

            stream.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[requestedLength];
            var totalRead = 0;
            while (totalRead < requestedLength)
            {
                var read = stream.Read(bytes, totalRead, requestedLength - totalRead);
                if (read <= 0)
                {
                    throw new IOException(
                        "Editor log changed before its captured snapshot could be read");
                }

                totalRead += read;
            }

            var offset = 0;
            if (start > 0)
            {
                while (offset < bytes.Length && (bytes[offset] & 0xC0) == 0x80)
                {
                    offset++;
                }
            }

            string text;
            try
            {
                text = s_StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException e)
            {
                throw new IOException(
                    "Captured Editor log snapshot ended with incomplete or invalid UTF-8 data; retry the read-only command",
                    e);
            }

            return NormalizeNewlines(text).TrimStart('\uFEFF');
        }

        private static int FindRecordEnd(string text, int searchStart)
        {
            var boundary = text.IndexOf("\n\n", searchStart, StringComparison.Ordinal);
            if (boundary >= 0)
            {
                var contentStart = boundary + 2;
                if (text.IndexOf("(Filename:", contentStart, StringComparison.Ordinal) == contentStart)
                {
                    var filenameBoundary = text.IndexOf(
                        "\n\n",
                        contentStart,
                        StringComparison.Ordinal);
                    if (filenameBoundary >= 0)
                    {
                        return filenameBoundary + 2;
                    }

                    throw new IOException(
                        "Captured Editor log snapshot ended inside the console marker footer; retry the read-only command");
                }

                return contentStart;
            }

            throw new IOException(
                "Captured Editor log snapshot ended inside the console marker record; retry the read-only command");
        }

        private static string TrimPartialLeadingRecord(string text)
        {
            var boundary = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (boundary >= 0)
            {
                return text.Substring(boundary + 2);
            }

            return TrimPartialLeadingLine(text);
        }

        private static string TrimPartialLeadingLine(string text)
        {
            var lineEnd = text.IndexOf('\n');
            return lineEnd >= 0 ? text.Substring(lineEnd + 1) : "";
        }

        private static string TrimPartialTrailingLine(string text)
        {
            var lineEnd = text.LastIndexOf('\n');
            return lineEnd >= 0 ? text.Substring(0, lineEnd) : text;
        }

        private static string SafePrefix(string text, int length)
        {
            var safeLength = Math.Max(0, Math.Min(length, text.Length));
            if (safeLength > 0
                && safeLength < text.Length
                && char.IsHighSurrogate(text[safeLength - 1])
                && char.IsLowSurrogate(text[safeLength]))
            {
                safeLength--;
            }

            return text.Substring(0, safeLength);
        }

        private static string SafeSuffix(string text, int length)
        {
            var safeLength = Math.Max(0, Math.Min(length, text.Length));
            var start = text.Length - safeLength;
            if (start > 0
                && start < text.Length
                && char.IsLowSurrogate(text[start])
                && char.IsHighSurrogate(text[start - 1]))
            {
                start++;
            }

            return text.Substring(start);
        }

        private static string NormalizeNewlines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        internal static string ResolveEditorLogPath()
        {
            try
            {
                if (!string.IsNullOrEmpty(Application.consoleLogPath))
                {
                    return Application.consoleLogPath;
                }
            }
            catch
            {
                // Fall back to Unity's default editor log locations below.
            }

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    var localAppData = Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(localAppData))
                    {
                        return Path.Combine(
                            localAppData,
                            "Unity",
                            "Editor",
                            "Editor.log");
                    }
                }

                if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    var home = Environment.GetFolderPath(
                        Environment.SpecialFolder.Personal);
                    if (!string.IsNullOrEmpty(home))
                    {
                        return Path.Combine(
                            home,
                            "Library",
                            "Logs",
                            "Unity",
                            "Editor.log");
                    }
                }

                if (Application.platform == RuntimePlatform.LinuxEditor)
                {
                    var home = Environment.GetFolderPath(
                        Environment.SpecialFolder.Personal);
                    if (!string.IsNullOrEmpty(home))
                    {
                        return Path.Combine(
                            home,
                            ".config",
                            "unity3d",
                            "Editor.log");
                    }
                }
            }
            catch
            {
                // Ignore fallback resolution failures and return empty below.
            }

            return "";
        }
    }
#endif
}
