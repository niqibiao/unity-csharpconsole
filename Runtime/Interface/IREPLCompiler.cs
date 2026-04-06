namespace Zh1Zh1.CSharpConsole.Interface
{
    public interface IREPLCompiler
    {
        public (byte[] assemblyBytes, string scriptClass, string errorMsg) Compile(string code, string defines, string defaultUsing);
    }
}