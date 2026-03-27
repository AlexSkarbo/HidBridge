using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

public sealed class RuntimeCtlAppRoutingTests
{
    [Fact]
    public void Parse_LegacyTaskSyntax_IsRejected()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "task", "webrtc-edge-agent-acceptance",
            "--", "-StopOnFailure",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.True(snapshot.ShowHelp);
        Assert.Contains("Legacy 'task <name>' routing was removed", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(snapshot.CommandName);
    }

    [Fact]
    public void Parse_CommandAlias_RoutesAcceptanceAlias_ToNativeCommand()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "webrtc-edge-agent-acceptance",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.False(snapshot.ShowHelp);
        Assert.Null(snapshot.Error);
        Assert.Equal("webrtc-edge-agent-acceptance", snapshot.CommandName);
        Assert.Equal("WebRtcAcceptance", snapshot.CommandKind);
        Assert.Null(snapshot.ScriptRelativePath);
    }

    [Fact]
    public void Parse_UnsupportedCommand_ReturnsHelpWithError()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "unknown-command",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.True(snapshot.ShowHelp);
        Assert.Contains("Unsupported command", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(snapshot.CommandName);
    }

    [Fact]
    public async Task RunAsync_DispatchesAliasCommand_AndReturnsParseFailureForInvalidOption()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "smoke-bearer", // alias -> BearerSmokeCommand
            "-ThisOptionDoesNotExist",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();
        Assert.Equal("BearerSmoke", snapshot.CommandKind);

        var exitCode = await app.RunAsync();
        Assert.Equal(1, exitCode);
    }

    private static string ResolvePlatformRoot()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "Platform");
            if (File.Exists(Path.Combine(candidate, "run.ps1")))
            {
                return candidate;
            }

            probe = probe.Parent;
        }

        throw new InvalidOperationException("Unable to resolve Platform root for RuntimeCtl tests.");
    }
}
