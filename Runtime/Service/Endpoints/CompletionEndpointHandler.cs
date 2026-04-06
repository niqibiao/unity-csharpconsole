using System;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Interface;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Endpoints
{
    internal sealed class CompletionEndpointHandler
    {
        private readonly ConsoleHttpServiceDependencies _dependencies;

        public CompletionEndpointHandler(ConsoleHttpServiceDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task Handle(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
            CompletionResponse responseData = new() { items = Array.Empty<CompletionItemJson>() };

            try
            {
                var req = JsonUtility.FromJson<CompletionRequest>(message);

                IREPLCompiler compiler = !string.IsNullOrEmpty(req.runtimeDllPath)
                    ? _dependencies.FetchRuntimeReplCompiler(req.uuid, req.runtimeDllPath)
                    : _dependencies.FetchEditorReplCompiler(req.uuid);

                if (compiler is IREPLCompletionProvider completionProvider)
                {
                    var items = completionProvider.GetCompletions(req.code, req.cursorPosition, req.defines, req.defaultUsing);
                    responseData = new CompletionResponse
                    {
                        items = new CompletionItemJson[items.Count]
                    };
                    for (int i = 0; i < items.Count; i++)
                    {
                        responseData.items[i] = new CompletionItemJson
                        {
                            label = items[i].Label,
                            kind = items[i].Kind,
                            detail = items[i].Detail,
                            accessibility = items[i].Accessibility
                        };
                    }
                }
                else
                {
                    responseData = new CompletionResponse
                    {
                        error = $"Compiler ({compiler?.GetType().Name}) does not implement IREPLCompletionProvider"
                    };
                }
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Completion exception: {e}");
                responseData = new CompletionResponse { error = e.ToString() };
            }

            var ok = string.IsNullOrEmpty(responseData.error);
            var summary = ok
                ? $"Returned {responseData.items?.Length ?? 0} completion items"
                : responseData.error;
            var envelope = _dependencies.EnvelopeFactory.CreateEnvelope(
                ok,
                "compile",
                ok ? "ok" : "runtime_error",
                summary,
                "",
                JsonUtility.ToJson(responseData));
            await _dependencies.WriteEnvelopeResponseAsync(context, envelope, "Completion");
        }

    }
}
