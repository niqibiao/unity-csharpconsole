using System.Runtime.CompilerServices;

// The Lite assembly was carved out of the main Runtime assembly. Its internal
// types (e.g. LiteExecuteResponseData, the wire codec helpers) were previously
// reachable from the service/editor code simply because everything shared one
// assembly. Grant the two sibling package assemblies that same access across the
// new boundary — this preserves pre-split semantics without widening the
// package's public API surface (consumers still cannot see Lite internals).
[assembly: InternalsVisibleTo("Zh1Zh1.CSharpConsole.Runtime")]
[assembly: InternalsVisibleTo("Zh1Zh1.CSharpConsole.Editor")]
