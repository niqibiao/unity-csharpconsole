using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    public enum CommandRuleKind
    {
        ExactlyOneOf,
        AtMostOneOf,
        AtLeastOneOf,
        AtLeastOneMutation,
        RequiresWhen
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class CommandArgumentAttribute : Attribute
    {
        public bool NonEmpty { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public string[] AllowedValues { get; set; } = Array.Empty<string>();
        public bool AllowedValuesIgnoreCase { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class CommandFieldAttribute : Attribute
    {
        public bool Optional { get; set; }
        public bool AllowNull { get; set; }
        public bool NonEmpty { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public string[] AllowedValues { get; set; } = Array.Empty<string>();
        public bool AllowedValuesIgnoreCase { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    internal sealed class CommandWireFieldAttribute : Attribute
    {
        internal string name { get; }
        internal Type schemaType { get; }

        internal CommandWireFieldAttribute(string name, Type schemaType = null)
        {
            this.name = name ?? "";
            this.schemaType = schemaType;
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class CommandRuleAttribute : Attribute
    {
        public CommandRuleKind kind { get; }
        public string[] arguments { get; }
        public string WhenArgument { get; set; } = "";
        public string WhenEqualsJson { get; set; } = "";
        public string[] Requires { get; set; } = Array.Empty<string>();

        public CommandRuleAttribute(CommandRuleKind kind, params string[] arguments)
        {
            this.kind = kind;
            this.arguments = arguments ?? Array.Empty<string>();
        }
    }
}
