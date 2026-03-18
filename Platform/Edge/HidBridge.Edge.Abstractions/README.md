# HidBridge.Edge.Abstractions

Stable edge-runtime contracts used by edge agents and protocol adapters.

This project intentionally contains only contracts and DTOs:

- `IEdgeCommandExecutor`
- `IEdgeDeviceHealthProbe`
- `IEdgeTelemetryPublisher`
- `IEdgeMediaPublisher`

No transport-specific implementation code lives here.
