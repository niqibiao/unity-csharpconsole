using System;
using System.Collections.Generic;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal sealed class CommandRegistry
    {
        internal sealed class RouteEntry
        {
            public Func<CommandInvocation, CommandResponse> handler;
            public CommandDescriptor descriptor;
        }

        private readonly Dictionary<string, RouteEntry> _routes = new(StringComparer.Ordinal);
        private readonly List<CommandDescriptor> _descriptors = new();
        private CommandDescriptor[] _sortedSnapshot;

        public void Register(CommandDescriptor descriptor, Func<CommandInvocation, CommandResponse> handler)
        {
            descriptor ??= new CommandDescriptor();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var route = new RouteEntry
            {
                handler = handler,
                descriptor = descriptor
            };

            RegisterRoute(descriptor.commandNamespace, descriptor.action, route);
            _descriptors.Add(descriptor);
        }

        public bool TryGet(string commandNamespace, string action, out RouteEntry route)
        {
            return _routes.TryGetValue(BuildKey(commandNamespace, action), out route);
        }

        public CommandDescriptor[] ListDescriptors()
        {
            if (_sortedSnapshot != null)
            {
                return _sortedSnapshot;
            }

            var snapshot = _descriptors.ToArray();
            Array.Sort(snapshot, (left, right) =>
            {
                var nsCompare = string.CompareOrdinal(left?.commandNamespace, right?.commandNamespace);
                return nsCompare != 0 ? nsCompare : string.CompareOrdinal(left?.action, right?.action);
            });
            _sortedSnapshot = snapshot;
            return _sortedSnapshot;
        }

        private void RegisterRoute(string commandNamespace, string action, RouteEntry route)
        {
            var key = BuildKey(commandNamespace, action);
            if (_routes.ContainsKey(key))
            {
                throw new InvalidOperationException($"Duplicate command registration: {commandNamespace}/{action}");
            }

            _routes[key] = route;
        }

        private static string BuildKey(string commandNamespace, string action)
        {
            return $"{commandNamespace ?? ""}\n{action ?? ""}";
        }
    }
}
