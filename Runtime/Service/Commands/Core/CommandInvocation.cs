using System;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    internal sealed class CommandInvocation
    {
        public string source = "legacy-http";
        public string requestedCapability = "command";
        public string commandNamespace = "";
        public string action = "";
        public string sessionId = "";
        public string argsJson = "{}";

        public static CommandInvocation FromRequest(CommandRequest request)
        {
            request ??= new CommandRequest();

            var invocation = new CommandInvocation();
            ApplyPayload(invocation, request.invocation);

            invocation.commandNamespace ??= "";
            invocation.action ??= "";
            invocation.sessionId ??= "";
            invocation.argsJson = string.IsNullOrWhiteSpace(invocation.argsJson) ? "{}" : invocation.argsJson;
            return invocation;
        }

        private static void ApplyPayload(CommandInvocation invocation, CommandRequest.InvocationPayload payload)
        {
            if (invocation == null || payload == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(payload.source))
            {
                invocation.source = payload.source;
            }

            if (!string.IsNullOrWhiteSpace(payload.requestedCapability))
            {
                invocation.requestedCapability = payload.requestedCapability;
            }

            if (!string.IsNullOrWhiteSpace(payload.sessionId))
            {
                invocation.sessionId = payload.sessionId;
            }

            if (!string.IsNullOrWhiteSpace(payload.argsJson))
            {
                invocation.argsJson = payload.argsJson;
            }

            if (payload.command == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(payload.command.commandNamespace))
            {
                invocation.commandNamespace = payload.command.commandNamespace;
            }

            if (!string.IsNullOrWhiteSpace(payload.command.action))
            {
                invocation.action = payload.command.action;
            }
        }
    }
}
