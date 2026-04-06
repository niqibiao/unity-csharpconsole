using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    public sealed class CommandDiscoveryOptions
    {
        private static readonly object s_Lock = new();
        private static CommandDiscoveryOptions s_Current = new();
        private static ICommandAssemblyFilter s_AssemblyFilter;
        private static int s_Version;

        public string[] assemblyNamePrefixes = Array.Empty<string>();
        public bool scanReferencingAssembliesOnly = true;
        public bool includeEditorAssemblies = true;

        public static void Configure(CommandDiscoveryOptions options, ICommandAssemblyFilter assemblyFilter = null)
        {
            lock (s_Lock)
            {
                s_Current = options ?? new CommandDiscoveryOptions();
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
                return s_Current ?? new CommandDiscoveryOptions();
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
    }
}
