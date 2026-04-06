using System.Reflection;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    public interface ICommandAssemblyFilter
    {
        bool ShouldScan(Assembly assembly, CommandDiscoveryOptions options);
    }
}
