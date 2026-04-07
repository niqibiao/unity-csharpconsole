using System;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal sealed class CommandEndpointHandler
    {
        [Serializable]
        private sealed class CommandEnvelopeCommandData
        {
            public string commandNamespace = "";
            public string action = "";
            public string sessionId = "";
        }

        [Serializable]
        private sealed class CommandEnvelopeData
        {
            public CommandEnvelopeCommandData command = new CommandEnvelopeCommandData();
            public string resultJson = "";
        }

        private readonly ConsoleHttpServiceDependencies _dependencies;

        public CommandEndpointHandler(ConsoleHttpServiceDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task Handle(HttpListenerContext context)
        {
            var response = CommandResponseFactory.SystemError(null, "Failed to process command");

            try
            {
                var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
                if (string.IsNullOrWhiteSpace(message))
                {
                    response = CommandResponseFactory.InvalidRequestBody();
                }
                else
                {
                    var request = JsonUtility.FromJson<CommandRequest>(message);
                    response = request == null
                        ? CommandResponseFactory.InvalidRequestBody()
                        : (CommandRouter.Dispatch(request) ?? CommandResponseFactory.SystemError(CommandInvocation.FromRequest(request), "Failed to process command"));
                }
            }
            catch (Exception e)
            {
                response = CommandResponseFactory.SystemError(null, $"Failed to process command: {e.Message}");
            }

            var envelope = CreateCommandEnvelope(response);
            await _dependencies.WriteEnvelopeResponseAsync(context, envelope, "Command");
        }

        private HttpResponseEnvelope CreateCommandEnvelope(CommandResponse response)
        {
            response ??= CommandResponseFactory.SystemError(null, "Failed to process command");
            var dataJson = JsonUtility.ToJson(new CommandEnvelopeData
            {
                command = new CommandEnvelopeCommandData
                {
                    commandNamespace = response.commandNamespace ?? "",
                    action = response.action ?? "",
                    sessionId = response.sessionId ?? ""
                },
                resultJson = response.resultJson ?? ""
            });
            return _dependencies.EnvelopeFactory.CreateEnvelope(
                response.ok,
                "command",
                response.type ?? (response.ok ? "ok" : "system_error"),
                response.summary ?? "",
                response.sessionId ?? "",
                dataJson);
        }
    }
}
