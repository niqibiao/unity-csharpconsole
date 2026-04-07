using System;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class CommandCatalogCommandActions
    {
        [Serializable]
        private sealed class CommandCatalogResult
        {
            public CommandDescriptor[] commands = Array.Empty<CommandDescriptor>();
        }

        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(CommandCatalogCommandActions));
        }

        [CommandAction("command", "list", summary: "List registered commands")]
        private static CommandResponse ListCommands()
        {
            var result = new CommandCatalogResult
            {
                commands = CommandRouter.ListDescriptors()
            };

            return CommandResponseFactory.Ok($"Listed {result.commands.Length} command(s)", JsonUtility.ToJson(result));
        }
    }
}
