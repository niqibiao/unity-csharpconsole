using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal static class CommandJsonSchemaValidator
    {
        internal static bool TryValidate(
            string rawValue,
            CommandValueSchema schema,
            out string error)
        {
            schema ??= new CommandValueSchema();
            var definitions = new Dictionary<string, CommandValueSchema>(
                StringComparer.Ordinal);
            foreach (var definition in schema.definitions
                     ?? Array.Empty<CommandSchemaDefinition>())
            {
                var id = definition?.id ?? "";
                if (string.IsNullOrEmpty(id)
                    || definitions.ContainsKey(id))
                {
                    error = $"schema contains an invalid definition id '{id}'";
                    return false;
                }

                definitions.Add(
                    id,
                    definition?.schema ?? new CommandValueSchema());
            }

            return TryValidateValue(
                rawValue,
                schema,
                definitions,
                "",
                out error);
        }

        private static bool TryValidateValue(
            string rawValue,
            CommandValueSchema schema,
            Dictionary<string, CommandValueSchema> definitions,
            string path,
            out string error)
        {
            schema ??= new CommandValueSchema();
            rawValue = (rawValue ?? "").Trim();
            if (string.Equals(rawValue, "null", StringComparison.Ordinal))
            {
                if (schema.nullable)
                {
                    error = null;
                    return true;
                }

                error = AtPath("null is not allowed", path);
                return false;
            }

            if (string.Equals(schema.kind, "reference", StringComparison.Ordinal))
            {
                if (!definitions.TryGetValue(schema.reference ?? "", out var target))
                {
                    error = AtPath(
                        $"schema reference '{schema.reference}' is unresolved",
                        path);
                    return false;
                }

                return TryValidateValue(rawValue, target, definitions, path, out error);
            }

            switch (schema.kind)
            {
                case "empty":
                    error = null;
                    return true;
                case "string":
                    return TryValidateString(rawValue, schema, path, out error);
                case "boolean":
                    if (rawValue == "true" || rawValue == "false")
                    {
                        error = null;
                        return true;
                    }

                    error = AtPath("expected true or false", path);
                    return false;
                case "enum":
                    return TryValidateEnum(rawValue, schema, path, out error);
                case "integer":
                    return TryValidateInteger(rawValue, schema.format, path, out error);
                case "number":
                    return TryValidateNumber(rawValue, schema.format, path, out error);
                case "array":
                    return TryValidateArray(
                        rawValue,
                        schema,
                        definitions,
                        path,
                        out error);
                case "object":
                    return TryValidateObject(
                        rawValue,
                        schema,
                        definitions,
                        path,
                        out error);
                default:
                    error = AtPath(
                        $"schema kind '{schema.kind}' is unsupported",
                        path);
                    return false;
            }
        }

        private static bool TryValidateString(
            string rawValue,
            CommandValueSchema schema,
            string path,
            out string error)
        {
            var index = 0;
            if (!CommandArgumentBinder.TryParseStringLiteral(
                    rawValue,
                    out var value,
                    ref index))
            {
                error = AtPath("expected a JSON string", path);
                return false;
            }

            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            if (index != rawValue.Length)
            {
                error = AtPath("expected a JSON string", path);
                return false;
            }

            if (schema.format == "char" && value.Length != 1)
            {
                error = AtPath("expected a single-character JSON string", path);
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryValidateEnum(
            string rawValue,
            CommandValueSchema schema,
            string path,
            out string error)
        {
            if (!TryDecodeJsonString(rawValue, out var value))
            {
                error = AtPath(
                    $"expected one of: {string.Join(", ", schema.enumValues ?? Array.Empty<string>())}",
                    path);
                return false;
            }

            if (!(schema.enumValues ?? Array.Empty<string>())
                .Contains(value, StringComparer.Ordinal))
            {
                error = AtPath(
                    $"expected one of: {string.Join(", ", schema.enumValues ?? Array.Empty<string>())}",
                    path);
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryValidateInteger(
            string rawValue,
            string format,
            string path,
            out string error)
        {
            if (!IsJsonInteger(rawValue))
            {
                error = AtPath("expected an integer", path);
                return false;
            }

            var unsigned = format != null && format.StartsWith("uint", StringComparison.Ordinal);
            if (unsigned)
            {
                if (!ulong.TryParse(
                        rawValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var unsignedValue)
                    || !FitsUnsignedIntegerFormat(unsignedValue, format))
                {
                    error = AtPath($"integer is out of range for {format}", path);
                    return false;
                }
            }
            else if (!long.TryParse(
                         rawValue,
                         NumberStyles.AllowLeadingSign,
                         CultureInfo.InvariantCulture,
                         out var signedValue)
                     || !FitsSignedIntegerFormat(signedValue, format))
            {
                error = AtPath($"integer is out of range for {format}", path);
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryValidateNumber(
            string rawValue,
            string format,
            string path,
            out string error)
        {
            if (!IsJsonNumber(rawValue))
            {
                error = AtPath("expected a number", path);
                return false;
            }

            if (format == "float32")
            {
                if (!float.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var floatValue)
                    || float.IsNaN(floatValue)
                    || float.IsInfinity(floatValue))
                {
                    error = AtPath("number is out of range for float32", path);
                    return false;
                }
            }
            else if (format == "decimal")
            {
                if (!decimal.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out _))
                {
                    error = AtPath("number is out of range for decimal", path);
                    return false;
                }
            }
            else if (!double.TryParse(
                         rawValue,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out var doubleValue)
                     || double.IsNaN(doubleValue)
                     || double.IsInfinity(doubleValue))
            {
                error = AtPath("number is out of range for float64", path);
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryValidateArray(
            string rawValue,
            CommandValueSchema schema,
            Dictionary<string, CommandValueSchema> definitions,
            string path,
            out string error)
        {
            var index = 0;
            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            if (index >= rawValue.Length || rawValue[index] != '[')
            {
                error = AtPath("expected a JSON array", path);
                return false;
            }

            index++;
            var itemIndex = 0;
            var mayClose = true;
            while (true)
            {
                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                if (index >= rawValue.Length)
                {
                    error = AtPath("JSON array was not closed", path);
                    return false;
                }

                if (rawValue[index] == ']')
                {
                    if (!mayClose)
                    {
                        error = AtPath("JSON array has a trailing comma", path);
                        return false;
                    }

                    index++;
                    CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                    if (index != rawValue.Length)
                    {
                        error = AtPath("JSON array contains trailing content", path);
                        return false;
                    }

                    error = null;
                    return true;
                }

                var valueStart = index;
                mayClose = true;
                if (!CommandArgumentBinder.TrySkipValue(rawValue, ref index))
                {
                    error = AtPath($"array item {itemIndex} is invalid", path);
                    return false;
                }

                var itemRaw = rawValue.Substring(valueStart, index - valueStart);
                var itemPath = string.IsNullOrEmpty(path)
                    ? $"[{itemIndex}]"
                    : $"{path}[{itemIndex}]";
                if (!TryValidateValue(
                        itemRaw,
                        schema.items,
                        definitions,
                        itemPath,
                        out error))
                {
                    return false;
                }

                itemIndex++;
                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                if (index >= rawValue.Length)
                {
                    error = AtPath("JSON array was not closed", path);
                    return false;
                }

                if (rawValue[index] == ',')
                {
                    index++;
                    mayClose = false;
                    continue;
                }

                if (rawValue[index] != ']')
                {
                    error = AtPath("JSON array is missing ',' or ']'", path);
                    return false;
                }
            }
        }

        private static bool TryValidateObject(
            string rawValue,
            CommandValueSchema schema,
            Dictionary<string, CommandValueSchema> definitions,
            string path,
            out string error)
        {
            var expectedFields = (schema.fields ?? Array.Empty<CommandSchemaField>())
                .ToDictionary(
                    field => field?.name ?? "",
                    field => field ?? new CommandSchemaField(),
                    StringComparer.Ordinal);
            var suppliedFields = new HashSet<string>(StringComparer.Ordinal);

            var index = 0;
            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            if (index >= rawValue.Length || rawValue[index] != '{')
            {
                error = AtPath("expected a JSON object", path);
                return false;
            }

            index++;
            var mayClose = true;
            while (true)
            {
                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                if (index >= rawValue.Length)
                {
                    error = AtPath("JSON object was not closed", path);
                    return false;
                }

                if (rawValue[index] == '}')
                {
                    if (!mayClose)
                    {
                        error = AtPath("JSON object has a trailing comma", path);
                        return false;
                    }

                    index++;
                    CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                    if (index != rawValue.Length)
                    {
                        error = AtPath("JSON object contains trailing content", path);
                        return false;
                    }

                    break;
                }

                if (!CommandArgumentBinder.TryParseStringLiteral(
                        rawValue,
                        out var name,
                        ref index))
                {
                    error = AtPath("object contains an invalid field name", path);
                    return false;
                }

                mayClose = true;
                if (!suppliedFields.Add(name))
                {
                    error = AtPath($"duplicate field '{name}'", path);
                    return false;
                }

                if (!expectedFields.TryGetValue(name, out var field))
                {
                    error = AtPath($"unknown field '{name}'", path);
                    return false;
                }

                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                if (index >= rawValue.Length || rawValue[index] != ':')
                {
                    error = AtPath($"missing ':' after field '{name}'", path);
                    return false;
                }

                index++;
                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                var valueStart = index;
                if (!CommandArgumentBinder.TrySkipValue(rawValue, ref index))
                {
                    error = AtPath($"field '{name}' has an invalid value", path);
                    return false;
                }

                var fieldRaw = rawValue.Substring(valueStart, index - valueStart);
                var fieldPath = string.IsNullOrEmpty(path)
                    ? name
                    : $"{path}.{name}";
                if (!TryValidateValue(
                        fieldRaw,
                        field.schema,
                        definitions,
                        fieldPath,
                        out error)
                    || !TryValidateFieldConstraints(
                        fieldRaw,
                        field,
                        fieldPath,
                        out error))
                {
                    return false;
                }

                CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
                if (index >= rawValue.Length)
                {
                    error = AtPath("JSON object was not closed", path);
                    return false;
                }

                if (rawValue[index] == ',')
                {
                    index++;
                    mayClose = false;
                    continue;
                }

                if (rawValue[index] != '}')
                {
                    error = AtPath("JSON object is missing ',' or '}'", path);
                    return false;
                }
            }

            foreach (var field in expectedFields.Values)
            {
                if (field.required && !suppliedFields.Contains(field.name ?? ""))
                {
                    error = AtPath(
                        $"missing required field '{field.name}'",
                        path);
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool TryValidateFieldConstraints(
            string rawValue,
            CommandSchemaField field,
            string path,
            out string error)
        {
            rawValue = (rawValue ?? "").Trim();
            if (rawValue == "null")
            {
                error = null;
                return true;
            }

            if (field.nonEmpty)
            {
                if (field.schema.kind == "string"
                    && (!TryDecodeJsonString(rawValue, out var text)
                        || string.IsNullOrWhiteSpace(text)))
                {
                    error = AtPath("value must not be empty", path);
                    return false;
                }

                if (field.schema.kind == "array"
                    && IsEmptyJsonArray(rawValue))
                {
                    error = AtPath("value must not be empty", path);
                    return false;
                }
            }

            if (field.hasMinimum || field.hasMaximum)
            {
                if (!double.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var numericValue))
                {
                    error = AtPath("value must be numeric", path);
                    return false;
                }

                if (field.hasMinimum && numericValue < field.minimum)
                {
                    error = AtPath(
                        $"value must be greater than or equal to {field.minimum.ToString("R", CultureInfo.InvariantCulture)}",
                        path);
                    return false;
                }

                if (field.hasMaximum && numericValue > field.maximum)
                {
                    error = AtPath(
                        $"value must be less than or equal to {field.maximum.ToString("R", CultureInfo.InvariantCulture)}",
                        path);
                    return false;
                }
            }

            var allowedValues = field.allowedValues ?? Array.Empty<string>();
            if (allowedValues.Length > 0)
            {
                if (!TryCanonicalizeScalar(
                        rawValue,
                        field.schema,
                        out var canonical))
                {
                    error = AtPath("value cannot be compared to allowed values", path);
                    return false;
                }

                var comparison = field.allowedValuesIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!allowedValues.Any(
                        allowed => string.Equals(
                            canonical,
                            allowed ?? "",
                            comparison)))
                {
                    error = AtPath(
                        $"value must be one of: {string.Join(", ", allowedValues)}",
                        path);
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool TryCanonicalizeScalar(
            string rawValue,
            CommandValueSchema schema,
            out string canonical)
        {
            canonical = null;
            if (schema.kind == "string" || schema.kind == "enum")
            {
                if (!TryDecodeJsonString(rawValue, out var text))
                {
                    return false;
                }

                canonical = CommandContractValueEncoder.Encode(text);
                return true;
            }

            if (schema.kind == "boolean")
            {
                if (rawValue == "true" || rawValue == "false")
                {
                    canonical = rawValue;
                    return true;
                }

                return false;
            }

            if (schema.kind == "integer")
            {
                if (rawValue.StartsWith("-", StringComparison.Ordinal))
                {
                    if (!long.TryParse(
                            rawValue,
                            NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture,
                            out var signed))
                    {
                        return false;
                    }

                    canonical = CommandContractValueEncoder.Encode(signed);
                    return true;
                }

                if (!ulong.TryParse(
                        rawValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var unsigned))
                {
                    return false;
                }

                canonical = CommandContractValueEncoder.Encode(unsigned);
                return true;
            }

            if (schema.kind == "number" && schema.format == "float32")
            {
                if (!float.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var floatValue)
                    || float.IsNaN(floatValue)
                    || float.IsInfinity(floatValue))
                {
                    return false;
                }

                canonical = CommandContractValueEncoder.Encode(floatValue);
                return true;
            }

            if (schema.kind == "number" && schema.format == "decimal")
            {
                if (!decimal.TryParse(
                        rawValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var decimalValue))
                {
                    return false;
                }

                canonical = CommandContractValueEncoder.Encode(decimalValue);
                return true;
            }

            if (schema.kind == "number"
                && double.TryParse(
                    rawValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var doubleValue)
                && !double.IsNaN(doubleValue)
                && !double.IsInfinity(doubleValue))
            {
                canonical = CommandContractValueEncoder.Encode(doubleValue);
                return true;
            }

            return false;
        }

        private static bool TryDecodeJsonString(string rawValue, out string value)
        {
            var index = 0;
            if (!CommandArgumentBinder.TryParseStringLiteral(
                    rawValue,
                    out value,
                    ref index))
            {
                return false;
            }

            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            return index == rawValue.Length;
        }

        private static bool IsEmptyJsonArray(string rawValue)
        {
            var index = 0;
            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            if (index >= rawValue.Length || rawValue[index++] != '[')
            {
                return false;
            }

            CommandArgumentBinder.SkipWhitespace(rawValue, ref index);
            return index < rawValue.Length && rawValue[index] == ']';
        }

        private static bool IsJsonInteger(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var index = text[0] == '-' ? 1 : 0;
            if (index >= text.Length)
            {
                return false;
            }

            if (text[index] == '0')
            {
                return index + 1 == text.Length;
            }

            if (text[index] < '1' || text[index] > '9')
            {
                return false;
            }

            for (index++; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsJsonNumber(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var index = text[0] == '-' ? 1 : 0;
            if (index >= text.Length)
            {
                return false;
            }

            if (text[index] == '0')
            {
                index++;
            }
            else
            {
                if (text[index] < '1' || text[index] > '9')
                {
                    return false;
                }

                while (++index < text.Length
                       && text[index] >= '0'
                       && text[index] <= '9')
                {
                }
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                var fractionStart = index;
                while (index < text.Length
                       && text[index] >= '0'
                       && text[index] <= '9')
                {
                    index++;
                }

                if (index == fractionStart)
                {
                    return false;
                }
            }

            if (index < text.Length
                && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length
                    && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                var exponentStart = index;
                while (index < text.Length
                       && text[index] >= '0'
                       && text[index] <= '9')
                {
                    index++;
                }

                if (index == exponentStart)
                {
                    return false;
                }
            }

            return index == text.Length;
        }

        private static bool FitsUnsignedIntegerFormat(ulong value, string format)
        {
            switch (format)
            {
                case "uint8":
                    return value <= byte.MaxValue;
                case "uint16":
                    return value <= ushort.MaxValue;
                case "uint32":
                    return value <= uint.MaxValue;
                case "uint64":
                case "":
                case null:
                    return true;
                default:
                    return false;
            }
        }

        private static bool FitsSignedIntegerFormat(long value, string format)
        {
            switch (format)
            {
                case "int8":
                    return value >= sbyte.MinValue && value <= sbyte.MaxValue;
                case "int16":
                    return value >= short.MinValue && value <= short.MaxValue;
                case "int32":
                    return value >= int.MinValue && value <= int.MaxValue;
                case "int64":
                case "":
                case null:
                    return true;
                default:
                    return false;
            }
        }

        private static string AtPath(string message, string path)
        {
            return string.IsNullOrEmpty(path)
                ? message
                : $"{message} at '{path}'";
        }
    }
}
