using System.Collections.Concurrent;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp;

public class TestWebApplication : WebApplicationFactory<HttpHostProgram>
{
    private readonly TestCluster _cluster;
    private readonly int _port;
    private readonly bool _useOrleans;

    public TestWebApplication(ClusterFixtureBase cluster, int port = 80, bool useOrleans = true)
    {
        _port = port;
        _useOrleans = useOrleans;
        _cluster = cluster.Cluster;
    }

    public static ConcurrentDictionary<string, ConcurrentQueue<string>> StaticLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_useOrleans)
            builder.UseEnvironment("Production");
        else
            builder.UseEnvironment("Development");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(s => { s.AddSingleton(_cluster.Client); });
        return base.CreateHost(builder);
    }

    public HttpClient CreateHttpClient(WebApplicationFactoryClientOptions options = null)
    {
        options ??= new WebApplicationFactoryClientOptions();
        options.BaseAddress = new Uri($"http://localhost:{_port}");
        var client = CreateClient(options);
        return client;
    }


    public HubConnection CreateSignalRClient(string hubPath, Action<HubConnectionBuilder>? configure = null,
        Action<HttpConnectionOptions>? configureConnection = null)
    {
        using var client = Server.CreateClient();
        var baseUri = client.BaseAddress ?? new Uri($"http://localhost:{_port}");
        if (!hubPath.StartsWith('/'))
            hubPath = "/" + hubPath;

        var builder = new HubConnectionBuilder();
        builder.WithAutomaticReconnect();
        configure?.Invoke(builder);

        return builder.WithUrl(new Uri(baseUri, hubPath), HttpTransportType.LongPolling, options =>
        {
            configureConnection?.Invoke(options);
            options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
        }).Build();
    }
}
