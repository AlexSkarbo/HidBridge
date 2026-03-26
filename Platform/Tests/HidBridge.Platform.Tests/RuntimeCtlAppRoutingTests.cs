using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

public sealed class RuntimeCtlAppRoutingTests
{
    [Fact]
    public void Parse_TaskRedirectsLegacyAcceptanceAlias_ToNativeCommand()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "task", "webrtc-edge-agent-acceptance",
            "--", "-StopOnFailure",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.False(snapshot.ShowHelp);
        Assert.Null(snapshot.Error);
        Assert.Equal("webrtc-acceptance", snapshot.CommandName);
        Assert.Equal("WebRtcAcceptance", snapshot.CommandKind);
        Assert.Equal(["-StopOnFailure"], snapshot.ForwardArgs);
    }

    [Fact]
    public void Parse_TaskKeepsScriptBridge_ForNotYetMigratedTask()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "task", "bearer-rollout",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.False(snapshot.ShowHelp);
        Assert.Null(snapshot.Error);
        Assert.Equal("task bearer-rollout", snapshot.CommandName);
        Assert.Equal("ScriptBridge", snapshot.CommandKind);
        Assert.Equal("Scripts/run_bearer_rollout_phase.ps1", snapshot.ScriptRelativePath);
    }

    [Fact]
    public void Parse_UnsupportedTask_ReturnsHelpWithError()
    {
        var platformRoot = ResolvePlatformRoot();

        var app = RuntimeCtlApp.Parse(
        [
            "--platform-root", platformRoot,
            "task", "unknown-task",
        ]);

        var snapshot = app.GetDiagnosticsSnapshot();

        Assert.True(snapshot.ShowHelp);
        Assert.Contains("Unsupported task", snapshot.Error, StringComparison.OrdinalIgnoreCase);
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
