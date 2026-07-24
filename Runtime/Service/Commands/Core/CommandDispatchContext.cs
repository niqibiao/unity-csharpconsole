using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal sealed class CommandDispatchContext
    {
        internal bool IsBatch { get; private set; }
        internal string ProtectedInvocationId { get; private set; } = "";

        internal static CommandDispatchContext Direct(string protectedInvocationId)
        {
            var normalized = Guid.TryParse(
                protectedInvocationId?.Trim(),
                out var parsed)
                ? parsed.ToString("D")
                : "";
            return new CommandDispatchContext
            {
                IsBatch = false,
                ProtectedInvocationId = normalized
            };
        }

        internal static CommandDispatchContext Batch()
        {
            return new CommandDispatchContext
            {
                IsBatch = true,
                ProtectedInvocationId = ""
            };
        }

        internal static CommandDispatchContext Unprotected()
        {
            return new CommandDispatchContext();
        }
    }
}
