using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RSEV.Utilities.ApiBuilder;

namespace ioBroker_NewGen.Core.Web
{
    /// <summary>
    /// Übergangshilfe für bestehende native Adapter, deren Endpoint bereits
    /// gegen RSEV.Utilities.ApiBuilder implementiert ist. Der Listener bleibt
    /// trotzdem ausschließlich beim separaten Web-Adapter.
    /// </summary>
    public static class WebEndpointRegistryCompatibilityExtensions
    {
        public static bool Register(this IWebEndpointRegistry registry, string ownerId, IRuntimeEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(endpoint);

            return registry.Register(
                ownerId,
                endpoint.Route,
                WebEndpointKind.Http,
                async (request, _) =>
                {
                    var legacyRequest = new EndpointRequest
                    {
                        Route = request.Route,
                        Headers = request.Headers.ToDictionary(
                            header => header.Key,
                            header => header.Value,
                            StringComparer.OrdinalIgnoreCase),
                        Body = request.Body
                    };
                    var response = await endpoint.HandleAsync(legacyRequest).ConfigureAwait(false);
                    return new WebEndpointResponse
                    {
                        StatusCode = response.StatusCode,
                        Headers = response.Headers,
                        Body = response.Body
                    };
                });
        }
    }
}
