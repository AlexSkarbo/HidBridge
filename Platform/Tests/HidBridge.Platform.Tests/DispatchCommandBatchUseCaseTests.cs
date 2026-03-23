using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies deterministic batch command orchestration over session dispatch path.
/// </summary>
public sealed class DispatchCommandBatchUseCaseTests
{
    /// <summary>
    /// Aggregates applied/rejected/timeout counters from per-item acknowledgements.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AggregatesAckStatuses()
    {
        var orchestrator = new RecordingOrchestrator();
        var useCase = new DispatchCommandBatchUseCase(orchestrator);

        var result = await useCase.ExecuteAsync(
            "session-1",
            new SessionCommandBatchDispatchBody(
                Commands:
                [
                    new SessionCommandBatchItemBody("cmd-1", CommandChannel.Hid, "keyboard.text", new Dictionary<string, object?>(), 3000, "idem-1"),
                    new SessionCommandBatchItemBody("cmd-2", CommandChannel.Hid, "mouse.move", new Dictionary<string, object?>(), 3000, "idem-2"),
                    new SessionCommandBatchItemBody("cmd-3", CommandChannel.Hid, "keyboard.reset", new Dictionary<string, object?>(), 3000, "idem-3"),
                ],
                TenantId: "local-tenant",
                OrganizationId: "local-org",
                OperatorRoles: ["operator.viewer"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Results.Count);
        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(1, result.TimeoutCount);
        Assert.Equal(3, orchestrator.DispatchCalls.Count);
        Assert.All(orchestrator.DispatchCalls, call => Assert.Equal("session-1", call.SessionId));
    }

    private sealed class RecordingOrchestrator : ISessionOrchestrator
    {
        public List<CommandRequestBody> DispatchCalls { get; } = [];

        public Task<CommandAckBody> DispatchAsync(CommandRequestBody request, CancellationToken cancellationToken)
        {
            DispatchCalls.Add(request);
            var status = request.CommandId switch
            {
                "cmd-1" => CommandStatus.Applied,
                "cmd-2" => CommandStatus.Rejected,
                _ => CommandStatus.Timeout,
            };
            return Task.FromResult(new CommandAckBody(request.CommandId, status));
        }

        public Task<SessionOpenBody> OpenAsync(SessionOpenBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionCloseBody> CloseAsync(SessionCloseBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<SessionParticipantBody>> ListParticipantsAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionParticipantBody> UpsertParticipantAsync(string sessionId, SessionParticipantUpsertBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionParticipantRemoveBody> RemoveParticipantAsync(string sessionId, SessionParticipantRemoveBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<SessionShareBody>> ListSharesAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> GrantShareAsync(string sessionId, SessionShareGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> RequestInvitationAsync(string sessionId, SessionInvitationRequestBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> ApproveInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> DeclineInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> AcceptShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> RejectShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionShareBody> RevokeShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionControlLeaseBody> RequestControlAsync(string sessionId, SessionControlRequestBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionControlLeaseBody> GrantControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionControlLeaseBody?> GetControlLeaseAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionControlLeaseBody?> ReleaseControlAsync(string sessionId, SessionControlReleaseBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<SessionControlLeaseBody> ForceTakeoverControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
