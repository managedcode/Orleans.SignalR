using System.IdentityModel.Tokens.Jwt;
using System.Text;
using ManagedCode.Orleans.SignalR.Client.Extensions;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp;

public class HttpHostProgram
{
    public static byte[] GetEncryptionKey()
    {
        return Encoding.ASCII.GetBytes("your_secret_key_here_your_secret_key_here");
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();

        builder.Services.Configure<OrleansSignalROptions>(options =>
        {
            options.ClientTimeoutInterval = TestDefaults.ClientTimeout;
            options.KeepMessageInterval = TestDefaults.MessageRetention;
            options.ConnectionPartitionCount = TestDefaults.ConnectionPartitions;
            options.GroupPartitionCount = TestDefaults.GroupPartitions;
        });
        builder.Services.Configure<HubOptions>(options =>
        {
            options.ClientTimeoutInterval = TestDefaults.ClientTimeout;
            options.KeepAliveInterval = TestDefaults.KeepAliveInterval;
        });

        if (builder.Environment.IsProduction())
        {
            builder.Services.AddSignalR()
                .AddOrleans();
        }
        else
        {
            builder.Services.AddSignalR(); //.AddStackExchangeRedis();
        }

        builder.Services.AddSingleton<JwtSecurityTokenHandler>();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidIssuer = "YourIssuer",
                ValidAudience = "YourAudience",
                IssuerSigningKey = new SymmetricSecurityKey(GetEncryptionKey())
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
