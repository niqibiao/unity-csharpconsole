using System;

namespace Zh1Zh1.CSharpConsole.Lite
{
    public sealed class LiteWireException : Exception
    {
        public string ErrorCode { get; }

        public LiteWireException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public LiteWireException(string errorCode, string message, Exception inner)
            : base(message, inner)
        {
            ErrorCode = errorCode;
        }
    }
}
