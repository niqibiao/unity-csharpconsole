using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    public static class CommandResponseFactory
    {
        public static CommandResponse Ok(string summary)
        {
            return Ok(summary, "{}");
        }

        public static CommandResponse Ok<T>(string summary, T result)
        {
            return Ok(summary, JsonUtility.ToJson(result));
        }

        public static CommandResponse Ok(string summary, string resultJson)
        {
            return new CommandResponse
            {
                ok = true,
                type = "ok",
                summary = summary,
                resultJson = resultJson ?? "",
                metadataJson = "{}"
            };
        }

        public static CommandResponse ValidationError(string summary)
        {
            return new CommandResponse
            {
                ok = false,
                type = "validation_error",
                summary = summary,
                metadataJson = "{}"
            };
        }

        internal static CommandResponse ValidationError(CommandInvocation invocation, string summary)
        {
            var response = ValidationError(summary);
            ApplyInvocation(response, invocation);
            return response;
        }

        internal static CommandResponse InvalidRequestBody()
        {
            return new CommandResponse
            {
                ok = false,
                type = "validation_error",
                summary = "Unknown command: request body is empty or invalid",
                metadataJson = "{}"
            };
        }

        internal static CommandResponse SystemError(string summary)
        {
            return new CommandResponse
            {
                ok = false,
                type = "system_error",
                summary = summary,
                metadataJson = "{}"
            };
        }

        internal static CommandResponse SystemError(CommandInvocation invocation, string summary)
        {
            var response = SystemError(summary);
            ApplyInvocation(response, invocation);
            return response;
        }

        private static void ApplyInvocation(CommandResponse response, CommandInvocation invocation)
        {
            response.commandNamespace = invocation?.commandNamespace ?? "";
            response.action = invocation?.action ?? "";
            response.sessionId = invocation?.sessionId ?? "";
        }
    }
}
