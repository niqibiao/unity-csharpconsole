using System;
using System.Reflection;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Handlers;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal sealed class CommandRouter
    {
        private readonly static object s_Lock = new();
        private static CommandRouter s_Instance;
        private static int s_ConfigVersion = -1;
        private static int s_DiscoveryFailedVersion = -1;
        private const double DISCOVERY_RETRY_COOLDOWN_SECONDS = 10.0;
        private static double s_DiscoveryFailedTimestamp = -1.0;

        private readonly CommandRegistry m_Registry = new();
        private readonly CommandDispatcher m_Dispatcher;

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

        internal static void ConfigureDiscovery(CommandDiscoveryOptions options, ICommandAssemblyFilter assemblyFilter = null)
        {
            lock (s_Lock)
            {
                CommandDiscoveryOptions.Configure(options, assemblyFilter);
                s_Instance = null;
                s_ConfigVersion = -1;
                s_DiscoveryFailedVersion = -1;
                s_DiscoveryFailedTimestamp = -1.0;
            }
        }

        internal void RegisterAttributedHandlers(Type ownerType)
        {
            if (ownerType == null)
            {
                throw new ArgumentNullException(nameof(ownerType));
            }

            RegisterAttributedHandlersFromType(ownerType);
        }

        private static CommandRouter GetOrCreate()
        {
            lock (s_Lock)
            {
                var configVersion = CommandDiscoveryOptions.GetVersion();
                if (s_Instance != null && s_ConfigVersion == configVersion)
                {
                    // If discovery previously failed and cooldown has elapsed, discard the
                    // cached instance and fall through to rebuild from scratch.  We cannot
                    // retry on the existing instance because CommandRegistry throws on
                    // duplicate registrations for commands that were already registered
                    // before the original failure.
                    if (s_DiscoveryFailedVersion == configVersion && ShouldRetryDiscovery())
                    {
                        s_Instance = null;
                    }
                    else
                    {
                        return s_Instance;
                    }
                }

                var router = new CommandRouter(BuildMainThreadRunner());
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

                if (s_DiscoveryFailedVersion != configVersion || ShouldRetryDiscovery())
                {
                    try
                    {
                        router.RegisterAttributedHandlersFromLoadedAssemblies();
                        s_DiscoveryFailedVersion = -1;
                        s_DiscoveryFailedTimestamp = -1.0;
                    }
                    catch (Exception e)
                    {
                        s_DiscoveryFailedVersion = configVersion;
                        s_DiscoveryFailedTimestamp = ServiceTimestamp.Now();
                        Debug.LogError($"[CSharpConsole] Failed to auto-discover command actions: {e}");
                    }
                }

                s_Instance = router;
                s_ConfigVersion = configVersion;
                return s_Instance;
            }
        }

        private void RegisterAttributedHandlersFromLoadedAssemblies()
        {
            var discoveryOptions = CommandDiscoveryOptions.GetCurrent();
            var assemblyFilter = CommandDiscoveryOptions.GetAssemblyFilter();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (!ShouldScanAssembly(assembly, discoveryOptions, assemblyFilter))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || !type.IsClass)
                    {
                        continue;
                    }

                    RegisterAttributedHandlersFromType(type);
                }
            }
        }

        private void RegisterAttributedHandlersFromType(Type ownerType)
        {
            var methods = ownerType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<CommandActionAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var (invoker, arguments) = CommandHandlerBindingFactory.Create(ownerType, method, attribute);
                m_Registry.Register(BuildDescriptor(ownerType, method, attribute, arguments), invoker);
            }
        }

        private CommandResponse DispatchInternal(CommandRequest request)
        {
            var invocation = CommandInvocation.FromRequest(request);
            return m_Dispatcher.Dispatch(m_Registry, invocation);
        }

        private static CommandDescriptor BuildDescriptor(Type ownerType, MethodInfo method, CommandActionAttribute attribute, CommandArgumentDescriptor[] arguments)
        {
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
                arguments = arguments ?? Array.Empty<CommandArgumentDescriptor>()
            };
        }

        private static bool ShouldScanAssembly(Assembly assembly, CommandDiscoveryOptions discoveryOptions, ICommandAssemblyFilter assemblyFilter)
        {
            if (assembly == null)
            {
                return false;
            }

            var assemblyName = assembly.GetName().Name ?? "";
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
                    Debug.LogWarning($"[CSharpConsole] Command assembly filter failed for {assembly.FullName}: {e.Message}");
                    return false;
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

        private static Func<Func<CommandResponse>, CommandResponse> BuildMainThreadRunner()
        {
            return work => MainThreadRequestRunner.RunOnMainThread(work);
        }

        private static bool ShouldRetryDiscovery()
        {
            if (s_DiscoveryFailedTimestamp < 0.0)
            {
                return true;
            }

            return (ServiceTimestamp.Now() - s_DiscoveryFailedTimestamp) >= DISCOVERY_RETRY_COOLDOWN_SECONDS;
        }

    }
}
