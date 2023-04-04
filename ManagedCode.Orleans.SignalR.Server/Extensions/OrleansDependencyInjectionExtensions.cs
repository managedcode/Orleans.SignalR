using Microsoft.AspNetCore.SignalR;
using Orleans.Hosting;

namespace ManagedCode.Orleans.SignalR.Server.Extensions;

/// <summary>
///     Extension methods for configuring Redis-based scale-out for a SignalR Server in an
///     <see cref="ISignalRServerBuilder" />.
/// </summary>
public static class OrleansDependencyInjectionExtensions
{
    /// <summary>
    ///     Adds scale-out to a <see cref="ISignalRServerBuilder" />, using a shared Redis server.
    /// </summary>
    /// <param name="signalrBuilder">The <see cref="ISignalRServerBuilder" />.</param>
    /// <returns>The same instance of the <see cref="ISignalRServerBuilder" /> for chaining.</returns>
    public static ISiloBuilder AddOrleansSignalr(this ISiloBuilder siloBuilder)
    {
        return siloBuilder;
    }
}