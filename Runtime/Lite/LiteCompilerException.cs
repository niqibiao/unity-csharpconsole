using System;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Carries a stable error code so HTTP layer / test harness / REPL client
    // can branch on the failure kind without parsing the message. Mirrors the
    // shape of LiteWireException, which serves the same purpose on the Player
    // wire path. Lives in Runtime so ConsoleHttpService can catch it directly
    // without crossing the Runtime↔Editor asmdef boundary.
    public sealed class LiteCompilerException : Exception
    {
        public string ErrorCode { get; }
        public LiteCompilerException(string code, string message) : base(message) { ErrorCode = code; }
    }
}
