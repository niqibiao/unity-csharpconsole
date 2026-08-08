using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal sealed class CommandValueSchema
    {
        public string kind = "empty";
        public string format = "";
        public bool nullable;
        [CommandWireField("$ref")]
        public string reference = "";
        public CommandValueSchema items;
        public CommandSchemaField[] fields = Array.Empty<CommandSchemaField>();
        public string[] enumValues = Array.Empty<string>();
        [CommandWireField(
            "$defs",
            typeof(Dictionary<string, CommandValueSchema>))]
        public CommandSchemaDefinition[] definitions = Array.Empty<CommandSchemaDefinition>();
    }

    internal sealed class CommandSchemaDefinition
    {
        public string id = "";
        public CommandValueSchema schema = new CommandValueSchema();
    }

    internal sealed class CommandSchemaField
    {
        public string name = "";
        public CommandValueSchema schema = new CommandValueSchema();
        public bool required;
        public bool nonEmpty;
        public bool hasMinimum;
        public double minimum;
        public bool hasMaximum;
        public double maximum;
        public string[] allowedValues = Array.Empty<string>();
        public bool allowedValuesIgnoreCase;
    }

    [Serializable]
    internal sealed class CommandContractRule
    {
        public string kind = "";
        public string[] arguments = Array.Empty<string>();
        public string whenArgument = "";
        public string whenEqualsJson = "";
        public string[] requires = Array.Empty<string>();
    }

    internal static class CommandContractRuleOrder
    {
        internal static int Compare(CommandContractRule left, CommandContractRule right)
        {
            var comparison = string.CompareOrdinal(left?.kind ?? "", right?.kind ?? "");
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStrings(left?.arguments, right?.arguments);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(
                left?.whenArgument ?? "",
                right?.whenArgument ?? "");
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(
                left?.whenEqualsJson ?? "",
                right?.whenEqualsJson ?? "");
            return comparison != 0
                ? comparison
                : CompareStrings(left?.requires, right?.requires);
        }

        private static int CompareStrings(string[] left, string[] right)
        {
            left ??= Array.Empty<string>();
            right ??= Array.Empty<string>();
            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = string.CompareOrdinal(
                    left[index] ?? "",
                    right[index] ?? "");
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }

    internal static class CommandContractValueEncoder
    {
        internal static string Encode(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string text)
            {
                return Quote(text);
            }

            if (value is char character)
            {
                return Quote(character.ToString());
            }

            if (value is bool boolean)
            {
                return boolean ? "true" : "false";
            }

            if (value.GetType().IsEnum)
            {
                var enumName = Enum.GetName(value.GetType(), value);
                if (enumName == null)
                {
                    throw new InvalidOperationException(
                        $"Undefined enum values cannot be command defaults: {value}");
                }

                return Quote(enumName);
            }

            if (value is float single)
            {
                if (float.IsNaN(single) || float.IsInfinity(single))
                {
                    throw new InvalidOperationException(
                        "Command contract numbers must be finite");
                }

                return NormalizeZero(single.ToString("R", CultureInfo.InvariantCulture));
            }

            if (value is double number)
            {
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new InvalidOperationException(
                        "Command contract numbers must be finite");
                }

                return NormalizeZero(number.ToString("R", CultureInfo.InvariantCulture));
            }

            if (value is decimal decimalNumber)
            {
                return NormalizeZero(decimalNumber.ToString("G29", CultureInfo.InvariantCulture));
            }

            if (value is byte
                || value is sbyte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is IEnumerable sequence)
            {
                var values = new List<string>();
                foreach (var item in sequence)
                {
                    values.Add(Encode(item));
                }

                return "[" + string.Join(",", values) + "]";
            }

            throw new InvalidOperationException(
                $"Unsupported command default value type: {value.GetType().FullName}");
        }

        internal static string Quote(string value)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            foreach (var character in value ?? "")
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static string NormalizeZero(string value)
        {
            return value == "-0" ? "0" : value;
        }
    }

    internal static class CommandAllowedValueCodec
    {
        internal static string Encode(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string text)
            {
                return CommandContractValueEncoder.Encode(text);
            }

            if (value is char character)
            {
                return CommandContractValueEncoder.Encode(character);
            }

            if (value is bool boolean)
            {
                return CommandContractValueEncoder.Encode(boolean);
            }

            if (value.GetType().IsEnum)
            {
                return CommandContractValueEncoder.Encode(value);
            }

            if (value is byte
                || value is sbyte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong
                || value is float
                || value is double
                || value is decimal)
            {
                return CommandContractValueEncoder.Encode(value);
            }

            throw new InvalidOperationException(
                $"Allowed values are unsupported for type: {value.GetType().FullName}");
        }
    }
}
