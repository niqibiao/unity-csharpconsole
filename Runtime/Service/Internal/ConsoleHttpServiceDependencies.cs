using System;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ConsoleHttpServiceDependencies
    {
        internal sealed class CachedRequestBody
        {
            public byte[] Bytes = Array.Empty<byte>();
            public string Text = "";
            public InvocationClaim InvocationClaim;
        }

        private static readonly ConditionalWeakTable<HttpListenerContext, CachedRequestBody> s_RequestBodies =
            new ConditionalWeakTable<HttpListenerContext, CachedRequestBody>();

        public ConsoleHttpServiceDependencies(
            HttpEnvelopeFactory envelopeFactory,
            Func<HealthResponse> buildHealthResponseSnapshot,
            Func<HttpListenerContext, HttpResponseEnvelope, string, Task> writeEnvelopeResponseAsync,
            Func<string, IREPLCompiler> fetchEditorReplCompiler,
            Func<string, string, IREPLCompiler> fetchRuntimeReplCompiler)
        {
            EnvelopeFactory = envelopeFactory;
            BuildHealthResponseSnapshot = buildHealthResponseSnapshot;
            WriteEnvelopeResponseAsync = writeEnvelopeResponseAsync;
            FetchEditorReplCompiler = fetchEditorReplCompiler;
            FetchRuntimeReplCompiler = fetchRuntimeReplCompiler;
        }

        public HttpEnvelopeFactory EnvelopeFactory { get; }

        public Func<HealthResponse> BuildHealthResponseSnapshot { get; }

        public Func<HttpListenerContext, HttpResponseEnvelope, string, Task> WriteEnvelopeResponseAsync { get; }

        public Func<string, IREPLCompiler> FetchEditorReplCompiler { get; }

        public Func<string, string, IREPLCompiler> FetchRuntimeReplCompiler { get; }

        public static async Task<string> ReadRequestBodyAsync(HttpListenerContext context)
        {
            return (await PrepareRequestBodyAsync(context)).Text;
        }

        public static async Task<CachedRequestBody> PrepareRequestBodyAsync(HttpListenerContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (s_RequestBodies.TryGetValue(context, out var cached))
            {
                return cached;
            }

            using var stream = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(stream);
            var bytes = stream.ToArray();
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            cached = new CachedRequestBody
            {
                Bytes = bytes,
                Text = text
            };
            try
            {
                s_RequestBodies.Add(context, cached);
            }
            catch (ArgumentException)
            {
                // A concurrent preparer won. This should not occur in the normal
                // single-dispatch flow, but returning the canonical cache keeps
                // downstream fingerprinting consistent if it does.
                if (s_RequestBodies.TryGetValue(context, out var existing))
                {
                    return existing;
                }

                throw;
            }

            return cached;
        }

        public static void AttachInvocationClaim(HttpListenerContext context, InvocationClaim claim)
        {
            if (!s_RequestBodies.TryGetValue(context, out var cached))
            {
                throw new InvalidOperationException("Request body must be prepared before attaching an invocation claim.");
            }

            cached.InvocationClaim = claim;
        }

        public static InvocationClaim GetInvocationClaim(HttpListenerContext context)
        {
            return context != null && s_RequestBodies.TryGetValue(context, out var cached)
                ? cached.InvocationClaim
                : null;
        }

        public static void ReleaseRequest(HttpListenerContext context)
        {
            if (context != null)
            {
                s_RequestBodies.Remove(context);
            }
        }
    }
}
