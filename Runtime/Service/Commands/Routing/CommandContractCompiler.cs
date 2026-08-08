using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    internal sealed class CompiledCommandContract
    {
        internal CommandArgumentDescriptor[] arguments = Array.Empty<CommandArgumentDescriptor>();
        internal CommandValueSchema result = new CommandValueSchema();
        internal CommandContractRule[] rules = Array.Empty<CommandContractRule>();
    }

    internal static class CommandContractCompiler
    {
        internal static CompiledCommandContract Compile(
            Type ownerType,
            MethodInfo method,
            CommandActionAttribute action,
            ParameterInfo[] boundParameters)
        {
            ownerType ??= method?.DeclaringType;
            boundParameters ??= Array.Empty<ParameterInfo>();

            var arguments = new CommandArgumentDescriptor[boundParameters.Length];
            for (var index = 0; index < boundParameters.Length; index++)
            {
                arguments[index] = CompileArgument(boundParameters[index]);
            }

            var argumentNames = new HashSet<string>(
                arguments.Select(argument => argument.name),
                StringComparer.OrdinalIgnoreCase);
            var parametersByName = new Dictionary<string, ParameterInfo>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in boundParameters)
            {
                parametersByName.Add(parameter.Name ?? "", parameter);
            }
            var rules = CompileRules(
                ownerType,
                method,
                argumentNames,
                parametersByName);

            return new CompiledCommandContract
            {
                arguments = arguments,
                result = BuildSchema(action?.resultType, isInput: false),
                rules = rules
            };
        }

        private static CommandArgumentDescriptor CompileArgument(ParameterInfo parameter)
        {
            var annotation = parameter.GetCustomAttribute<CommandArgumentAttribute>();
            var hasDefault = parameter.HasDefaultValue;
            var defaultValue = hasDefault ? GetDefaultValue(parameter) : null;
            var allowedValues = CompileAllowedValues(parameter, annotation);
            var hasMinimum = annotation != null && !double.IsNaN(annotation.Minimum);
            var hasMaximum = annotation != null && !double.IsNaN(annotation.Maximum);
            var schema = BuildSchema(parameter.ParameterType, isInput: true);
            if (hasDefault && defaultValue == null)
            {
                schema.nullable = true;
            }

            var descriptor = new CommandArgumentDescriptor
            {
                name = parameter.Name ?? "",
                typeName = parameter.ParameterType.FullName
                    ?? parameter.ParameterType.Name
                    ?? "",
                schema = schema,
                required = !hasDefault,
                hasDefault = hasDefault,
                defaultJson = hasDefault
                    ? CommandContractValueEncoder.Encode(defaultValue)
                    : "",
                nonEmpty = annotation?.NonEmpty ?? false,
                hasMinimum = hasMinimum,
                minimum = hasMinimum ? annotation.Minimum : 0,
                hasMaximum = hasMaximum,
                maximum = hasMaximum ? annotation.Maximum : 0,
                allowedValues = allowedValues,
                allowedValuesIgnoreCase = allowedValues.Length > 0
                    && (annotation?.AllowedValuesIgnoreCase ?? false)
            };

            ValidateArgumentMetadata(parameter, descriptor);
            if (hasDefault
                && !CommandArgumentBinder.TryValidateContractValue(
                    descriptor,
                    defaultValue,
                    out var defaultError))
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has an invalid default: {defaultError}");
            }

            return descriptor;
        }

        private static string[] CompileAllowedValues(
            ParameterInfo parameter,
            CommandArgumentAttribute annotation)
        {
            return CompileAllowedValues(
                $"Command argument '{parameter.Name}'",
                parameter.ParameterType,
                annotation?.AllowedValues,
                annotation?.AllowedValuesIgnoreCase ?? false);
        }

        private static string[] CompileAllowedValues(
            string owner,
            Type valueType,
            string[] declaredValues,
            bool ignoreCase)
        {
            declaredValues ??= Array.Empty<string>();
            if (declaredValues.Length == 0)
            {
                return Array.Empty<string>();
            }

            var actualType = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (ignoreCase
                && actualType != typeof(string)
                && actualType != typeof(char))
            {
                throw new InvalidOperationException(
                    $"{owner} can ignore allowed-value "
                    + "case only for string or char values");
            }

            var canonical = new List<string>(declaredValues.Length);
            foreach (var declaredValue in declaredValues)
            {
                if (declaredValue == null)
                {
                    throw new InvalidOperationException(
                        $"{owner} has a null allowed-value declaration");
                }

                object value;
                string conversionError;
                if (actualType == typeof(string))
                {
                    value = declaredValue;
                }
                else if (actualType == typeof(char))
                {
                    if (declaredValue.Length != 1)
                    {
                        throw new InvalidOperationException(
                            $"{owner} allowed char "
                            + $"'{declaredValue}' must contain exactly one character");
                    }

                    value = declaredValue[0];
                }
                else if (actualType.IsEnum)
                {
                    try
                    {
                        value = Enum.Parse(actualType, declaredValue, false);
                    }
                    catch
                    {
                        throw new InvalidOperationException(
                            $"{owner} has invalid allowed "
                            + $"enum value '{declaredValue}'");
                    }

                    if (!Enum.IsDefined(actualType, value))
                    {
                        throw new InvalidOperationException(
                            $"{owner} has undefined allowed "
                            + $"enum value '{declaredValue}'");
                    }
                }
                else if (!CommandArgumentBinder.TryConvertContractValue(
                             declaredValue,
                             valueType,
                             out value,
                             out conversionError))
                {
                    throw new InvalidOperationException(
                        $"{owner} has invalid allowed value "
                        + $"'{declaredValue}': {conversionError}");
                }

                try
                {
                    canonical.Add(CommandAllowedValueCodec.Encode(value));
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"{owner} has unsupported allowed "
                        + $"value '{declaredValue}'",
                        error);
                }
            }

            if (ignoreCase)
            {
                return canonical
                    .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(value => value, StringComparer.Ordinal).First())
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
            }

            return canonical
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static void ValidateArgumentMetadata(
            ParameterInfo parameter,
            CommandArgumentDescriptor descriptor)
        {
            if (descriptor.hasMinimum
                && (double.IsInfinity(descriptor.minimum) || double.IsNaN(descriptor.minimum)))
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has an invalid minimum");
            }

            if (descriptor.hasMaximum
                && (double.IsInfinity(descriptor.maximum) || double.IsNaN(descriptor.maximum)))
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has an invalid maximum");
            }

            if (descriptor.hasMinimum
                && descriptor.hasMaximum
                && descriptor.minimum > descriptor.maximum)
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has minimum greater than maximum");
            }

            if ((descriptor.hasMinimum || descriptor.hasMaximum)
                && descriptor.schema.kind != "integer"
                && descriptor.schema.kind != "number")
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has a numeric range on a non-numeric type");
            }

            if (descriptor.nonEmpty
                && descriptor.schema.kind != "string"
                && descriptor.schema.kind != "array")
            {
                throw new InvalidOperationException(
                    $"Command argument '{parameter.Name}' has NonEmpty on an unsupported type");
            }
        }

        private static CommandContractRule[] CompileRules(
            Type ownerType,
            MethodInfo method,
            HashSet<string> argumentNames,
            Dictionary<string, ParameterInfo> parametersByName)
        {
            var attributes = method?
                .GetCustomAttributes<CommandRuleAttribute>()
                .ToArray()
                ?? Array.Empty<CommandRuleAttribute>();
            var rules = new List<CommandContractRule>(attributes.Length);
            foreach (var attribute in attributes)
            {
                var arguments = NormalizeNames(attribute.arguments);
                var requires = NormalizeNames(attribute.Requires);
                ValidateRule(ownerType, method, attribute, arguments, requires, argumentNames);
                arguments = CanonicalizeNames(arguments, parametersByName);
                requires = CanonicalizeNames(requires, parametersByName);
                var whenArgument = CanonicalizeName(
                    attribute.WhenArgument,
                    parametersByName);
                rules.Add(new CommandContractRule
                {
                    kind = ToWireRuleKind(attribute.kind),
                    arguments = arguments,
                    whenArgument = whenArgument,
                    whenEqualsJson = CanonicalizeWhenEqualsJson(
                        ownerType,
                        method,
                        attribute,
                        whenArgument,
                        parametersByName),
                    requires = requires
                });
            }

            rules.Sort(CommandContractRuleOrder.Compare);
            return rules.ToArray();
        }

        private static void ValidateRule(
            Type ownerType,
            MethodInfo method,
            CommandRuleAttribute attribute,
            string[] arguments,
            string[] requires,
            HashSet<string> argumentNames)
        {
            var handlerName = $"{ownerType?.FullName ?? ""}.{method?.Name ?? ""}";
            if (attribute.kind != CommandRuleKind.RequiresWhen && arguments.Length < 2)
            {
                throw new InvalidOperationException(
                    $"Command rule '{attribute.kind}' requires at least two arguments: {handlerName}");
            }

            foreach (var name in arguments.Concat(requires))
            {
                if (!argumentNames.Contains(name))
                {
                    throw new InvalidOperationException(
                        $"Command rule '{attribute.kind}' references unknown argument '{name}': {handlerName}");
                }
            }

            if (attribute.kind == CommandRuleKind.RequiresWhen)
            {
                if (arguments.Length != 0)
                {
                    throw new InvalidOperationException(
                        $"Command rule RequiresWhen cannot declare selector arguments: {handlerName}");
                }

                if (string.IsNullOrEmpty(attribute.WhenArgument)
                    || !argumentNames.Contains(attribute.WhenArgument))
                {
                    throw new InvalidOperationException(
                        $"Command rule RequiresWhen has an unknown when argument: {handlerName}");
                }

                if (requires.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Command rule RequiresWhen requires at least one dependent argument: {handlerName}");
                }

                return;
            }

            if (!string.IsNullOrEmpty(attribute.WhenArgument)
                || !string.IsNullOrEmpty(attribute.WhenEqualsJson)
                || requires.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Command rule '{attribute.kind}' cannot declare RequiresWhen fields: {handlerName}");
            }
        }

        private static string[] NormalizeNames(string[] names)
        {
            return (names ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] CanonicalizeNames(
            string[] names,
            Dictionary<string, ParameterInfo> parametersByName)
        {
            var canonical = new string[names?.Length ?? 0];
            for (var index = 0; index < canonical.Length; index++)
            {
                canonical[index] = CanonicalizeName(names[index], parametersByName);
            }

            return canonical;
        }

        private static string CanonicalizeName(
            string name,
            Dictionary<string, ParameterInfo> parametersByName)
        {
            name = (name ?? "").Trim();
            return parametersByName != null
                && parametersByName.TryGetValue(name, out var parameter)
                    ? parameter.Name ?? ""
                    : name;
        }

        private static string CanonicalizeWhenEqualsJson(
            Type ownerType,
            MethodInfo method,
            CommandRuleAttribute attribute,
            string whenArgument,
            Dictionary<string, ParameterInfo> parametersByName)
        {
            var rawJson = attribute?.WhenEqualsJson ?? "";
            if (attribute == null
                || attribute.kind != CommandRuleKind.RequiresWhen
                || string.IsNullOrEmpty(rawJson))
            {
                return "";
            }

            if (!parametersByName.TryGetValue(whenArgument, out var parameter))
            {
                throw new InvalidOperationException(
                    $"Command rule RequiresWhen references unknown argument "
                    + $"'{whenArgument}' on {ownerType?.FullName ?? ""}.{method?.Name ?? ""}");
            }

            if (!CommandArgumentBinder.TryConvertContractValue(
                    rawJson,
                    parameter.ParameterType,
                    out var value,
                    out var conversionError))
            {
                throw new InvalidOperationException(
                    $"Command rule RequiresWhen has invalid WhenEqualsJson for "
                    + $"'{whenArgument}' on {ownerType?.FullName ?? ""}.{method?.Name ?? ""}: "
                    + conversionError);
            }

            try
            {
                return CommandContractValueEncoder.Encode(value);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Command rule RequiresWhen cannot canonicalize WhenEqualsJson for "
                    + $"'{whenArgument}' on {ownerType?.FullName ?? ""}.{method?.Name ?? ""}",
                    error);
            }
        }

        private static string ToWireRuleKind(CommandRuleKind kind)
        {
            switch (kind)
            {
                case CommandRuleKind.ExactlyOneOf:
                    return "exactlyOneOf";
                case CommandRuleKind.AtMostOneOf:
                    return "atMostOneOf";
                case CommandRuleKind.AtLeastOneOf:
                    return "atLeastOneOf";
                case CommandRuleKind.AtLeastOneMutation:
                    return "atLeastOneMutation";
                case CommandRuleKind.RequiresWhen:
                    return "requiresWhen";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private sealed class SchemaBuildContext
        {
            internal bool isInput;
            internal readonly Dictionary<Type, bool> recursiveTypes =
                new Dictionary<Type, bool>();
            internal readonly Dictionary<Type, string> definitionIds =
                new Dictionary<Type, string>();
            internal readonly List<CommandSchemaDefinition> definitions =
                new List<CommandSchemaDefinition>();
        }

        private static CommandValueSchema BuildSchema(Type type, bool isInput)
        {
            var context = new SchemaBuildContext { isInput = isInput };
            var schema = BuildSchema(type, context);
            if (isInput && context.definitions.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Recursive command input schema is not supported: {type?.FullName}");
            }

            schema.definitions = context.definitions
                .OrderBy(definition => definition.id, StringComparer.Ordinal)
                .ToArray();
            return schema;
        }

        private static CommandValueSchema BuildSchema(
            Type type,
            SchemaBuildContext context)
        {
            if (type == null || type == typeof(void))
            {
                return new CommandValueSchema();
            }

            var nullableType = Nullable.GetUnderlyingType(type);
            var actualType = nullableType ?? type;
            var nullable = nullableType != null;

            if (actualType == typeof(string) || actualType == typeof(char))
            {
                return Primitive("string", actualType == typeof(char) ? "char" : "", nullable);
            }

            if (actualType == typeof(bool))
            {
                return Primitive("boolean", "", nullable);
            }

            if (actualType.IsEnum)
            {
                var names = Enum.GetNames(actualType);
                Array.Sort(names, StringComparer.Ordinal);
                var schema = Primitive("enum", "string", nullable);
                schema.enumValues = names;
                return schema;
            }

            if (IsInteger(actualType, out var integerFormat))
            {
                return Primitive("integer", integerFormat, nullable);
            }

            if (IsNumber(actualType, out var numberFormat))
            {
                return Primitive("number", numberFormat, nullable);
            }

            if (actualType.IsArray)
            {
                return new CommandValueSchema
                {
                    kind = "array",
                    nullable = nullable,
                    items = BuildSchema(actualType.GetElementType(), context)
                };
            }

            if (actualType.IsGenericType
                && actualType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return new CommandValueSchema
                {
                    kind = "array",
                    nullable = nullable,
                    items = BuildSchema(actualType.GetGenericArguments()[0], context)
                };
            }

            if (actualType.IsGenericType
                && actualType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                && actualType.GetGenericArguments()[0] == typeof(string))
            {
                if (context.isInput)
                {
                    throw new InvalidOperationException(
                        $"Dictionary command input schema is not supported: {type.FullName}");
                }

                return new CommandValueSchema
                {
                    kind = "map",
                    nullable = nullable,
                    items = BuildSchema(actualType.GetGenericArguments()[1], context)
                };
            }

            if (IsRecursiveSchemaType(actualType, context))
            {
                if (context.definitionIds.TryGetValue(actualType, out var existingId))
                {
                    return Reference(existingId, nullable);
                }

                var definitionId = $"d{context.definitions.Count}";
                context.definitionIds.Add(actualType, definitionId);
                var definition = new CommandSchemaDefinition { id = definitionId };
                context.definitions.Add(definition);
                definition.schema = BuildObjectSchema(actualType, context, nullable: false);
                return Reference(definitionId, nullable);
            }

            return BuildObjectSchema(actualType, context, nullable);
        }

        private static CommandValueSchema BuildObjectSchema(
            Type type,
            SchemaBuildContext context,
            bool nullable)
        {
            var fields = GetSerializableFields(type)
                .OrderBy(GetWireFieldName, StringComparer.Ordinal)
                .ThenBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => CompileField(field, context))
                .ToArray();
            var duplicateName = fields
                .GroupBy(field => field.name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateName != null)
            {
                throw new InvalidOperationException(
                    $"Serializable result type '{type.FullName}' has duplicate wire field "
                    + $"name '{duplicateName.Key}'");
            }

            return new CommandValueSchema
            {
                kind = "object",
                nullable = nullable,
                fields = fields
            };
        }

        private static string GetWireFieldName(FieldInfo field)
        {
            var annotation = field?.GetCustomAttribute<CommandWireFieldAttribute>();
            return string.IsNullOrEmpty(annotation?.name)
                ? field?.Name ?? ""
                : annotation.name;
        }

        private static CommandSchemaField CompileField(
            FieldInfo field,
            SchemaBuildContext context)
        {
            var annotation = field.GetCustomAttribute<CommandFieldAttribute>();
            var wireAnnotation = field.GetCustomAttribute<CommandWireFieldAttribute>();
            if (context.isInput && wireAnnotation != null)
            {
                throw new InvalidOperationException(
                    $"Command schema field '{field.DeclaringType?.FullName}.{field.Name}' "
                    + "cannot override its wire shape in an input contract");
            }

            var allowedValues = CompileAllowedValues(
                $"Command schema field '{field.DeclaringType?.FullName}.{field.Name}'",
                field.FieldType,
                annotation?.AllowedValues,
                annotation?.AllowedValuesIgnoreCase ?? false);
            var hasMinimum = annotation != null && !double.IsNaN(annotation.Minimum);
            var hasMaximum = annotation != null && !double.IsNaN(annotation.Maximum);
            var schema = BuildSchema(
                wireAnnotation?.schemaType ?? field.FieldType,
                context);
            if (annotation?.AllowNull ?? false)
            {
                if (field.FieldType.IsValueType
                    && Nullable.GetUnderlyingType(field.FieldType) == null)
                {
                    throw new InvalidOperationException(
                        $"Command schema field '{field.DeclaringType?.FullName}.{field.Name}' "
                        + "cannot allow null for a non-nullable value type");
                }

                schema.nullable = true;
            }

            var descriptor = new CommandSchemaField
            {
                name = string.IsNullOrEmpty(wireAnnotation?.name)
                    ? field.Name ?? ""
                    : wireAnnotation.name,
                schema = schema,
                required = context.isInput && !(annotation?.Optional ?? false),
                nonEmpty = annotation?.NonEmpty ?? false,
                hasMinimum = hasMinimum,
                minimum = hasMinimum ? annotation.Minimum : 0,
                hasMaximum = hasMaximum,
                maximum = hasMaximum ? annotation.Maximum : 0,
                allowedValues = allowedValues,
                allowedValuesIgnoreCase = allowedValues.Length > 0
                    && (annotation?.AllowedValuesIgnoreCase ?? false)
            };

            ValidateFieldMetadata(field, descriptor);
            return descriptor;
        }

        private static void ValidateFieldMetadata(
            FieldInfo field,
            CommandSchemaField descriptor)
        {
            var owner = $"Command schema field '{field.DeclaringType?.FullName}.{field.Name}'";
            if (descriptor.hasMinimum
                && (double.IsInfinity(descriptor.minimum) || double.IsNaN(descriptor.minimum)))
            {
                throw new InvalidOperationException($"{owner} has an invalid minimum");
            }

            if (descriptor.hasMaximum
                && (double.IsInfinity(descriptor.maximum) || double.IsNaN(descriptor.maximum)))
            {
                throw new InvalidOperationException($"{owner} has an invalid maximum");
            }

            if (descriptor.hasMinimum
                && descriptor.hasMaximum
                && descriptor.minimum > descriptor.maximum)
            {
                throw new InvalidOperationException($"{owner} has minimum greater than maximum");
            }

            if ((descriptor.hasMinimum || descriptor.hasMaximum)
                && descriptor.schema.kind != "integer"
                && descriptor.schema.kind != "number")
            {
                throw new InvalidOperationException(
                    $"{owner} has a numeric range on a non-numeric type");
            }

            if (descriptor.nonEmpty
                && descriptor.schema.kind != "string"
                && descriptor.schema.kind != "array")
            {
                throw new InvalidOperationException(
                    $"{owner} has NonEmpty on an unsupported type");
            }
        }

        private static bool IsRecursiveSchemaType(
            Type type,
            SchemaBuildContext context)
        {
            if (context.recursiveTypes.TryGetValue(type, out var recursive))
            {
                return recursive;
            }

            recursive = GetSerializableFields(type)
                .Select(field => GetObjectSchemaType(field.FieldType))
                .Where(childType => childType != null)
                .Any(childType => CanReachSchemaType(
                    childType,
                    type,
                    new HashSet<Type>()));
            context.recursiveTypes[type] = recursive;
            return recursive;
        }

        private static bool CanReachSchemaType(
            Type current,
            Type target,
            HashSet<Type> visited)
        {
            current = GetObjectSchemaType(current);
            if (current == null)
            {
                return false;
            }

            if (current == target)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                return false;
            }

            return GetSerializableFields(current)
                .Select(field => GetObjectSchemaType(field.FieldType))
                .Where(childType => childType != null)
                .Any(childType => CanReachSchemaType(childType, target, visited));
        }

        private static Type GetObjectSchemaType(Type type)
        {
            if (type == null)
            {
                return null;
            }

            var actualType = Nullable.GetUnderlyingType(type) ?? type;
            while (actualType.IsArray
                   || (actualType.IsGenericType
                       && (actualType.GetGenericTypeDefinition() == typeof(List<>)
                           || (actualType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                               && actualType.GetGenericArguments()[0] == typeof(string)))))
            {
                actualType = actualType.IsArray
                    ? actualType.GetElementType()
                    : actualType.GetGenericTypeDefinition() == typeof(List<>)
                        ? actualType.GetGenericArguments()[0]
                        : actualType.GetGenericArguments()[1];
                actualType = Nullable.GetUnderlyingType(actualType) ?? actualType;
            }

            if (actualType == typeof(string)
                || actualType == typeof(char)
                || actualType == typeof(bool)
                || actualType.IsEnum
                || IsInteger(actualType, out _)
                || IsNumber(actualType, out _))
            {
                return null;
            }

            return actualType;
        }

        private static CommandValueSchema Reference(string id, bool nullable)
        {
            return new CommandValueSchema
            {
                kind = "reference",
                nullable = nullable,
                reference = id ?? ""
            };
        }

        private static FieldInfo[] GetSerializableFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                fields.AddRange(
                    current
                        .GetFields(
                            BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly)
                        .Where(field =>
                            !field.IsStatic
                            && !field.IsLiteral
                            && !field.IsInitOnly
                            && !field.IsNotSerialized
                            && (field.IsPublic
                                || field.IsDefined(typeof(UnityEngine.SerializeField), false)
                                || field.IsDefined(typeof(UnityEngine.SerializeReference), false))));
            }

            var duplicateName = fields
                .GroupBy(field => field.Name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateName != null)
            {
                throw new InvalidOperationException(
                    $"Serializable result type '{type.FullName}' has duplicate field "
                    + $"name '{duplicateName.Key}'");
            }

            return fields.ToArray();
        }

        private static CommandValueSchema Primitive(string kind, string format, bool nullable)
        {
            return new CommandValueSchema
            {
                kind = kind,
                format = format,
                nullable = nullable
            };
        }

        private static bool IsInteger(Type type, out string format)
        {
            if (type == typeof(byte))
            {
                format = "uint8";
                return true;
            }

            if (type == typeof(sbyte))
            {
                format = "int8";
                return true;
            }

            if (type == typeof(short))
            {
                format = "int16";
                return true;
            }

            if (type == typeof(ushort))
            {
                format = "uint16";
                return true;
            }

            if (type == typeof(int))
            {
                format = "int32";
                return true;
            }

            if (type == typeof(uint))
            {
                format = "uint32";
                return true;
            }

            if (type == typeof(long))
            {
                format = "int64";
                return true;
            }

            if (type == typeof(ulong))
            {
                format = "uint64";
                return true;
            }

            format = "";
            return false;
        }

        private static bool IsNumber(Type type, out string format)
        {
            if (type == typeof(float))
            {
                format = "float32";
                return true;
            }

            if (type == typeof(double))
            {
                format = "float64";
                return true;
            }

            if (type == typeof(decimal))
            {
                format = "decimal";
                return true;
            }

            format = "";
            return false;
        }

        private static object GetDefaultValue(ParameterInfo parameter)
        {
            var value = parameter.DefaultValue;
            if (value == DBNull.Value || value == Type.Missing)
            {
                return parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
            }

            return value;
        }
    }

}
