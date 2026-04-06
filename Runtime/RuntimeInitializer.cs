using Zh1Zh1.CSharpConsole.Executor;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole
{
    public static class RuntimeInitializer
    {
        public static void ConsoleInitialize()
        {
            ConsoleHttpService.InitializeForRuntime(() => new REPLExecutor());
        }
    }
}
