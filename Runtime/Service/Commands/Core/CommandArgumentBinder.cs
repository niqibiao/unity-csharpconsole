using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal static class CommandArgumentBinder
    {
        internal static bool IsInjectedParameterType(Type parameterType)
        {
            return parameterType == typeof(CommandInvocation);
        }

        internal static bool IsSupportedBoundParameterType(Type parameterType)
        {
            if (parameterType == null)
            {
                return false;
            }

            if (parameterType.IsByRef
                || parameterType.IsPointer
                || parameterType == typeof(object)
                || typeof(Delegate).IsAssignableFrom(parameterType))
            {
                return false;
            }

            var underlyingType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (underlyingType.IsArray)
            {
                return IsSupportedBoundParameterType(underlyingType.GetElementType());
            }

            if (underlyingType.IsGenericType)
            {
                if (underlyingType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return IsSupportedBoundParameterType(underlyingType.GetGenericArguments()[0]);
                }

                return false;
            }

            return true;
        }

        internal static bool TryBind(
            CommandInvocation invocation,
            ParameterInfo[] parameters,
            CommandArgumentDescriptor[] contractArguments,
            CommandContractRule[] rules,
            bool requiresSessionId,
            out object[] arguments,
            out CommandResponse errorResponse)
        {
            invocation ??= new CommandInvocation();
            parameters ??= Array.Empty<ParameterInfo>();
            contractArguments ??= Array.Empty<CommandArgumentDescriptor>();
            rules ??= Array.Empty<CommandContractRule>();

            arguments = new object[parameters.Length];
            errorResponse = null;

            if (requiresSessionId && string.IsNullOrWhiteSpace(invocation.sessionId))
            {
                errorResponse = CommandResponseFactory.ValidationError(
                    invocation,
                    $"Command requires a sessionId: {invocation.commandNamespace}/{invocation.action}");
                return false;
            }

            if (!TryParseArgsObject(invocation.argsJson, out var argsByName, out var parseError))
            {
                errorResponse = CommandResponseFactory.ValidationError(invocation, parseError);
                return false;
            }

            var consumedArgumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suppliedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var boundValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var positionalIndex = 0;
            var contractIndex = 0;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter == null)
                {
                    continue;
                }

                if (parameter.ParameterType == typeof(CommandInvocation))
                {
                    arguments[i] = invocation;
                    continue;
                }

                if (contractIndex >= contractArguments.Length)
                {
                    throw new InvalidOperationException(
                        $"Missing compiled contract for parameter '{parameter.Name}'");
                }

                var contractArgument = contractArguments[contractIndex++];
                var argumentName = parameter.Name ?? string.Empty;
                var supplied = argsByName.TryGetValue(argumentName, out var rawValue);
                if (supplied)
                {
                    consumedArgumentNames.Add(argumentName);
                }
                else
                {
                    var positionalKey = $"__pos{positionalIndex}";
                    if (argsByName.TryGetValue(positionalKey, out rawValue))
                    {
                        supplied = true;
                        consumedArgumentNames.Add(positionalKey);
                        positionalIndex++;
                    }
                    else if (parameter.HasDefaultValue)
                    {
                        var defaultValue = GetDefaultValue(parameter);
                        arguments[i] = defaultValue;
                        boundValues[argumentName] = defaultValue;
                        continue;
                    }
                    else
                    {
                        errorResponse = CommandResponseFactory.ValidationError(
                            invocation,
                            $"Missing required argument '{parameter.Name}' for {invocation.commandNamespace}/{invocation.action}");
                        return false;
                    }
                }

                if (supplied)
                {
                    suppliedArguments.Add(argumentName);
                }

                if (!CommandJsonSchemaValidator.TryValidate(
                        rawValue,
                        contractArgument.schema,
                        out var schemaError))
                {
                    errorResponse = CommandResponseFactory.ValidationError(
                        invocation,
                        $"Invalid argument '{parameter.Name}' for {invocation.commandNamespace}/{invocation.action}: {schemaError}");
                    return false;
                }

                if (!TryConvertContractValue(
                        rawValue,
                        parameter.ParameterType,
                        out var value,
                        out var conversionError))
                {
                    errorResponse = CommandResponseFactory.ValidationError(
                        invocation,
                        $"Invalid argument '{parameter.Name}' for {invocation.commandNamespace}/{invocation.action}: {conversionError}");
                    return false;
                }

                if (!TryValidateContractValue(contractArgument, value, out var validationError))
                {
                    errorResponse = CommandResponseFactory.ValidationError(
                        invocation,
                        $"Invalid argument '{argumentName}' for {invocation.commandNamespace}/{invocation.action}: {validationError}");
                    return false;
                }

                arguments[i] = value;
                boundValues[argumentName] = value;
            }

            if (contractIndex != contractArguments.Length)
            {
                throw new InvalidOperationException(
                    $"Compiled command contract has {contractArguments.Length - contractIndex} unbound argument(s)");
            }

            foreach (var argumentName in argsByName.Keys)
            {
                if (consumedArgumentNames.Contains(argumentName))
                {
                    continue;
                }

                errorResponse = CommandResponseFactory.ValidationError(
                    invocation,
                    $"Unknown argument '{argumentName}' for {invocation.commandNamespace}/{invocation.action}");
                return false;
            }

            if (!TryValidateRules(
                    contractArguments,
                    rules,
                    suppliedArguments,
                    boundValues,
                    out var ruleError))
            {
                errorResponse = CommandResponseFactory.ValidationError(invocation, ruleError);
                return false;
            }

            return true;
        }

        internal static bool TryValidateContractValue(
            CommandArgumentDescriptor descriptor,
            object value,
            out string error)
        {
            descriptor ??= new CommandArgumentDescriptor();
            error = null;

            if (value == null)
            {
                if (descriptor.schema?.nullable ?? false)
                {
                    return true;
                }

                error = "null is not allowed";
                return false;
            }

            if (descriptor.nonEmpty)
            {
                if ((value is string text && string.IsNullOrWhiteSpace(text))
                    || (value is System.Collections.ICollection collection && collection.Count == 0))
                {
                    error = "value must not be empty";
                    return false;
                }
            }

            if (descriptor.hasMinimum || descriptor.hasMaximum)
            {
                double numericValue;
                try
                {
                    numericValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    error = "value must be numeric";
                    return false;
                }

                if (descriptor.hasMinimum && numericValue < descriptor.minimum)
                {
                    error = $"value must be greater than or equal to {descriptor.minimum.ToString("R", CultureInfo.InvariantCulture)}";
                    return false;
                }

                if (descriptor.hasMaximum && numericValue > descriptor.maximum)
                {
                    error = $"value must be less than or equal to {descriptor.maximum.ToString("R", CultureInfo.InvariantCulture)}";
                    return false;
                }
            }

            var allowedValues = descriptor.allowedValues ?? Array.Empty<string>();
            if (allowedValues.Length > 0)
            {
                var comparison = descriptor.allowedValuesIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                string actual;
                try
                {
                    actual = CommandAllowedValueCodec.Encode(value);
                }
                catch (Exception validationException)
                {
                    error = validationException.Message;
                    return false;
                }
                var matched = false;
                foreach (var allowedValue in allowedValues)
                {
                    if (string.Equals(actual, allowedValue ?? "", comparison))
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    error = $"value must be one of: {string.Join(", ", allowedValues)}";
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateRules(
            CommandArgumentDescriptor[] arguments,
            CommandContractRule[] rules,
            HashSet<string> suppliedArguments,
            Dictionary<string, object> boundValues,
            out string error)
        {
            error = null;
            var argumentsByName = new Dictionary<string, CommandArgumentDescriptor>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var argument in arguments ?? Array.Empty<CommandArgumentDescriptor>())
            {
                argumentsByName[argument?.name ?? ""] = argument ?? new CommandArgumentDescriptor();
            }

            foreach (var rule in rules ?? Array.Empty<CommandContractRule>())
            {
                var effectiveRule = rule ?? new CommandContractRule();
                var ruleArguments = effectiveRule.arguments ?? Array.Empty<string>();
                var presentCount = 0;
                foreach (var argumentName in ruleArguments)
                {
                    var isPresent = effectiveRule.kind == "atLeastOneMutation"
                        ? IsSuppliedNonEmpty(
                            argumentName,
                            suppliedArguments,
                            boundValues)
                        : IsMeaningfullyPresent(
                            argumentName,
                            argumentsByName,
                            suppliedArguments,
                            boundValues);
                    if (isPresent)
                    {
                        presentCount++;
                    }
                }

                if (effectiveRule.kind == "exactlyOneOf" && presentCount != 1)
                {
                    error = $"Exactly one of [{string.Join(", ", ruleArguments)}] must be provided";
                    return false;
                }

                if (effectiveRule.kind == "atMostOneOf" && presentCount > 1)
                {
                    error = $"At most one of [{string.Join(", ", ruleArguments)}] may be provided";
                    return false;
                }

                if ((effectiveRule.kind == "atLeastOneOf"
                        || effectiveRule.kind == "atLeastOneMutation")
                    && presentCount < 1)
                {
                    error = $"At least one of [{string.Join(", ", ruleArguments)}] must be provided";
                    return false;
                }

                if (effectiveRule.kind == "requiresWhen"
                    && IsWhenConditionSatisfied(
                        effectiveRule,
                        argumentsByName,
                        suppliedArguments,
                        boundValues))
                {
                    foreach (var requiredName in effectiveRule.requires ?? Array.Empty<string>())
                    {
                        if (!IsSuppliedNonEmpty(
                                requiredName,
                                suppliedArguments,
                                boundValues))
                        {
                            error = $"Argument '{requiredName}' is required when '{effectiveRule.whenArgument}' matches {effectiveRule.whenEqualsJson}";
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static bool IsWhenConditionSatisfied(
            CommandContractRule rule,
            Dictionary<string, CommandArgumentDescriptor> argumentsByName,
            HashSet<string> suppliedArguments,
            Dictionary<string, object> boundValues)
        {
            if (string.IsNullOrEmpty(rule.whenArgument)
                || !boundValues.TryGetValue(rule.whenArgument, out var value))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(rule.whenEqualsJson))
            {
                return string.Equals(
                    CommandContractValueEncoder.Encode(value),
                    rule.whenEqualsJson,
                    StringComparison.Ordinal);
            }

            return IsMeaningfullyPresent(
                rule.whenArgument,
                argumentsByName,
                suppliedArguments,
                boundValues);
        }

        private static bool IsSuppliedNonEmpty(
            string argumentName,
            HashSet<string> suppliedArguments,
            Dictionary<string, object> boundValues)
        {
            if (!suppliedArguments.Contains(argumentName)
                || !boundValues.TryGetValue(argumentName, out var value)
                || value == null)
            {
                return false;
            }

            if (value is string text)
            {
                return !string.IsNullOrEmpty(text);
            }

            return !(value is System.Collections.ICollection collection)
                || collection.Count > 0;
        }

        private static bool IsMeaningfullyPresent(
            string argumentName,
            Dictionary<string, CommandArgumentDescriptor> argumentsByName,
            HashSet<string> suppliedArguments,
            Dictionary<string, object> boundValues)
        {
            if (!suppliedArguments.Contains(argumentName)
                || !boundValues.TryGetValue(argumentName, out var value))
            {
                return false;
            }

            if (value == null
                || (value is string text && string.IsNullOrEmpty(text))
                || (value is System.Collections.ICollection collection && collection.Count == 0))
            {
                return false;
            }

            if (!argumentsByName.TryGetValue(argumentName, out var descriptor)
                || !descriptor.hasDefault)
            {
                return true;
            }

            if (string.Equals(descriptor.defaultJson, "null", StringComparison.Ordinal))
            {
                return true;
            }

            return !string.Equals(
                CommandContractValueEncoder.Encode(value),
                descriptor.defaultJson ?? "",
                StringComparison.Ordinal);
        }

        private static object GetDefaultValue(ParameterInfo parameter)
        {
            if (parameter == null)
            {
                return null;
            }

            var defaultValue = parameter.DefaultValue;
            if (defaultValue == DBNull.Value || defaultValue == Type.Missing)
            {
                var parameterType = parameter.ParameterType;
                return parameterType != null && parameterType.IsValueType
                    ? Activator.CreateInstance(parameterType)
                    : null;
            }

            return defaultValue;
        }

        internal static bool TryConvertContractValue(
            string rawValue,
            Type parameterType,
            out object value,
            out string error)
        {
            error = null;
            value = null;

            if (parameterType == null)
            {
                error = "unsupported parameter type";
                return false;
            }

            var underlyingType = Nullable.GetUnderlyingType(parameterType);
            if (string.Equals(rawValue, "null", StringComparison.Ordinal))
            {
                if (!parameterType.IsValueType || underlyingType != null)
                {
                    return true;
                }

                error = "null is not allowed";
                return false;
            }

            var targetType = underlyingType ?? parameterType;

            if (targetType == typeof(string))
            {
                if (!TryParseStringLiteral(rawValue, out var stringValue, out _))
                {
                    error = "expected a JSON string";
                    return false;
                }

                value = stringValue;
                return true;
            }

            if (targetType == typeof(bool))
            {
                if (bool.TryParse(rawValue, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }

                error = "expected true or false";
                return false;
            }

            if (targetType == typeof(int))
            {
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    value = intValue;
                    return true;
                }

                error = "expected an integer";
                return false;
            }

            if (targetType == typeof(uint))
            {
                if (uint.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uintValue))
                {
                    value = uintValue;
                    return true;
                }

                error = "expected an unsigned integer";
                return false;
            }

            if (targetType == typeof(long))
            {
                if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    value = longValue;
                    return true;
                }

                error = "expected a 64-bit integer";
                return false;
            }

            if (targetType == typeof(ulong))
            {
                if (ulong.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ulongValue))
                {
                    value = ulongValue;
                    return true;
                }

                error = "expected an unsigned 64-bit integer";
                return false;
            }

            if (targetType == typeof(short))
            {
                if (short.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shortValue))
                {
                    value = shortValue;
                    return true;
                }

                error = "expected a 16-bit integer";
                return false;
            }

            if (targetType == typeof(ushort))
            {
                if (ushort.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ushortValue))
                {
                    value = ushortValue;
                    return true;
                }

                error = "expected an unsigned 16-bit integer";
                return false;
            }

            if (targetType == typeof(byte))
            {
                if (byte.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteValue))
                {
                    value = byteValue;
                    return true;
                }

                error = "expected a byte";
                return false;
            }

            if (targetType == typeof(sbyte))
            {
                if (sbyte.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sbyteValue))
                {
                    value = sbyteValue;
                    return true;
                }

                error = "expected an 8-bit integer";
                return false;
            }

            if (targetType == typeof(float))
            {
                if (float.TryParse(
                        rawValue,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out var floatValue)
                    && !float.IsNaN(floatValue)
                    && !float.IsInfinity(floatValue))
                {
                    value = floatValue;
                    return true;
                }

                error = "expected a number";
                return false;
            }

            if (targetType == typeof(double))
            {
                if (double.TryParse(
                        rawValue,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out var doubleValue)
                    && !double.IsNaN(doubleValue)
                    && !double.IsInfinity(doubleValue))
                {
                    value = doubleValue;
                    return true;
                }

                error = "expected a number";
                return false;
            }

            if (targetType == typeof(decimal))
            {
                if (decimal.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var decimalValue))
                {
                    value = decimalValue;
                    return true;
                }

                error = "expected a decimal number";
                return false;
            }

            if (targetType == typeof(char))
            {
                if (TryParseStringLiteral(rawValue, out var charLiteral, out _)
                    && !string.IsNullOrEmpty(charLiteral)
                    && charLiteral.Length == 1)
                {
                    value = charLiteral[0];
                    return true;
                }

                error = "expected a single-character JSON string";
                return false;
            }

            if (targetType.IsEnum)
            {
                if (TryParseStringLiteral(rawValue, out var enumName, out _))
                {
                    try
                    {
                        var parsed = Enum.Parse(targetType, enumName, false);
                        if (!Enum.IsDefined(targetType, parsed))
                        {
                            throw new ArgumentException("undefined enum value");
                        }

                        value = parsed;
                        return true;
                    }
                    catch
                    {
                        error = $"expected one of: {string.Join(", ", Enum.GetNames(targetType))}";
                        return false;
                    }
                }

                error = $"expected one of: {string.Join(", ", Enum.GetNames(targetType))}";
                return false;
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return TryConvertListValue(rawValue, targetType, out value, out error);
            }

            if (targetType.IsArray)
            {
                return TryConvertArrayValue(rawValue, targetType, out value, out error);
            }

            try
            {
                value = JsonUtility.FromJson(rawValue, targetType);
                if (value != null || !targetType.IsValueType)
                {
                    return true;
                }

                value = Activator.CreateInstance(targetType);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool TryConvertArrayValue(string rawValue, Type arrayType, out object value, out string error)
        {
            value = null;
            error = null;

            var wrapperType = typeof(JsonArrayWrapper<>).MakeGenericType(arrayType.GetElementType());
            var wrappedJson = "{\"items\":" + rawValue + "}";
            try
            {
                var wrapper = JsonUtility.FromJson(wrappedJson, wrapperType);
                var itemsField = wrapperType.GetField("items");
                value = itemsField?.GetValue(wrapper) ?? Array.CreateInstance(arrayType.GetElementType(), 0);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool TryConvertListValue(string rawValue, Type listType, out object value, out string error)
        {
            var elementType = listType.GetGenericArguments()[0];
            var arrayType = elementType.MakeArrayType();
            if (!TryConvertArrayValue(rawValue, arrayType, out var arrayValue, out error))
            {
                value = null;
                return false;
            }

            var array = (Array)arrayValue;
            var list = (System.Collections.IList)Activator.CreateInstance(listType, array.Length);
            for (var i = 0; i < array.Length; i++)
            {
                list.Add(array.GetValue(i));
            }

            value = list;
            error = null;
            return true;
        }

        private static bool TryParseArgsObject(string argsJson, out Dictionary<string, string> properties, out string error)
        {
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;

            if (string.IsNullOrWhiteSpace(argsJson))
            {
                return true;
            }

            var index = 0;
            SkipWhitespace(argsJson, ref index);
            if (index >= argsJson.Length)
            {
                return true;
            }

            if (argsJson[index] != '{')
            {
                error = "argsJson must be a JSON object";
                return false;
            }

            index++;
            var mayClose = true;
            while (true)
            {
                SkipWhitespace(argsJson, ref index);
                if (index >= argsJson.Length)
                {
                    error = "argsJson ended before the JSON object was closed";
                    return false;
                }

                if (argsJson[index] == '}')
                {
                    if (!mayClose)
                    {
                        error = "argsJson contains a trailing comma";
                        return false;
                    }

                    index++;
                    SkipWhitespace(argsJson, ref index);
                    if (index != argsJson.Length)
                    {
                        error = "argsJson contains trailing content";
                        return false;
                    }

                    return true;
                }

                if (!TryParseStringLiteral(argsJson, out var name, ref index))
                {
                    error = "argsJson contains an invalid property name";
                    return false;
                }

                mayClose = true;
                SkipWhitespace(argsJson, ref index);
                if (index >= argsJson.Length || argsJson[index] != ':')
                {
                    error = $"argsJson is missing ':' after property '{name}'";
                    return false;
                }

                index++;
                SkipWhitespace(argsJson, ref index);
                var valueStart = index;
                if (!TrySkipValue(argsJson, ref index))
                {
                    error = $"argsJson contains an invalid value for '{name}'";
                    return false;
                }

                if (properties.ContainsKey(name))
                {
                    error = $"argsJson contains duplicate property '{name}'";
                    return false;
                }

                properties.Add(name, argsJson.Substring(valueStart, index - valueStart));

                SkipWhitespace(argsJson, ref index);
                if (index >= argsJson.Length)
                {
                    error = "argsJson ended before the JSON object was closed";
                    return false;
                }

                if (argsJson[index] == ',')
                {
                    index++;
                    mayClose = false;
                    continue;
                }

                if (argsJson[index] == '}')
                {
                    index++;
                    SkipWhitespace(argsJson, ref index);
                    if (index != argsJson.Length)
                    {
                        error = "argsJson contains trailing content";
                        return false;
                    }

                    return true;
                }

                error = "argsJson is missing ',' or '}'";
                return false;
            }
        }

        internal static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        internal static bool TrySkipValue(string text, ref int index)
        {
            if (index >= text.Length)
            {
                return false;
            }

            switch (text[index])
            {
                case '"':
                    return TryParseStringLiteral(text, out _, ref index);
                case '{':
                    return TrySkipCompound(text, ref index, '{', '}');
                case '[':
                    return TrySkipCompound(text, ref index, '[', ']');
                default:
                    var start = index;
                    while (index < text.Length)
                    {
                        var c = text[index];
                        if (c == ',' || c == '}' || c == ']')
                        {
                            break;
                        }

                        if (char.IsWhiteSpace(c))
                        {
                            break;
                        }

                        index++;
                    }

                    return index > start;
            }
        }

        private static bool TrySkipCompound(string text, ref int index, char open, char close)
        {
            if (index >= text.Length || text[index] != open)
            {
                return false;
            }

            var depth = 0;
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"')
                {
                    if (!TrySkipStringCharacters(text, ref index))
                    {
                        return false;
                    }

                    continue;
                }

                if (c == open)
                {
                    depth++;
                    continue;
                }

                if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool TryParseStringLiteral(string text, out string value, ref int index)
        {
            value = null;
            if (index >= text.Length || text[index] != '"')
            {
                return false;
            }

            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    if (c < 0x20)
                    {
                        return false;
                    }

                    builder.Append(c);
                    continue;
                }

                if (index >= text.Length)
                {
                    return false;
                }

                var escaped = text[index++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 > text.Length)
                        {
                            return false;
                        }

                        if (!ushort.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                        {
                            return false;
                        }

                        builder.Append((char)codePoint);
                        index += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool TryParseStringLiteral(string rawValue, out string value, out string error)
        {
            value = null;
            error = null;

            var index = 0;
            if (!TryParseStringLiteral(rawValue, out value, ref index))
            {
                error = "invalid JSON string";
                return false;
            }

            SkipWhitespace(rawValue, ref index);
            if (index != rawValue.Length)
            {
                error = "invalid trailing content after JSON string";
                return false;
            }

            return true;
        }

        private static bool TrySkipStringCharacters(string text, ref int index)
        {
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"')
                {
                    return true;
                }

                if (c == '\\')
                {
                    if (index >= text.Length)
                    {
                        return false;
                    }

                    var escaped = text[index++];
                    if (escaped == 'u')
                    {
                        if (index + 4 > text.Length)
                        {
                            return false;
                        }

                        index += 4;
                    }
                }
            }

            return false;
        }

        [Serializable]
        private sealed class JsonArrayWrapper<T>
        {
            public T[] items = Array.Empty<T>();
        }
    }
}
