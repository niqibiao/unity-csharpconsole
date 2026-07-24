namespace Zh1Zh1.CSharpConsole.Service
{
    public static class ConsoleServiceConfig
    {
        public const string PackageVersion = "2.0.7";
        public const int ProtocolVersion = 2;

        public static int MainThreadTimeoutMs { get; set; } = 30_000;

        /// <summary>
        /// Must be set before ConsoleHttpService is first used. The HttpClient
        /// timeout is captured once at static initialization.
        /// </summary>
        public static int HttpClientTimeoutMs { get; set; } = 30_000;
    }
}
