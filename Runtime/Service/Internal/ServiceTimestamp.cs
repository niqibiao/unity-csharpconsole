using System.Diagnostics;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal static class ServiceTimestamp
    {
        private static readonly Stopwatch s_Stopwatch = Stopwatch.StartNew();

        public static double Now()
        {
            return s_Stopwatch.Elapsed.TotalSeconds;
        }
    }
}
