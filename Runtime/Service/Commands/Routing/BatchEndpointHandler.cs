using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal sealed class BatchEndpointHandler
    {
        [Serializable]
        private sealed class BatchRequest
        {
            [Serializable]
            public sealed class BatchCommandItem
            {
                public string commandNamespace = "";
                public string action = "";
                public string sessionId = "";
                public string argsJson = "";
            }

            public BatchCommandItem[] commands = Array.Empty<BatchCommandItem>();
            public bool stopOnError;
        }

        [Serializable]
        private sealed class BatchResponseItem
        {
            public bool ok;
            public string type = "";
            public string summary = "";
            public string commandNamespace = "";
            public string action = "";
            public string sessionId = "";
            public string resultJson = "";
        }

        [Serializable]
        private sealed class BatchResponse
        {
            public bool ok;
            public int total;
            public int succeeded;
            public int failed;
            public string resultsJson = "[]";
        }

        private readonly ConsoleHttpServiceDependencies _dependencies;

        public BatchEndpointHandler(ConsoleHttpServiceDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task Handle(HttpListenerContext context)
        {
            BatchResponse batchResponse;

            try
            {
                var body = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
                if (string.IsNullOrWhiteSpace(body))
                {
                    batchResponse = CreateErrorResponse("Empty request body");
                }
                else
                {
                    var request = JsonUtility.FromJson<BatchRequest>(body);
                    batchResponse = request?.commands == null
                        ? CreateErrorResponse("Invalid batch request")
                        : ExecuteBatch(request);
                }
            }
            catch (Exception e)
            {
                batchResponse = CreateErrorResponse($"Batch processing failed: {e.Message}");
            }

            var envelope = _dependencies.EnvelopeFactory.CreateEnvelope(
                batchResponse.ok,
                "command",
                batchResponse.ok ? "ok" : "system_error",
                batchResponse.ok
                    ? $"Batch completed: {batchResponse.succeeded}/{batchResponse.total} succeeded"
                    : $"Batch failed: {batchResponse.succeeded}/{batchResponse.total} succeeded",
                "",
                JsonUtility.ToJson(batchResponse));

            await _dependencies.WriteEnvelopeResponseAsync(context, envelope, "Batch");
        }

        private static BatchResponse ExecuteBatch(BatchRequest request)
        {
            var commands = request.commands ?? Array.Empty<BatchRequest.BatchCommandItem>();
            var results = new List<string>(commands.Length);
            var succeeded = 0;
            var failed = 0;
            var allOk = true;

            foreach (var cmd in commands)
            {
                var commandRequest = new CommandRequest
                {
                    invocation = new CommandRequest.InvocationPayload
                    {
                        sessionId = cmd.sessionId ?? "",
                        argsJson = cmd.argsJson ?? "",
                        command = new CommandRequest.InvocationCommand
                        {
                            commandNamespace = cmd.commandNamespace ?? "",
                            action = cmd.action ?? ""
                        }
                    }
                };

                var response = CommandRouter.Dispatch(commandRequest)
                    ?? CommandResponseFactory.SystemError(
                        CommandInvocation.FromRequest(commandRequest),
                        "Failed to process command");

                var item = new BatchResponseItem
                {
                    ok = response.ok,
                    type = response.type ?? "",
                    summary = response.summary ?? "",
                    commandNamespace = response.commandNamespace ?? "",
                    action = response.action ?? "",
                    sessionId = response.sessionId ?? "",
                    resultJson = response.resultJson ?? ""
                };
                results.Add(JsonUtility.ToJson(item));

                if (response.ok)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                    allOk = false;
                    if (request.stopOnError)
                    {
                        break;
                    }
                }
            }

            return new BatchResponse
            {
                ok = allOk,
                total = commands.Length,
                succeeded = succeeded,
                failed = failed,
                resultsJson = "[" + string.Join(",", results) + "]"
            };
        }

        private static BatchResponse CreateErrorResponse(string message)
        {
            var errorItem = JsonUtility.ToJson(new BatchResponseItem
            {
                ok = false,
                type = "system_error",
                summary = message ?? ""
            });
            return new BatchResponse
            {
                ok = false,
                total = 0,
                succeeded = 0,
                failed = 1,
                resultsJson = "[" + errorItem + "]"
            };
        }
    }
}
