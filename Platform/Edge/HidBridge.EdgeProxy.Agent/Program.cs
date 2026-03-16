using HidBridge.EdgeProxy.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "HIDBRIDGE_EDGE_PROXY_");
builder.Services
    .AddOptions<EdgeProxyOptions>()
    .Bind(builder.Configuration)
    .PostConfigure(options => options.Normalize())
    .ValidateDataAnnotations()
    .Validate(options => options.IsValid(out _), "Edge proxy configuration is invalid.");

builder.Services.AddHttpClient("edge-proxy")
    .ConfigureHttpClient((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<EdgeProxyOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.HttpTimeoutSec));
    });

builder.Services.AddHostedService<EdgeProxyWorker>();

var app = builder.Build();
app.Run();
