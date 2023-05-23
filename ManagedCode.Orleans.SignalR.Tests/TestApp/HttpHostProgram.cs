using System.Text;
using ManagedCode.Orleans.SignalR.Client.Extensions;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp;

public class HttpHostProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        builder.Services.AddControllers();

        // builder.Host.UseOrleans(builder =>
        // {
        //     builder.UseLocalhostClustering();
        //     new TestSiloConfigurations().Configure(builder);
        // });
        
        if (builder.Environment.IsProduction())
            builder.Services
                .AddSignalR()
                .AddOrleans();
        else
            builder.Services
                .AddSignalR()
                .AddStackExchangeRedis();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false;
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("your_secret_key_here")),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        context.Token = context.Request.Query["access_token"];
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireAuthenticatedUser", policy => { policy.RequireAuthenticatedUser(); });
        });


        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseCookiePolicy();
        app.MapControllers();
        app.MapHub<SimpleTestHub>(nameof(SimpleTestHub));
        app.MapHub<InterfaceTestHub>(nameof(InterfaceTestHub)); 
        app.MapHub<StressTestHub>(nameof(StressTestHub));
        
        app.Run();
    }
}