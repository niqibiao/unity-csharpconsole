using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Endpoints
{
    // Retrieves a file from the machine running the service. Commands that
    // produce artifacts -- screenshots, profiler binary logs, a project's own
    // log file -- write them to local disk and report a path, which is useless
    // when the service is on a phone or a colleague's machine. This is the way
    // those bytes come back.
    //
    // The path is unrestricted on purpose: a session can already read any file
    // by executing C#, so restricting this route would only push callers back to
    // that, without making anything safer. What is bounded is one request's
    // size, so a mistaken call cannot pull gigabytes into memory; larger files
    // are fetched with Range.
    internal sealed class DownloadEndpointHandler
    {
        internal const int MAX_SINGLE_RESPONSE_BYTES = 32 * 1024 * 1024;

        private readonly ConsoleHttpServiceDependencies _dependencies;

        public DownloadEndpointHandler(ConsoleHttpServiceDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        [Serializable]
        private sealed class DownloadError
        {
            public string path = "";
            public string error = "";
            public long fileSize;
        }

        public async Task Handle(HttpListenerContext context)
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteError(context, 405, "", "The download route accepts GET only", 0);
                return;
            }

            var path = context.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                await WriteError(context, 400, "", "A 'path' query parameter is required", 0);
                return;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception e)
            {
                await WriteError(context, 400, path, $"Path could not be read: {e.Message}", 0);
                return;
            }

            if (!info.Exists)
            {
                await WriteError(context, 404, path, "No file exists at that path", 0);
                return;
            }

            var total = info.Length;
            if (!TryParseRange(context.Request.Headers["Range"], total, out var offset, out var length, out var rangeError))
            {
                await WriteError(context, 416, path, rangeError, total);
                return;
            }

            if (length > MAX_SINGLE_RESPONSE_BYTES)
            {
                await WriteError(
                    context,
                    413,
                    path,
                    $"The requested {length} bytes exceed the {MAX_SINGLE_RESPONSE_BYTES}-byte limit for one response; request a Range instead",
                    total);
                return;
            }

            try
            {
                await WriteFileRange(context, info, offset, length, total);
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"[Download] Failed to send '{path}': {e.Message}");
            }
        }

        // Accepts the single-range forms "bytes=start-end", "bytes=start-" and
        // "bytes=-suffix". Absent or unparsable headers mean the whole file.
        private static bool TryParseRange(string header, long total, out long offset, out long length, out string error)
        {
            offset = 0;
            length = total;
            error = "";

            if (string.IsNullOrWhiteSpace(header))
            {
                return true;
            }

            const string prefix = "bytes=";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "Only byte ranges are supported";
                return false;
            }

            var spec = header.Substring(prefix.Length).Trim();
            if (spec.Contains(","))
            {
                error = "Only a single range per request is supported";
                return false;
            }

            var dash = spec.IndexOf('-');
            if (dash < 0)
            {
                error = $"Malformed range '{header}'";
                return false;
            }

            var startText = spec.Substring(0, dash).Trim();
            var endText = spec.Substring(dash + 1).Trim();

            if (startText.Length == 0)
            {
                if (!long.TryParse(endText, out var suffix) || suffix <= 0)
                {
                    error = $"Malformed range '{header}'";
                    return false;
                }

                length = Math.Min(suffix, total);
                offset = total - length;
                return true;
            }

            if (!long.TryParse(startText, out offset) || offset < 0 || offset >= total)
            {
                error = $"Range start is outside the {total}-byte file";
                return false;
            }

            if (endText.Length == 0)
            {
                length = total - offset;
                return true;
            }

            if (!long.TryParse(endText, out var end) || end < offset)
            {
                error = $"Malformed range '{header}'";
                return false;
            }

            length = Math.Min(end, total - 1) - offset + 1;
            return true;
        }

        private static async Task WriteFileRange(HttpListenerContext context, FileInfo info, long offset, long length, long total)
        {
            var partial = offset != 0 || length != total;
            context.Response.StatusCode = partial ? 206 : 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = length;
            if (partial)
            {
                context.Response.AddHeader("Content-Range", $"bytes {offset}-{offset + length - 1}/{total}");
            }

            try
            {
                using var stream = new FileStream(
                    info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[64 * 1024];
                var remaining = length;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer, 0, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    await context.Response.OutputStream.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        private async Task WriteError(HttpListenerContext context, int statusCode, string path, string message, long fileSize)
        {
            context.Response.StatusCode = statusCode;
            var payload = new DownloadError { path = path ?? "", error = message, fileSize = fileSize };
            var envelope = _dependencies.EnvelopeFactory.CreateEnvelope(
                false,
                "bootstrap",
                "validation_error",
                message,
                "",
                JsonUtility.ToJson(payload));
            await _dependencies.WriteEnvelopeResponseAsync(context, envelope, "Download");
        }
    }
}
