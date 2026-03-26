using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Implements the <c>CloseStaleRoomsCommand</c> RuntimeCtl lane.
/// Execution contract:
/// 1) Parse CLI arguments into strongly typed options.
/// 2) Execute lane-specific orchestration (native RuntimeCtl commands first, script bridge only when explicitly needed).
/// 3) Persist step logs/summaries under <c>Platform/.logs</c> and return process-style exit code semantics (0 = success).
/// </summary>
internal static class CloseStaleRoomsCommand
{
    public static Task<int> RunAsync(string platformRoot, IReadOnlyList<string> args) =>
        RoomCleanupCommand.RunAsync(
            args,
            new RoomCleanupCommand.Mode(
                SummaryTitle: "Close Stale Rooms",
                FoundLabel: "Stale sessions found",
                EndpointPath: "/api/v1/sessions/actions/close-stale",
                DefaultPrincipalId: "stale-room-cleanup",
                DefaultReason: "manual stale-room cleanup",
                SupportsStaleAfterMinutes: true));
}
