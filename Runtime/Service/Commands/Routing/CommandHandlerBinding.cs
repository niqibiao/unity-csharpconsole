using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal static class CommandHandlerBindingFactory
    {
        private readonly static Type s_BoolStringTupleType = typeof(ValueTuple<bool, string>);

        internal static (Func<CommandInvocation, CommandResponse> invoker, CommandArgumentDescriptor[] arguments) Create(Type ownerType, MethodInfo method, CommandActionAttribute attribute)
        {
            ValidateHandlerSignature(ownerType, method, attribute);

            var parameters = method.GetParameters();
            var boundParameters = CollectBoundParameters(ownerType, method, parameters);
            var returnType = method.ReturnType;

            return (
                invocation => Invoke(method, parameters, returnType, invocation),
                BuildArgumentDescriptors(boundParameters)
            );
        }

        private static CommandResponse Invoke(MethodInfo method, ParameterInfo[] parameters, Type returnType, CommandInvocation invocation)
        {
            if (!CommandArgumentBinder.TryBind(invocation, parameters, out var arguments, out var errorResponse))
            {
                return errorResponse;
            }

            try
            {
                var result = method.Invoke(null, arguments);
                if (result is CommandResponse response)
                {
                    return response;
                }

                if (returnType == s_BoolStringTupleType)
                {
                    var tuple = ((bool ok, string message))result;
                    return tuple.ok
                        ? CommandResponseFactory.Ok(tuple.message)
                        : CommandResponseFactory.ValidationError(tuple.message);
                }

                throw new InvalidOperationException($"Unexpected return type: {returnType}");
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private static bool IsSupportedReturnType(Type returnType)
        {
            return returnType == typeof(CommandResponse) || returnType == s_BoolStringTupleType;
        }

        private static void ValidateHandlerSignature(Type ownerType, MethodInfo method, CommandActionAttribute attribute)
        {
            if (!IsSupportedReturnType(method.ReturnType))
            {
                throw new InvalidOperationException($"Command action handler must return {nameof(CommandResponse)} or (bool, string): {ownerType.FullName}.{method.Name}");
            }

            if (string.IsNullOrEmpty(attribute.commandNamespace) || string.IsNullOrEmpty(attribute.action))
            {
                throw new InvalidOperationException($"Command action attribute requires non-empty namespace/action: {ownerType.FullName}.{method.Name}");
            }
        }

        private static ParameterInfo[] CollectBoundParameters(Type ownerType, MethodInfo method, ParameterInfo[] parameters)
        {
            var boundParameters = new List<ParameterInfo>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in parameters)
            {
                if (parameter == null)
                {
                    continue;
                }

                if (parameter.IsOut || parameter.ParameterType.IsByRef)
                {
                    throw new InvalidOperationException($"Command action handler cannot use ref/out parameters: {ownerType.FullName}.{method.Name}");
                }

                if (CommandArgumentBinder.IsInjectedParameterType(parameter.ParameterType))
                {
                    continue;
                }

                if (!CommandArgumentBinder.IsSupportedBoundParameterType(parameter.ParameterType))
                {
                    throw new InvalidOperationException($"Unsupported command action parameter type '{parameter.ParameterType.FullName}' on {ownerType.FullName}.{method.Name}");
                }

                if (!usedNames.Add(parameter.Name ?? string.Empty))
                {
                    throw new InvalidOperationException($"Duplicate command action parameter name '{parameter.Name}' on {ownerType.FullName}.{method.Name}");
                }

                boundParameters.Add(parameter);
            }

            return boundParameters.ToArray();
        }

        private static CommandArgumentDescriptor[] BuildArgumentDescriptors(ParameterInfo[] boundParameters)
        {
            if (boundParameters == null || boundParameters.Length == 0)
            {
                return Array.Empty<CommandArgumentDescriptor>();
            }

            var descriptors = new CommandArgumentDescriptor[boundParameters.Length];
            for (var i = 0; i < boundParameters.Length; i++)
            {
                var parameter = boundParameters[i];
                descriptors[i] = new CommandArgumentDescriptor
                {
                    name = parameter.Name ?? "",
                    typeName = parameter.ParameterType.FullName ?? parameter.ParameterType.Name ?? ""
                };
            }

            return descriptors;
        }
    }
}
