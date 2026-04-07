using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal sealed class CommandDispatcher
    {
        private readonly Func<Func<CommandResponse>, CommandResponse> _mainThreadRunner;

        internal CommandDispatcher(Func<Func<CommandResponse>, CommandResponse> mainThreadRunner = null)
        {
            _mainThreadRunner = mainThreadRunner;
        }

        public CommandResponse Dispatch(CommandRegistry registry, CommandInvocation invocation)
        {
            registry ??= new CommandRegistry();
            invocation ??= new CommandInvocation();

            if (!registry.TryGet(invocation.commandNamespace, invocation.action, out var route))
            {
                return CommandResponseFactory.ValidationError(invocation, $"Unknown command: {invocation.commandNamespace}/{invocation.action}");
            }

#if !UNITY_EDITOR
            if (route.descriptor != null && route.descriptor.editorOnly)
            {
                return CommandResponseFactory.ValidationError(invocation, $"Command is only available in the Unity editor: {invocation.commandNamespace}/{invocation.action}");
            }
#endif

            try
            {
                if (route.descriptor != null && route.descriptor.runOnMainThread && _mainThreadRunner != null)
                {
                    return NormalizeResponse(_mainThreadRunner(() => route.handler(invocation)), invocation);
                }

                return NormalizeResponse(route.handler(invocation), invocation);
            }
            catch (Exception e)
            {
                return CommandResponseFactory.SystemError(invocation, $"Failed to process command: {invocation.commandNamespace}/{invocation.action}, {e.Message}");
            }
        }

        private static CommandResponse NormalizeResponse(CommandResponse response, CommandInvocation invocation)
        {
            response ??= new CommandResponse();
            response.type ??= response.ok ? "ok" : "system_error";
            response.summary ??= "";
            response.commandNamespace = string.IsNullOrEmpty(response.commandNamespace)
                ? invocation?.commandNamespace ?? ""
                : response.commandNamespace;
            response.action = string.IsNullOrEmpty(response.action)
                ? invocation?.action ?? ""
                : response.action;
            response.sessionId = string.IsNullOrEmpty(response.sessionId)
                ? invocation?.sessionId ?? ""
                : response.sessionId;
            response.resultJson ??= "";
            return response;
        }
    }
}
