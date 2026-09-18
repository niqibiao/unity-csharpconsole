using Zh1Zh1.CSharpConsole.Executor;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole
{
    public static class RuntimeInitializer
    {
        public static void ConsoleInitialize()
        {
            // Before the service starts: it answers on a worker thread, and the build
            // GUID may only be read from this one.
            ConsoleBuildIdentity.Capture();
            ConsoleHttpService.InitializeForRuntime(() => new REPLExecutor());
        }
    }
}
