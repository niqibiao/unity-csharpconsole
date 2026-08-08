using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Handlers;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal sealed class CommandRouter
    {
        private readonly static object s_Lock = new object();
        private const int MAX_DISCOVERY_STABILITY_ATTEMPTS = 8;

        private static CommandRouter s_Instance;
        private static int s_ConfigVersion = -1;
        private static long s_AssemblyEpoch;
        private static long s_PublishedAssemblyEpoch = -1;

        private readonly CommandRegistry m_Registry = new CommandRegistry();
        private readonly CommandDispatcher m_Dispatcher;

        static CommandRouter()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private CommandRouter(Func<Func<CommandResponse>, CommandResponse> mainThreadRunner)
        {
            m_Dispatcher = new CommandDispatcher(mainThreadRunner);
        }

        internal static CommandResponse Dispatch(CommandRequest request)
        {
            return GetOrCreate().DispatchInternal(request ?? new CommandRequest());
        }

        internal static CommandDescriptor[] ListDescriptors()
        {
            return GetOrCreate().m_Registry.ListDescriptors();
        }

        internal static CommandRegistrySnapshot GetRegistrySnapshot()
        {
            return GetOrCreate().m_Registry.GetSnapshot();
        }

        internal static void ConfigureDiscovery(CommandDiscoveryOptions options, ICommandAssemblyFilter assemblyFilter = null)
        {
            lock (s_Lock)
            {
                CommandDiscoveryOptions.Configure(options, assemblyFilter);
                s_ConfigVersion = -1;
            }
        }

        internal void RegisterAttributedHandlers(Type ownerType)
        {
            if (ownerType == null)
            {
                throw new ArgumentNullException(nameof(ownerType));
            }

            RegisterAttributedHandlersFromType(ownerType, RegistryPartition.Builtin);
        }

        private static CommandRouter GetOrCreate()
        {
            lock (s_Lock)
            {
                var configVersion = CommandDiscoveryOptions.GetVersion();
                var assemblyEpoch = ReadAssemblyEpoch();
                if (s_Instance != null && s_ConfigVersion == configVersion)
                {
                    if (s_PublishedAssemblyEpoch == assemblyEpoch)
                    {
                        return s_Instance;
                    }
                }

                try
                {
                    for (var attempt = 0;
                         attempt < MAX_DISCOVERY_STABILITY_ATTEMPTS;
                         attempt++)
                    {
                        var startingConfigVersion =
                            CommandDiscoveryOptions.GetVersion();
                        var startingAssemblyEpoch = ReadAssemblyEpoch();
                        var candidate = BuildCandidate();

                        // Materialize normalization, references, and fingerprints before
                        // the candidate can become routable.
                        candidate.m_Registry.GetSnapshot();

                        var endingConfigVersion =
                            CommandDiscoveryOptions.GetVersion();
                        var endingAssemblyEpoch = ReadAssemblyEpoch();
                        if (startingConfigVersion != endingConfigVersion
                            || startingAssemblyEpoch != endingAssemblyEpoch)
                        {
                            continue;
                        }

                        s_Instance = candidate;
                        s_ConfigVersion = endingConfigVersion;
                        s_PublishedAssemblyEpoch = endingAssemblyEpoch;

                        // AssemblyLoad does not take s_Lock. Re-read after publication
                        // so this point is the linearization boundary: a change before
                        // it retries, while a change after it dirties the next request.
                        if (CommandDiscoveryOptions.GetVersion()
                                == endingConfigVersion
                            && ReadAssemblyEpoch() == endingAssemblyEpoch)
                        {
                            return s_Instance;
                        }

                        s_Instance = null;
                        s_ConfigVersion = -1;
                        s_PublishedAssemblyEpoch = -1;
                    }

                    throw new InvalidOperationException(
                        "Command discovery did not reach a stable "
                        + "assembly/configuration epoch after "
                        + $"{MAX_DISCOVERY_STABILITY_ATTEMPTS} complete attempts");
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[CSharpConsole] Failed to build the command registry: {e}");
                    throw;
                }
            }
        }

        private static CommandRouter BuildCandidate()
        {
            var router = new CommandRouter(BuildMainThreadRunner());
            RegisterBuiltinHandlers(router);
            router.RegisterAttributedHandlersFromLoadedAssemblies();
            return router;
        }

        private static void RegisterBuiltinHandlers(CommandRouter router)
        {
            SessionCommandActions.Register(router);
            EditorCommandActions.Register(router);
            ProjectCommandActions.Register(router);
            CommandCatalogCommandActions.Register(router);
            GameObjectCommandActions.Register(router);
            ComponentCommandActions.Register(router);
            SceneCommandActions.Register(router);
            TransformCommandActions.Register(router);
            PrefabCommandActions.Register(router);
            MaterialCommandActions.Register(router);
            ScreenshotCommandActions.Register(router);
            ProfilerCommandActions.Register(router);
            AssetCommandActions.Register(router);
        }

        private void RegisterAttributedHandlersFromLoadedAssemblies()
        {
            var discoveryOptions = CommandDiscoveryOptions.GetCurrent();
            var assemblyFilter = CommandDiscoveryOptions.GetAssemblyFilter();
            var discoveredAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var candidates = new List<Assembly>(discoveredAssemblies.Length);
            foreach (var assembly in discoveredAssemblies)
            {
                if (ShouldScanAssembly(
                        assembly,
                        discoveryOptions,
                        assemblyFilter))
                {
                    candidates.Add(assembly);
                }
            }

            var assemblies = candidates.ToArray();
            Array.Sort(assemblies, CompareAssemblies);
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    throw new InvalidOperationException(
                        $"Failed to load all types from custom command assembly "
                        + $"'{assembly.FullName}'. Partial discovery is not allowed.",
                        e);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"Failed to inspect custom command assembly '{assembly.FullName}'.",
                        e);
                }

                if (types == null)
                {
                    throw new InvalidOperationException(
                        $"Custom command assembly '{assembly.FullName}' returned no type set.");
                }

                Array.Sort(types, CompareTypes);
                foreach (var type in types)
                {
                    if (type == null)
                    {
                        throw new InvalidOperationException(
                            $"Custom command assembly '{assembly.FullName}' returned a "
                            + "partial type set.");
                    }

                    if (!type.IsClass)
                    {
                        continue;
                    }

                    RegisterAttributedHandlersFromType(type, RegistryPartition.Custom);
                }
            }
        }

        private void RegisterAttributedHandlersFromType(
            Type ownerType,
            RegistryPartition registryPartition)
        {
            var methods = ownerType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Array.Sort(methods, CompareMethods);
            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<CommandActionAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var binding = CommandHandlerBindingFactory.Create(ownerType, method, attribute);
                m_Registry.Register(
                    BuildDescriptor(ownerType, method, attribute, binding, registryPartition),
                    binding.invoker);
            }
        }

        private CommandResponse DispatchInternal(CommandRequest request)
        {
            var invocation = CommandInvocation.FromRequest(request);
            return m_Dispatcher.Dispatch(m_Registry, invocation);
        }

        private static CommandDescriptor BuildDescriptor(
            Type ownerType,
            MethodInfo method,
            CommandActionAttribute attribute,
            CommandHandlerBinding binding,
            RegistryPartition registryPartition)
        {
            binding ??= new CommandHandlerBinding();
            return new CommandDescriptor
            {
                id = BuildId(attribute.commandNamespace, attribute.action),
                commandNamespace = attribute.commandNamespace ?? "",
                action = attribute.action ?? "",
                summary = attribute.summary ?? "",
                editorOnly = attribute.editorOnly,
                runOnMainThread = attribute.runOnMainThread,
                declaringType = ownerType?.FullName ?? "",
                methodName = method?.Name ?? "",
                partition = RegistryPartitionProtocol.ToWireName(registryPartition),
                requiresSessionId = attribute.requiresSessionId,
                arguments = binding.arguments ?? Array.Empty<CommandArgumentDescriptor>(),
                result = binding.result ?? new CommandValueSchema(),
                rules = binding.rules ?? Array.Empty<CommandContractRule>()
            };
        }

        private static bool ShouldScanAssembly(Assembly assembly, CommandDiscoveryOptions discoveryOptions, ICommandAssemblyFilter assemblyFilter)
        {
            if (assembly == null)
            {
                return false;
            }

            if (assembly.IsDynamic)
            {
                return false;
            }

            var assemblyName = GetAssemblySimpleName(assembly);
            if (assemblyName == "Zh1Zh1.CSharpConsole.Runtime")
            {
                return false;
            }

            if (assemblyName.EndsWith(".Tests", StringComparison.Ordinal)
                || assemblyName.EndsWith(".Test", StringComparison.Ordinal)
                || (assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
                    && !assemblyName.StartsWith("UnityEditor.", StringComparison.Ordinal))
                || assemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || assemblyName.StartsWith("System.", StringComparison.Ordinal)
                || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal)
                || assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
                || assemblyName.StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (assemblyFilter != null)
            {
                try
                {
                    return assemblyFilter.ShouldScan(assembly, discoveryOptions);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"Command assembly filter failed for '{assembly.FullName}'.",
                        e);
                }
            }

            if (!discoveryOptions.includeEditorAssemblies
                && IsLikelyEditorAssemblyName(assemblyName))
            {
                return false;
            }

            var prefixes = discoveryOptions.assemblyNamePrefixes ?? Array.Empty<string>();
            if (prefixes.Length > 0)
            {
                var matchedPrefix = false;
                foreach (var prefix in prefixes)
                {
                    if (!string.IsNullOrWhiteSpace(prefix) && assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        matchedPrefix = true;
                        break;
                    }
                }

                if (!matchedPrefix)
                {
                    return false;
                }
            }

            if (!discoveryOptions.scanReferencingAssembliesOnly)
            {
                return true;
            }

            return ReferencesRuntimeAssembly(assembly);
        }

        private static bool ReferencesRuntimeAssembly(Assembly assembly)
        {
            try
            {
                var references = assembly.GetReferencedAssemblies();
                foreach (var reference in references)
                {
                    if (string.Equals(reference?.Name, "Zh1Zh1.CSharpConsole.Runtime", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool IsLikelyEditorAssemblyName(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
            {
                return false;
            }

            if (assemblyName.StartsWith("UnityEditor.", StringComparison.Ordinal)
                || string.Equals(assemblyName, "UnityEditor", StringComparison.Ordinal))
            {
                return true;
            }

            if (assemblyName.EndsWith(".Editor", StringComparison.Ordinal)
                || assemblyName.EndsWith(".EditorTests", StringComparison.Ordinal)
                || assemblyName.EndsWith(".Editor.Test", StringComparison.Ordinal)
                || assemblyName.EndsWith(".Editor.Tests", StringComparison.Ordinal)
                || string.Equals(assemblyName, "Editor", StringComparison.Ordinal)
                || assemblyName.StartsWith("Editor.", StringComparison.Ordinal)
                || assemblyName.IndexOf(".Editor.", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            return false;
        }

        private static string BuildId(string commandNamespace, string action)
        {
            return $"{commandNamespace ?? ""}/{action ?? ""}";
        }

        private static int CompareAssemblies(Assembly left, Assembly right)
        {
            var comparison = string.CompareOrdinal(
                left?.GetName().Name ?? "",
                right?.GetName().Name ?? "");
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(left?.FullName ?? "", right?.FullName ?? "");
            if (comparison != 0)
            {
                return comparison;
            }

            return string.CompareOrdinal(
                GetModuleVersionId(left),
                GetModuleVersionId(right));
        }

        private static int CompareTypes(Type left, Type right)
        {
            var comparison = string.CompareOrdinal(
                GetStableTypeName(left),
                GetStableTypeName(right));
            if (comparison != 0)
            {
                return comparison;
            }

            return string.CompareOrdinal(
                left?.Assembly?.GetName().Name ?? "",
                right?.Assembly?.GetName().Name ?? "");
        }

        private static int CompareMethods(MethodInfo left, MethodInfo right)
        {
            var comparison = string.CompareOrdinal(left?.Name ?? "", right?.Name ?? "");
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareMethodGenericArity(left, right);
            if (comparison != 0)
            {
                return comparison;
            }

            var leftParameters = left?.GetParameters() ?? Array.Empty<ParameterInfo>();
            var rightParameters = right?.GetParameters() ?? Array.Empty<ParameterInfo>();
            comparison = leftParameters.Length.CompareTo(rightParameters.Length);
            if (comparison != 0)
            {
                return comparison;
            }

            for (var index = 0; index < leftParameters.Length; index++)
            {
                comparison = string.CompareOrdinal(
                    GetStableTypeName(leftParameters[index]?.ParameterType),
                    GetStableTypeName(rightParameters[index]?.ParameterType));
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = string.CompareOrdinal(
                    leftParameters[index]?.Name ?? "",
                    rightParameters[index]?.Name ?? "");
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = (leftParameters[index]?.IsOut ?? false).CompareTo(
                    rightParameters[index]?.IsOut ?? false);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.CompareOrdinal(
                GetStableTypeName(left?.ReturnType),
                GetStableTypeName(right?.ReturnType));
        }

        private static int CompareMethodGenericArity(MethodInfo left, MethodInfo right)
        {
            var leftArity = left?.IsGenericMethod == true
                ? left.GetGenericArguments().Length
                : 0;
            var rightArity = right?.IsGenericMethod == true
                ? right.GetGenericArguments().Length
                : 0;
            return leftArity.CompareTo(rightArity);
        }

        private static string GetStableTypeName(Type type)
        {
            return type?.FullName ?? type?.Name ?? "";
        }

        private static string GetModuleVersionId(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
            {
                return "";
            }

            try
            {
                return assembly.ManifestModule.ModuleVersionId.ToString("N");
            }
            catch
            {
                return "";
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs eventArgs)
        {
            if (CanAffectCustomDiscovery(eventArgs?.LoadedAssembly))
            {
                Interlocked.Increment(ref s_AssemblyEpoch);
            }
        }

        private static long ReadAssemblyEpoch()
        {
            return Interlocked.Read(ref s_AssemblyEpoch);
        }

        private static bool CanAffectCustomDiscovery(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
            {
                return false;
            }

            var assemblyName = GetAssemblySimpleName(assembly);
            if (assemblyName == "Zh1Zh1.CSharpConsole.Runtime"
                || assemblyName.EndsWith(".Tests", StringComparison.Ordinal)
                || assemblyName.EndsWith(".Test", StringComparison.Ordinal)
                || (assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
                    && !assemblyName.StartsWith(
                        "UnityEditor.",
                        StringComparison.Ordinal))
                || assemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || assemblyName.StartsWith("System.", StringComparison.Ordinal)
                || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal)
                || assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
                || assemblyName.StartsWith(
                    "nunit.",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ReferencesRuntimeAssembly(assembly);
        }

        private static string GetAssemblySimpleName(Assembly assembly)
        {
            try
            {
                return assembly?.GetName().Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static Func<Func<CommandResponse>, CommandResponse> BuildMainThreadRunner()
        {
            return work => MainThreadRequestRunner.RunOnMainThread(work);
        }
    }
}
