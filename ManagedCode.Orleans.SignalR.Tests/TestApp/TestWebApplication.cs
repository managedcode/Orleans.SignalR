using System.Collections.Concurrent;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp;

public class TestWebApplication(
    ClusterFixtureBase cluster,
    int port = 80,
    bool useOrleans = true,
    ITestOutputHelperAccessor? loggerAccessor = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<HttpHostProgram>
{
    private readonly TestCluster _cluster = cluster.Cluster;
    private readonly int _port = port;
    private readonly bool _useOrleans = useOrleans;
    private readonly Action<IServiceCollection>? _configureServices = configureServices;
    private readonly ITestOutputHelperAccessor? _loggerAccessor = loggerAccessor;

    public static ConcurrentDictionary<string, ConcurrentQueue<string>> StaticLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_useOrleans)
        {
            builder.UseEnvironment("Production");
        }
        else
        {
            builder.UseEnvironment("Development");
        }

        if (_loggerAccessor is not null)
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddProvider(new XunitLoggerProvider(_loggerAccessor));
            });
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(s =>
        {
            s.AddSingleton(_cluster.Client);
            if (_loggerAccessor is not null)
            {
                s.AddSingleton<ITestOutputHelperAccessor>(_loggerAccessor);
            }
            _configureServices?.Invoke(s);
        });
        return base.CreateHost(builder);
    }

    public HttpClient CreateHttpClient(WebApplicationFactoryClientOptions? options = null)
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
        {
            hubPath = "/" + hubPath;
        }

        var builder = new HubConnectionBuilder();
        builder.WithAutomaticReconnect();
        configure?.Invoke(builder);

        return builder.WithUrl(new Uri(baseUri, hubPath), options =>
        {
            configureConnection?.Invoke(options);
            options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
        }).Build();
    }
}
