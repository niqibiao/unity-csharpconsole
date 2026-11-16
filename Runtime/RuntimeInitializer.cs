using Zh1Zh1.CSharpConsole.Executor;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole
{
    public static class RuntimeInitializer
    {
        /// <summary>
        /// Starts the service in a development Player; call it on the main thread. The
        /// service starts by itself after the first scene loads, so call this only to
        /// start it earlier or when automatic startup is disabled. Repeated calls reuse
        /// the service.
        /// </summary>
        public static void ConsoleInitialize()
        {
            // Before the service starts: it answers on a worker thread, and the build
            // GUID may only be read from this one.
            ConsoleBuildIdentity.Capture();
            ConsoleHttpService.InitializeForRuntime(() => new REPLExecutor());
        }
    }
}
