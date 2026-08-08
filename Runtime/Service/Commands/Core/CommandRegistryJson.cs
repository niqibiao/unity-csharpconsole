using System;
using System.Globalization;
using System.Text;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal static class CommandRegistryJson
    {
        internal static string SerializeSnapshot(CommandRegistrySnapshot snapshot)
        {
            snapshot ??= new CommandRegistrySnapshot();
            var json = new StringBuilder();
            json.Append('{');
            PropertyName(json, "schemaVersion");
            json.Append(snapshot.schemaVersion.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            StringProperty(json, "registryGeneration", snapshot.registryGeneration);
            if (snapshot.unchanged)
            {
                json.Append(',');
                BooleanProperty(json, "unchanged", true);
                json.Append('}');
                return json.ToString();
            }

            json.Append(',');
            PropertyName(json, "builtin");
            WritePartition(json, snapshot.builtin);
            json.Append(',');
            PropertyName(json, "custom");
            WritePartition(json, snapshot.custom);
            json.Append('}');
            return json.ToString();
        }

        internal static string SerializeCommandList(CommandRegistrySnapshot snapshot)
        {
            snapshot ??= new CommandRegistrySnapshot();
            var json = new StringBuilder();
            json.Append('{');
            PropertyName(json, "commands");
            json.Append('[');
            var wroteCommand = false;
            WriteCommands(json, snapshot.builtin?.commands, ref wroteCommand);
            WriteCommands(json, snapshot.custom?.commands, ref wroteCommand);
            json.Append(']');
            json.Append('}');
            return json.ToString();
        }

        private static void WritePartition(
            StringBuilder json,
            CommandRegistryPartitionSnapshot partition)
        {
            partition ??= new CommandRegistryPartitionSnapshot();
            json.Append('{');
            BooleanProperty(json, "included", partition.included);
            json.Append(',');
            PropertyName(json, "count");
            json.Append(partition.count.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            StringProperty(json, "fingerprint", partition.fingerprint);
            json.Append(',');
            PropertyName(json, "commands");
            json.Append('[');
            var wroteCommand = false;
            WriteCommands(json, partition.commands, ref wroteCommand);
            json.Append(']');
            json.Append('}');
        }

        private static void WriteCommands(
            StringBuilder json,
            RegistryCommandContract[] commands,
            ref bool wroteCommand)
        {
            commands ??= Array.Empty<RegistryCommandContract>();
            for (var index = 0; index < commands.Length; index++)
            {
                if (wroteCommand)
                {
                    json.Append(',');
                }

                WriteCommand(json, commands[index]);
                wroteCommand = true;
            }
        }

        private static void WriteCommand(
            StringBuilder json,
            RegistryCommandContract command)
        {
            command ??= new RegistryCommandContract();
            json.Append('{');
            StringProperty(json, "id", command.id);
            json.Append(',');
            PropertyName(json, "wire");
            WriteWire(json, command.wire);
            json.Append(',');
            StringProperty(json, "summary", command.summary);
            json.Append(',');
            StringProperty(json, "partition", command.partition);
            json.Append(',');
            PropertyName(json, "requirements");
            WriteRequirements(json, command.requirements);
            json.Append(',');
            PropertyName(json, "arguments");
            WriteArguments(json, command.arguments);
            json.Append(',');
            PropertyName(json, "result");
            WriteSchema(json, command.result);
            json.Append(',');
            PropertyName(json, "rules");
            WriteRules(json, command.rules);
            json.Append('}');
        }

        private static void WriteWire(StringBuilder json, RegistryCommandWire wire)
        {
            wire ??= new RegistryCommandWire();
            json.Append('{');
            StringProperty(json, "commandNamespace", wire.commandNamespace);
            json.Append(',');
            StringProperty(json, "action", wire.action);
            json.Append('}');
        }

        private static void WriteRequirements(
            StringBuilder json,
            RegistryCommandRequirements requirements)
        {
            requirements ??= new RegistryCommandRequirements();
            json.Append('{');
            BooleanProperty(json, "editor", requirements.editor);
            json.Append(',');
            BooleanProperty(json, "mainThread", requirements.mainThread);
            json.Append(',');
            BooleanProperty(json, "sessionId", requirements.sessionId);
            json.Append('}');
        }

        private static void WriteArguments(
            StringBuilder json,
            RegistryCommandArgument[] arguments)
        {
            json.Append('[');
            arguments ??= Array.Empty<RegistryCommandArgument>();
            for (var index = 0; index < arguments.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                var argument = arguments[index] ?? new RegistryCommandArgument();
                json.Append('{');
                StringProperty(json, "name", argument.name);
                json.Append(',');
                PropertyName(json, "schema");
                WriteSchema(json, argument.schema);
                json.Append(',');
                BooleanProperty(json, "required", argument.required);
                json.Append(',');
                BooleanProperty(json, "hasDefault", argument.hasDefault);
                json.Append(',');
                StringProperty(json, "defaultJson", argument.defaultJson);
                json.Append(',');
                BooleanProperty(json, "nonEmpty", argument.nonEmpty);
                json.Append(',');
                BooleanProperty(json, "hasMinimum", argument.hasMinimum);
                if (argument.hasMinimum)
                {
                    json.Append(',');
                    NumberProperty(json, "minimum", argument.minimum);
                }

                json.Append(',');
                BooleanProperty(json, "hasMaximum", argument.hasMaximum);
                if (argument.hasMaximum)
                {
                    json.Append(',');
                    NumberProperty(json, "maximum", argument.maximum);
                }

                json.Append(',');
                PropertyName(json, "allowedValues");
                WriteStrings(json, argument.allowedValues);
                json.Append(',');
                BooleanProperty(
                    json,
                    "allowedValuesIgnoreCase",
                    argument.allowedValuesIgnoreCase);
                json.Append('}');
            }

            json.Append(']');
        }

        private static void WriteSchema(StringBuilder json, CommandValueSchema schema)
        {
            schema ??= new CommandValueSchema();
            json.Append('{');
            StringProperty(json, "kind", schema.kind);
            json.Append(',');
            StringProperty(json, "format", schema.format);
            json.Append(',');
            BooleanProperty(json, "nullable", schema.nullable);
            if (!string.IsNullOrEmpty(schema.reference))
            {
                json.Append(',');
                StringProperty(json, "$ref", schema.reference);
            }

            if (schema.items != null)
            {
                json.Append(',');
                PropertyName(json, "items");
                WriteSchema(json, schema.items);
            }

            json.Append(',');
            PropertyName(json, "enumValues");
            WriteStrings(json, schema.enumValues);
            json.Append(',');
            PropertyName(json, "fields");
            json.Append('[');
            var fields = schema.fields ?? Array.Empty<CommandSchemaField>();
            for (var index = 0; index < fields.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                var field = fields[index] ?? new CommandSchemaField();
                json.Append('{');
                StringProperty(json, "name", field.name);
                json.Append(',');
                PropertyName(json, "schema");
                WriteSchema(json, field.schema);
                if (field.required)
                {
                    json.Append(',');
                    BooleanProperty(json, "required", true);
                }

                if (field.nonEmpty)
                {
                    json.Append(',');
                    BooleanProperty(json, "nonEmpty", true);
                }

                if (field.hasMinimum)
                {
                    json.Append(',');
                    NumberProperty(json, "minimum", field.minimum);
                }

                if (field.hasMaximum)
                {
                    json.Append(',');
                    NumberProperty(json, "maximum", field.maximum);
                }

                var allowedValues = field.allowedValues ?? Array.Empty<string>();
                if (allowedValues.Length > 0)
                {
                    json.Append(',');
                    PropertyName(json, "allowedValues");
                    WriteStrings(json, allowedValues);
                }

                if (field.allowedValuesIgnoreCase)
                {
                    json.Append(',');
                    BooleanProperty(json, "allowedValuesIgnoreCase", true);
                }

                json.Append('}');
            }

            json.Append(']');
            var definitions = schema.definitions ?? Array.Empty<CommandSchemaDefinition>();
            if (definitions.Length > 0)
            {
                json.Append(',');
                PropertyName(json, "$defs");
                json.Append('{');
                for (var index = 0; index < definitions.Length; index++)
                {
                    if (index > 0)
                    {
                        json.Append(',');
                    }

                    var definition = definitions[index] ?? new CommandSchemaDefinition();
                    PropertyName(json, definition.id);
                    WriteSchema(json, definition.schema);
                }

                json.Append('}');
            }

            json.Append('}');
        }

        private static void WriteRules(
            StringBuilder json,
            CommandContractRule[] rules)
        {
            json.Append('[');
            rules ??= Array.Empty<CommandContractRule>();
            for (var index = 0; index < rules.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                var rule = rules[index] ?? new CommandContractRule();
                json.Append('{');
                StringProperty(json, "kind", rule.kind);
                json.Append(',');
                PropertyName(json, "arguments");
                WriteStrings(json, rule.arguments);
                json.Append(',');
                StringProperty(json, "whenArgument", rule.whenArgument);
                json.Append(',');
                StringProperty(json, "whenEqualsJson", rule.whenEqualsJson);
                json.Append(',');
                PropertyName(json, "requires");
                WriteStrings(json, rule.requires);
                json.Append('}');
            }

            json.Append(']');
        }

        private static void WriteStrings(StringBuilder json, string[] values)
        {
            json.Append('[');
            values ??= Array.Empty<string>();
            for (var index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(CommandContractValueEncoder.Quote(values[index]));
            }

            json.Append(']');
        }

        private static void StringProperty(
            StringBuilder json,
            string name,
            string value)
        {
            PropertyName(json, name);
            json.Append(CommandContractValueEncoder.Quote(value));
        }

        private static void BooleanProperty(
            StringBuilder json,
            string name,
            bool value)
        {
            PropertyName(json, name);
            json.Append(value ? "true" : "false");
        }

        private static void NumberProperty(
            StringBuilder json,
            string name,
            double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException(
                    $"Registry numeric field '{name}' must be finite");
            }

            PropertyName(json, name);
            json.Append(CommandContractValueEncoder.Encode(value));
        }

        private static void PropertyName(StringBuilder json, string name)
        {
            json.Append(CommandContractValueEncoder.Quote(name));
            json.Append(':');
        }
    }
}
