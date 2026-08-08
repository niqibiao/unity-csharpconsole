using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    internal enum RegistryPartition
    {
        Builtin,
        Custom
    }

    internal static class RegistryPartitionProtocol
    {
        internal static string ToWireName(RegistryPartition partition)
        {
            return partition == RegistryPartition.Custom ? "custom" : "builtin";
        }

        internal static bool IsCustom(string wireName)
        {
            return string.Equals(wireName, "custom", StringComparison.Ordinal);
        }
    }

    [Serializable]
    internal sealed class RegistryCommandWire
    {
        public string commandNamespace = "";
        public string action = "";
    }

    [Serializable]
    internal sealed class RegistryCommandRequirements
    {
        public bool editor;
        public bool mainThread;
        public bool sessionId;
    }

    [Serializable]
    internal sealed class RegistryCommandArgument
    {
        public string name = "";
        public CommandValueSchema schema = new CommandValueSchema();
        public bool required;
        public bool hasDefault;
        public string defaultJson = "";
        public bool nonEmpty;
        public bool hasMinimum;
        public double minimum;
        public bool hasMaximum;
        public double maximum;
        public string[] allowedValues = Array.Empty<string>();
        public bool allowedValuesIgnoreCase;
    }

    [Serializable]
    internal sealed class RegistryCommandContract
    {
        public string id = "";
        public RegistryCommandWire wire = new RegistryCommandWire();
        public string summary = "";
        public string partition = "builtin";
        public RegistryCommandRequirements requirements = new RegistryCommandRequirements();
        public RegistryCommandArgument[] arguments = Array.Empty<RegistryCommandArgument>();
        public CommandValueSchema result = new CommandValueSchema();
        public CommandContractRule[] rules = Array.Empty<CommandContractRule>();
    }

    [Serializable]
    internal sealed class CommandRegistryPartitionSnapshot
    {
        public bool included = true;
        public int count;
        public string fingerprint = "";
        public RegistryCommandContract[] commands = Array.Empty<RegistryCommandContract>();
    }

    [Serializable]
    internal sealed class CommandRegistrySnapshot
    {
        public int schemaVersion = CommandRegistrySnapshotBuilder.SchemaVersion;
        public string registryGeneration = "";
        public bool unchanged;
        public CommandRegistryPartitionSnapshot builtin = new CommandRegistryPartitionSnapshot();
        public CommandRegistryPartitionSnapshot custom = new CommandRegistryPartitionSnapshot();
    }

    internal static class CommandRegistrySnapshotBuilder
    {
        internal const int SchemaVersion = 1;

        internal static CommandRegistrySnapshot Build(CommandDescriptor[] descriptors)
        {
            var builtin = new List<RegistryCommandContract>();
            var custom = new List<RegistryCommandContract>();

            foreach (var descriptor in descriptors ?? Array.Empty<CommandDescriptor>())
            {
                var contract = Normalize(descriptor);
                if (RegistryPartitionProtocol.IsCustom(contract.partition))
                {
                    custom.Add(contract);
                }
                else
                {
                    builtin.Add(contract);
                }
            }

            builtin.Sort(CompareContracts);
            custom.Sort(CompareContracts);

            var builtinPartition = BuildPartition(RegistryPartition.Builtin, builtin.ToArray());
            var customPartition = BuildPartition(RegistryPartition.Custom, custom.ToArray());
            return new CommandRegistrySnapshot
            {
                schemaVersion = SchemaVersion,
                registryGeneration = ComputeGeneration(builtinPartition, customPartition),
                builtin = builtinPartition,
                custom = customPartition
            };
        }

        private static CommandRegistryPartitionSnapshot BuildPartition(
            RegistryPartition partition,
            RegistryCommandContract[] commands)
        {
            commands ??= Array.Empty<RegistryCommandContract>();
            return new CommandRegistryPartitionSnapshot
            {
                included = true,
                count = commands.Length,
                fingerprint = ComputePartitionFingerprint(partition, commands),
                commands = commands
            };
        }

        private static RegistryCommandContract Normalize(CommandDescriptor descriptor)
        {
            descriptor ??= new CommandDescriptor();
            var sourceArguments = descriptor.arguments ?? Array.Empty<CommandArgumentDescriptor>();
            var arguments = new RegistryCommandArgument[sourceArguments.Length];
            for (var index = 0; index < sourceArguments.Length; index++)
            {
                var argument = sourceArguments[index] ?? new CommandArgumentDescriptor();
                arguments[index] = new RegistryCommandArgument
                {
                    name = argument.name ?? "",
                    schema = NormalizeSchema(argument.schema),
                    required = argument.required,
                    hasDefault = argument.hasDefault,
                    defaultJson = argument.defaultJson ?? "",
                    nonEmpty = argument.nonEmpty,
                    hasMinimum = argument.hasMinimum,
                    minimum = argument.hasMinimum
                        ? NormalizeFiniteNumber(argument.minimum, argument.name, "minimum")
                        : 0,
                    hasMaximum = argument.hasMaximum,
                    maximum = argument.hasMaximum
                        ? NormalizeFiniteNumber(argument.maximum, argument.name, "maximum")
                        : 0,
                    allowedValues = NormalizeStrings(argument.allowedValues, sort: true),
                    allowedValuesIgnoreCase = argument.allowedValuesIgnoreCase
                };
            }

            var sourceRules = descriptor.rules ?? Array.Empty<CommandContractRule>();
            var rules = new CommandContractRule[sourceRules.Length];
            for (var index = 0; index < sourceRules.Length; index++)
            {
                var rule = sourceRules[index] ?? new CommandContractRule();
                rules[index] = new CommandContractRule
                {
                    kind = rule.kind ?? "",
                    arguments = NormalizeStrings(rule.arguments, sort: false),
                    whenArgument = rule.whenArgument ?? "",
                    whenEqualsJson = rule.whenEqualsJson ?? "",
                    requires = NormalizeStrings(rule.requires, sort: false)
                };
            }
            Array.Sort(rules, CommandContractRuleOrder.Compare);

            return new RegistryCommandContract
            {
                id = descriptor.id ?? "",
                wire = new RegistryCommandWire
                {
                    commandNamespace = descriptor.commandNamespace ?? "",
                    action = descriptor.action ?? ""
                },
                summary = descriptor.summary ?? "",
                partition = RegistryPartitionProtocol.IsCustom(descriptor.partition)
                    ? RegistryPartitionProtocol.ToWireName(RegistryPartition.Custom)
                    : RegistryPartitionProtocol.ToWireName(RegistryPartition.Builtin),
                requirements = new RegistryCommandRequirements
                {
                    editor = descriptor.editorOnly,
                    mainThread = descriptor.runOnMainThread,
                    sessionId = descriptor.requiresSessionId
                },
                arguments = arguments,
                result = NormalizeSchema(descriptor.result),
                rules = rules
            };
        }

        private static int CompareContracts(RegistryCommandContract left, RegistryCommandContract right)
        {
            var idComparison = string.CompareOrdinal(left?.id ?? "", right?.id ?? "");
            if (idComparison != 0)
            {
                return idComparison;
            }

            var namespaceComparison = string.CompareOrdinal(
                left?.wire?.commandNamespace ?? "",
                right?.wire?.commandNamespace ?? "");
            return namespaceComparison != 0
                ? namespaceComparison
                : string.CompareOrdinal(left?.wire?.action ?? "", right?.wire?.action ?? "");
        }

        private static string ComputePartitionFingerprint(
            RegistryPartition partition,
            RegistryCommandContract[] commands)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
                {
                    writer.Write(SchemaVersion);
                    WriteString(writer, RegistryPartitionProtocol.ToWireName(partition));
                    writer.Write(commands.Length);
                    foreach (var command in commands)
                    {
                        WriteString(writer, command.id);
                        WriteString(writer, command.wire?.commandNamespace);
                        WriteString(writer, command.wire?.action);
                        WriteString(writer, command.summary);
                        WriteString(writer, command.partition);
                        writer.Write(command.requirements?.editor ?? false);
                        writer.Write(command.requirements?.mainThread ?? false);
                        writer.Write(command.requirements?.sessionId ?? false);

                        var arguments = command.arguments ?? Array.Empty<RegistryCommandArgument>();
                        writer.Write(arguments.Length);
                        foreach (var argument in arguments)
                        {
                            WriteString(writer, argument?.name);
                            WriteSchema(writer, argument?.schema);
                            writer.Write(argument?.required ?? false);
                            writer.Write(argument?.hasDefault ?? false);
                            WriteString(writer, argument?.defaultJson);
                            writer.Write(argument?.nonEmpty ?? false);
                            writer.Write(argument?.hasMinimum ?? false);
                            if (argument?.hasMinimum ?? false)
                            {
                                writer.Write(argument.minimum);
                            }

                            writer.Write(argument?.hasMaximum ?? false);
                            if (argument?.hasMaximum ?? false)
                            {
                                writer.Write(argument.maximum);
                            }

                            WriteStrings(writer, argument?.allowedValues);
                            writer.Write(argument?.allowedValuesIgnoreCase ?? false);
                        }

                        WriteSchema(writer, command.result);
                        var rules = command.rules ?? Array.Empty<CommandContractRule>();
                        writer.Write(rules.Length);
                        foreach (var rule in rules)
                        {
                            WriteString(writer, rule?.kind);
                            WriteStrings(writer, rule?.arguments);
                            WriteString(writer, rule?.whenArgument);
                            WriteString(writer, rule?.whenEqualsJson);
                            WriteStrings(writer, rule?.requires);
                        }
                    }
                }

                return ComputeSha256(stream.ToArray());
            }
        }

        private static CommandValueSchema NormalizeSchema(CommandValueSchema source)
        {
            var normalized = NormalizeSchemaNode(source);
            ValidateSchemaReferences(normalized);
            return normalized;
        }

        private static CommandValueSchema NormalizeSchemaNode(CommandValueSchema source)
        {
            source ??= new CommandValueSchema();
            var sourceFields = source.fields ?? Array.Empty<CommandSchemaField>();
            var fields = new CommandSchemaField[sourceFields.Length];
            for (var index = 0; index < sourceFields.Length; index++)
            {
                var field = sourceFields[index] ?? new CommandSchemaField();
                fields[index] = new CommandSchemaField
                {
                    name = field.name ?? "",
                    schema = NormalizeSchemaNode(field.schema),
                    required = field.required,
                    nonEmpty = field.nonEmpty,
                    hasMinimum = field.hasMinimum,
                    minimum = field.hasMinimum
                        ? NormalizeFiniteNumber(field.minimum, field.name, "minimum")
                        : 0,
                    hasMaximum = field.hasMaximum,
                    maximum = field.hasMaximum
                        ? NormalizeFiniteNumber(field.maximum, field.name, "maximum")
                        : 0,
                    allowedValues = NormalizeStrings(field.allowedValues, sort: true),
                    allowedValuesIgnoreCase = field.allowedValuesIgnoreCase
                };
            }
            Array.Sort(
                fields,
                (left, right) => string.CompareOrdinal(left?.name ?? "", right?.name ?? ""));

            var sourceDefinitions =
                source.definitions ?? Array.Empty<CommandSchemaDefinition>();
            var definitions = new CommandSchemaDefinition[sourceDefinitions.Length];
            for (var index = 0; index < sourceDefinitions.Length; index++)
            {
                var definition = sourceDefinitions[index] ?? new CommandSchemaDefinition();
                definitions[index] = new CommandSchemaDefinition
                {
                    id = definition.id ?? "",
                    schema = NormalizeSchemaNode(definition.schema)
                };
            }
            Array.Sort(
                definitions,
                (left, right) => string.CompareOrdinal(left?.id ?? "", right?.id ?? ""));

            return new CommandValueSchema
            {
                kind = source.kind ?? "empty",
                format = source.format ?? "",
                nullable = source.nullable,
                reference = source.reference ?? "",
                items = source.items == null ? null : NormalizeSchemaNode(source.items),
                fields = fields,
                enumValues = NormalizeStrings(source.enumValues, sort: true),
                definitions = definitions
            };
        }

        private static void ValidateSchemaReferences(CommandValueSchema root)
        {
            root ??= new CommandValueSchema();
            var definitions = new Dictionary<string, CommandValueSchema>(
                StringComparer.Ordinal);
            foreach (var definition in root.definitions ?? Array.Empty<CommandSchemaDefinition>())
            {
                var id = definition?.id ?? "";
                if (string.IsNullOrEmpty(id))
                {
                    throw new InvalidOperationException(
                        "Command schema definition ids must not be empty");
                }

                if (definitions.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        $"Command schema contains duplicate definition '{id}'");
                }

                definitions.Add(
                    id,
                    definition?.schema ?? new CommandValueSchema());
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            CollectSchemaReferences(root, isRoot: true, referenced);
            var pending = new Queue<string>(referenced);
            while (pending.Count > 0)
            {
                var id = pending.Dequeue();
                if (!definitions.TryGetValue(id, out var definitionSchema))
                {
                    throw new InvalidOperationException(
                        $"Command schema contains dangling reference '{id}'");
                }

                var before = referenced.Count;
                CollectSchemaReferences(
                    definitionSchema,
                    isRoot: false,
                    referenced);
                if (referenced.Count == before)
                {
                    continue;
                }

                foreach (var referencedId in referenced)
                {
                    if (!definitions.ContainsKey(referencedId))
                    {
                        throw new InvalidOperationException(
                            $"Command schema contains dangling reference '{referencedId}'");
                    }

                    if (!string.Equals(referencedId, id, StringComparison.Ordinal))
                    {
                        pending.Enqueue(referencedId);
                    }
                }
            }

            foreach (var definitionId in definitions.Keys)
            {
                if (!referenced.Contains(definitionId))
                {
                    throw new InvalidOperationException(
                        $"Command schema contains unused definition '{definitionId}'");
                }
            }
        }

        private static void CollectSchemaReferences(
            CommandValueSchema schema,
            bool isRoot,
            HashSet<string> referenced)
        {
            schema ??= new CommandValueSchema();
            if (!isRoot
                && (schema.definitions?.Length ?? 0) > 0)
            {
                throw new InvalidOperationException(
                    "Command schema definitions are allowed only on the schema root");
            }

            if (string.Equals(schema.kind, "reference", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(schema.reference))
                {
                    throw new InvalidOperationException(
                        "Command schema reference ids must not be empty");
                }

                referenced.Add(schema.reference);
            }
            else if (!string.IsNullOrEmpty(schema.reference))
            {
                throw new InvalidOperationException(
                    $"Command schema kind '{schema.kind}' cannot declare a reference");
            }

            if (schema.items != null)
            {
                CollectSchemaReferences(schema.items, isRoot: false, referenced);
            }

            foreach (var field in schema.fields ?? Array.Empty<CommandSchemaField>())
            {
                CollectSchemaReferences(
                    field?.schema,
                    isRoot: false,
                    referenced);
            }
        }

        private static string[] NormalizeStrings(string[] values, bool sort)
        {
            values ??= Array.Empty<string>();
            var normalized = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                normalized[index] = values[index] ?? "";
            }

            if (sort)
            {
                Array.Sort(normalized, StringComparer.Ordinal);
            }

            return normalized;
        }

        private static double NormalizeFiniteNumber(
            double value,
            string argumentName,
            string fieldName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException(
                    $"Command argument '{argumentName}' has a non-finite {fieldName}");
            }

            return value == 0d ? 0d : value;
        }

        private static void WriteSchema(BinaryWriter writer, CommandValueSchema schema)
        {
            schema ??= new CommandValueSchema();
            WriteString(writer, schema.kind);
            WriteString(writer, schema.format);
            writer.Write(schema.nullable);
            WriteString(writer, schema.reference);
            writer.Write(schema.items != null);
            if (schema.items != null)
            {
                WriteSchema(writer, schema.items);
            }

            var fields = schema.fields ?? Array.Empty<CommandSchemaField>();
            writer.Write(fields.Length);
            foreach (var field in fields)
            {
                WriteString(writer, field?.name);
                WriteSchema(writer, field?.schema);
                writer.Write(field?.required ?? false);
                writer.Write(field?.nonEmpty ?? false);
                writer.Write(field?.hasMinimum ?? false);
                if (field?.hasMinimum ?? false)
                {
                    writer.Write(field.minimum);
                }

                writer.Write(field?.hasMaximum ?? false);
                if (field?.hasMaximum ?? false)
                {
                    writer.Write(field.maximum);
                }

                WriteStrings(writer, field?.allowedValues);
                writer.Write(field?.allowedValuesIgnoreCase ?? false);
            }

            WriteStrings(writer, schema.enumValues);
            var definitions =
                schema.definitions ?? Array.Empty<CommandSchemaDefinition>();
            writer.Write(definitions.Length);
            foreach (var definition in definitions)
            {
                WriteString(writer, definition?.id);
                WriteSchema(writer, definition?.schema);
            }
        }

        private static void WriteStrings(BinaryWriter writer, string[] values)
        {
            values ??= Array.Empty<string>();
            writer.Write(values.Length);
            foreach (var value in values)
            {
                WriteString(writer, value);
            }
        }

        private static string ComputeGeneration(
            CommandRegistryPartitionSnapshot builtin,
            CommandRegistryPartitionSnapshot custom)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
                {
                    writer.Write(SchemaVersion);
                    writer.Write(builtin?.count ?? 0);
                    WriteString(writer, builtin?.fingerprint);
                    writer.Write(custom?.count ?? 0);
                    WriteString(writer, custom?.fingerprint);
                }

                return ComputeSha256(stream.ToArray());
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(bytes ?? Array.Empty<byte>());
                var text = new StringBuilder(digest.Length * 2);
                foreach (var value in digest)
                {
                    text.Append(value.ToString("x2"));
                }

                return text.ToString();
            }
        }
    }
}
