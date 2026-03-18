using HidBridge.EdgeProxy.Agent;
using HidBridge.Edge.Abstractions;
using HidBridge.Edge.HidBridgeProtocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

builder.Services.AddSingleton<IEdgeCommandExecutor>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<EdgeProxyOptions>>().Value;
    var executorKind = options.GetCommandExecutorKind();
    return executorKind switch
    {
        EdgeProxyCommandExecutorKind.ControlWs => new ControlWsCommandExecutor(
            options.ControlWsUrl,
            serviceProvider.GetRequiredService<ILogger<ControlWsCommandExecutor>>()),
        EdgeProxyCommandExecutorKind.UartHid => new UartHidCommandExecutor(
            new UartHidCommandExecutorOptions(
                PortName: options.UartPort,
                BaudRate: options.UartBaud,
                HmacKey: options.UartHmacKey,
                MasterSecret: options.UartMasterSecret,
                MouseInterfaceSelector: (byte)options.UartMouseInterfaceSelector,
                KeyboardInterfaceSelector: (byte)options.UartKeyboardInterfaceSelector,
                CommandTimeoutMs: options.UartCommandTimeoutMs,
                InjectTimeoutMs: options.UartInjectTimeoutMs,
                InjectRetries: options.UartInjectRetries,
                ReleasePortAfterExecute: options.UartReleasePortAfterExecute),
            serviceProvider.GetRequiredService<ILogger<UartHidCommandExecutor>>()),
        _ => throw new InvalidOperationException($"Unsupported command executor kind '{executorKind}'."),
    };
});

builder.Services.AddHostedService<EdgeProxyWorker>();

var app = builder.Build();
app.Run();
