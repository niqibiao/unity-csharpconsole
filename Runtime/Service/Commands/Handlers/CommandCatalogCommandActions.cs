using System;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class CommandCatalogCommandActions
    {
        [Serializable]
        private sealed class CommandListResult
        {
            public RegistryCommandContract[] commands = Array.Empty<RegistryCommandContract>();
        }

        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(CommandCatalogCommandActions));
        }

        [CommandAction(
            "command",
            "list",
            summary: "List registered commands",
            resultType: typeof(CommandListResult))]
        private static CommandResponse ListCommands()
        {
            var snapshot = CommandRouter.GetRegistrySnapshot();
            var count = (snapshot.builtin?.count ?? 0) + (snapshot.custom?.count ?? 0);
            return CommandResponseFactory.Ok(
                $"Listed {count} command(s)",
                CommandRegistryJson.SerializeCommandList(snapshot));
        }

        [CommandAction(
            "command",
            "registry.snapshot",
            runOnMainThread: false,
            summary: "Get command registry snapshot",
            resultType: typeof(CommandRegistrySnapshot))]
        private static CommandResponse GetRegistrySnapshot(
            string ifGeneration = null)
        {
            var snapshot = CommandRouter.GetRegistrySnapshot();
            if (!string.IsNullOrEmpty(ifGeneration)
                && string.Equals(
                    ifGeneration,
                    snapshot.registryGeneration,
                    StringComparison.Ordinal))
            {
                return CommandResponseFactory.Ok(
                    "Registry unchanged",
                    CommandRegistryJson.SerializeSnapshot(new CommandRegistrySnapshot
                    {
                        schemaVersion = snapshot.schemaVersion,
                        registryGeneration = snapshot.registryGeneration,
                        unchanged = true
                    }));
            }

            var returnedCount =
                snapshot.builtin.commands.Length + snapshot.custom.commands.Length;
            return CommandResponseFactory.Ok(
                $"Listed {returnedCount} registry command(s)",
                CommandRegistryJson.SerializeSnapshot(snapshot));
        }
    }
}
