using System.Net;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Service.Internal;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Endpoints
{
    internal sealed class HealthEndpointHandler
    {
        private readonly ConsoleHttpServiceDependencies _dependencies;

        public HealthEndpointHandler(ConsoleHttpServiceDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task Handle(HttpListenerContext context)
        {
            var health = _dependencies.BuildHealthResponseSnapshot();
            var envelope = _dependencies.EnvelopeFactory.CreateEnvelope(
                true,
                "bootstrap",
                "ok",
                "Service is healthy",
                "",
                JsonUtility.ToJson(health));
            await _dependencies.WriteEnvelopeResponseAsync(context, envelope, "Health");
        }
    }
}
