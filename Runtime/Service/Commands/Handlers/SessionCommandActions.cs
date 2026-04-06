using System;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class SessionCommandActions
    {
        [Serializable]
        private sealed class SessionListResult
        {
            public SessionStateInfo[] sessions = Array.Empty<SessionStateInfo>();
        }

        [Serializable]
        private sealed class SessionInspectResult
        {
            public bool exists;
            public bool hasCompiler;
            public bool hasExecutor;
        }

        [Serializable]
        private sealed class SessionResetResult
        {
            public bool existed;
            public bool hasCompiler;
            public bool hasExecutor;
            public bool hasCompilerAfter;
            public bool hasExecutorAfter;
        }

        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(SessionCommandActions));
        }

        [CommandAction("session", "list", summary: "List active REPL sessions")]
        private static CommandResponse ListSessionsCommand()
        {
            var sessions = ConsoleHttpService.ListSessions();
            var payload = new SessionListResult
            {
                sessions = sessions.ToArray()
            };

            return CommandResponseFactory.Ok($"Listed {payload.sessions.Length} session(s)", JsonUtility.ToJson(payload));
        }

        [CommandAction("session", "inspect", summary: "Inspect a session's state")]
        private static CommandResponse InspectSessionCommand(CommandInvocation invocation)
        {
            var sessionId = invocation?.sessionId ?? "";
            if (string.IsNullOrEmpty(sessionId))
            {
                return CommandResponseFactory.ValidationError("sessionId is required for session/inspect");
            }

            var hasCompiler = ConsoleHttpService.HasCompilerForSession(sessionId);
            var hasExecutor = ConsoleHttpService.HasExecutorForSession(sessionId);
            var payload = new SessionInspectResult
            {
                exists = hasCompiler || hasExecutor,
                hasCompiler = hasCompiler,
                hasExecutor = hasExecutor
            };

            return CommandResponseFactory.Ok(
                payload.exists
                    ? $"Session '{sessionId}' is active"
                    : $"Session '{sessionId}' was not found",
                JsonUtility.ToJson(payload));
        }

        [CommandAction("session", "reset", summary: "Reset a session's compiler and executor")]
        private static CommandResponse ResetSessionCommand(CommandInvocation invocation)
        {
            var sessionId = invocation?.sessionId ?? "";
            if (string.IsNullOrEmpty(sessionId))
            {
                return CommandResponseFactory.ValidationError("sessionId is required for session/reset");
            }

            var hadCompiler = ConsoleHttpService.HasCompilerForSession(sessionId);
            var hadExecutor = ConsoleHttpService.HasExecutorForSession(sessionId);
            var existed = hadCompiler || hadExecutor;

            ConsoleHttpService.ResetSessionState(sessionId);

            var payload = new SessionResetResult
            {
                existed = existed,
                hasCompiler = hadCompiler,
                hasExecutor = hadExecutor,
                hasCompilerAfter = ConsoleHttpService.HasCompilerForSession(sessionId),
                hasExecutorAfter = ConsoleHttpService.HasExecutorForSession(sessionId)
            };

            return CommandResponseFactory.Ok(
                existed
                    ? $"Session '{sessionId}' has been reset"
                    : $"Session '{sessionId}' was not found",
                JsonUtility.ToJson(payload));
        }
    }
}
