namespace HidBridge.Contracts;

/// <summary>
/// Defines the canonical message kinds used by Agent Contract v1 envelopes.
/// </summary>
public static class MessageKinds
{
    public const string AgentRegister = "agent.register";
    public const string AgentHeartbeat = "agent.heartbeat";
    public const string SessionOpen = "session.open";
    public const string SessionClose = "session.close";
    public const string CommandRequest = "command.request";
    public const string CommandAck = "command.ack";
    public const string EventTelemetry = "event.telemetry";
    public const string EventAudit = "event.audit";
}

/// <summary>
/// Defines the current capability identifiers advertised by agents and connectors.
/// </summary>
public static class CapabilityNames
{
    public const string HidMouseV1 = "hid.mouse.v1";
    public const string HidKeyboardV1 = "hid.keyboard.v1";
    public const string TransportWebRtcDataChannelV1 = "transport.webrtc.datachannel.v1";
    public const string MediaPublishV1 = "media.publish.v1";
    public const string MediaSubscribeV1 = "media.subscribe.v1";
    public const string DiagnosticsTelemetryV1 = "diag.telemetry.v1";
}
