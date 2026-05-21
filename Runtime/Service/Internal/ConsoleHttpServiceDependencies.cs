using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ConsoleHttpServiceDependencies
    {
        public ConsoleHttpServiceDependencies(
            HttpEnvelopeFactory envelopeFactory,
            Func<HealthResponse> buildHealthResponseSnapshot,
            Func<HttpListenerContext, HttpResponseEnvelope, string, Task> writeEnvelopeResponseAsync,
            Func<string, IREPLCompiler> fetchEditorReplCompiler,
            Func<string, string, IREPLCompiler> fetchRuntimeReplCompiler,
            Func<string, IREPLCompletionProvider> fetchLiteCompletionProvider)
        {
            EnvelopeFactory = envelopeFactory;
            BuildHealthResponseSnapshot = buildHealthResponseSnapshot;
            WriteEnvelopeResponseAsync = writeEnvelopeResponseAsync;
            FetchEditorReplCompiler = fetchEditorReplCompiler;
            FetchRuntimeReplCompiler = fetchRuntimeReplCompiler;
            FetchLiteCompletionProvider = fetchLiteCompletionProvider;
        }

        public HttpEnvelopeFactory EnvelopeFactory { get; }

        public Func<HealthResponse> BuildHealthResponseSnapshot { get; }

        public Func<HttpListenerContext, HttpResponseEnvelope, string, Task> WriteEnvelopeResponseAsync { get; }

        public Func<string, IREPLCompiler> FetchEditorReplCompiler { get; }

        public Func<string, string, IREPLCompiler> FetchRuntimeReplCompiler { get; }

        // Returns the IREPLCompletionProvider for the Lite session keyed by
        // sessionId. Lite sessions live in a separate registry from the
        // HybridCLR-path IREPLCompiler set; this getter does the per-session
        // fetch (creating a fresh LiteREPLCompiler if the session doesn't
        // exist yet) and casts to the completion-provider interface — null
        // only if LiteREPLCompiler stops implementing it.
        public Func<string, IREPLCompletionProvider> FetchLiteCompletionProvider { get; }

        public static async Task<string> ReadRequestBodyAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }
    }
}
