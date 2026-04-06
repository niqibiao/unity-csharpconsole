using System.Threading.Tasks;

namespace Zh1Zh1.CSharpConsole.Interface
{
    public interface IREPLExecutor
    {
        const int MAX_SUBMISSION_ID = 4096;

        public Task<object> ExecuteAsync(byte[] assemblyBytes, string scriptClass);
    }
}
