using System.Threading.Tasks;

namespace Zh1Zh1.CSharpConsole.Interface
{
    public interface IREPLExecutor
    {
        public Task<object> ExecuteAsync(byte[] assemblyBytes, string scriptClass);
    }

    public static class REPLExecutorLimits
    {
        public const int MAX_SUBMISSION_ID = 4096;
    }
}
