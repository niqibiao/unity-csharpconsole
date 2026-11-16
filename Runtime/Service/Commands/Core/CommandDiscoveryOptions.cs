using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    public sealed class CommandDiscoveryOptions
    {
        private static readonly object s_Lock = new object();
        private static CommandDiscoveryOptions s_Current = new CommandDiscoveryOptions();
        private static ICommandAssemblyFilter s_AssemblyFilter;
        private static int s_Version;

        public string[] assemblyNamePrefixes = Array.Empty<string>();
        public bool scanReferencingAssembliesOnly = true;
        public bool includeEditorAssemblies = true;

        /// <summary>
        /// When not empty, the custom commands are exactly the [CommandAction] methods
        /// of these types, and no assembly is scanned: the prefixes and flags above, and
        /// any assembly filter passed to Configure, are not consulted. Scanning calls
        /// GetTypes() on every candidate assembly, which under IL2CPP creates class
        /// metadata for each type in it that is never released -- a cost a large game
        /// assembly referencing this package pays on the first command. Built-in
        /// commands are registered either way.
        /// </summary>
        public Type[] handlerTypes = Array.Empty<Type>();

        public static void Configure(CommandDiscoveryOptions options, ICommandAssemblyFilter assemblyFilter = null)
        {
            lock (s_Lock)
            {
                s_Current = Clone(options);
                s_AssemblyFilter = assemblyFilter;
                unchecked
                {
                    s_Version++;
                }
            }
        }

        internal static CommandDiscoveryOptions GetCurrent()
        {
            lock (s_Lock)
            {
                return Clone(s_Current);
            }
        }

        internal static ICommandAssemblyFilter GetAssemblyFilter()
        {
            lock (s_Lock)
            {
                return s_AssemblyFilter;
            }
        }

        internal static int GetVersion()
        {
            lock (s_Lock)
            {
                return s_Version;
            }
        }

        private static CommandDiscoveryOptions Clone(CommandDiscoveryOptions options)
        {
            options ??= new CommandDiscoveryOptions();
            return new CommandDiscoveryOptions
            {
                assemblyNamePrefixes =
                    (string[])(options.assemblyNamePrefixes
                        ?? Array.Empty<string>()).Clone(),
                scanReferencingAssembliesOnly =
                    options.scanReferencingAssembliesOnly,
                includeEditorAssemblies = options.includeEditorAssemblies,
                handlerTypes =
                    (Type[])(options.handlerTypes
                        ?? Array.Empty<Type>()).Clone()
            };
        }
    }
}
