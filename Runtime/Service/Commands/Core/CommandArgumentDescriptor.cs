using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    internal sealed class CommandArgumentDescriptor
    {
        public string name = "";
        public string typeName = "";
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
}
