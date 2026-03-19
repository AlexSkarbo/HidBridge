using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using System.Globalization;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Aggregates transport SLO diagnostics over relay command history and live relay peer metadata.
/// </summary>
public sealed class TransportSloDiagnosticsService
{
    private readonly ISessionStore _sessionStore;
    private readonly ICommandJournalStore _commandJournalStore;
    private readonly WebRtcCommandRelayService _relayService;
    private readonly TransportSloDiagnosticsOptions _options;

    /// <summary>
    /// Creates the transport SLO diagnostics service.
    /// </summary>
    public TransportSloDiagnosticsService(
        ISessionStore sessionStore,
        ICommandJournalStore commandJournalStore,
        WebRtcCommandRelayService relayService,
        TransportSloDiagnosticsOptions options)
    {
        _sessionStore = sessionStore;
        _commandJournalStore = commandJournalStore;
        _relayService = relayService;
        _options = options;
    }

    /// <summary>
    /// Builds one transport SLO summary.
    /// </summary>
    public async Task<TransportSloSummaryReadModel> GetSummaryAsync(
        ApiCallerContext caller,
        string? sessionId,
        int? windowMinutes,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var effectiveWindowMinutes = Math.Clamp(windowMinutes ?? _options.DefaultWindowMinutes, 5, 24 * 60);
        var windowStartUtc = nowUtc.AddMinutes(-effectiveWindowMinutes);

        var allSessions = await _sessionStore.ListAsync(cancellationToken);
        var selectedSessions = await ResolveVisibleSessionsAsync(allSessions, caller, sessionId, cancellationToken);
        var visibleSessionIds = selectedSessions
            .Select(static snapshot => snapshot.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var relayEntries = (await _commandJournalStore.ListAsync(cancellationToken))
            .Where(entry => visibleSessionIds.Contains(entry.SessionId))
            .Where(entry => ResolveCommandTimestamp(entry) >= windowStartUtc)
            .Where(IsRelayCommandEntry)
            .ToArray();

        var relayCommandCount = relayEntries.Length;
        var relayTimeoutCount = relayEntries.Count(static entry => entry.Status == CommandStatus.Timeout);
        var ackTimeoutRate = relayCommandCount == 0
            ? 0d
            : relayTimeoutCount / (double)relayCommandCount;

        var relayAckRoundtripMs = relayEntries
            .Select(entry => ReadDouble(entry.Metrics, "relayAdapterWsRoundtripMs"))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .OrderBy(static value => value)
            .ToArray();

        var sessionRows = new List<TransportSloSessionReadModel>(selectedSessions.Count);
        foreach (var snapshot in selectedSessions)
        {
            var relayMetrics = await _relayService.GetSessionMetricsAsync(snapshot.SessionId, snapshot.EndpointId, cancellationToken);
            var onlinePeerCount = ReadInt32(relayMetrics, "onlinePeerCount");
            var reconnectTransitionCount = ReadInt32(relayMetrics, "reconnectTransitionCount");
            var firstPeerSeenAtUtc = ReadDateTimeOffset(relayMetrics, "firstPeerSeenAtUtc");
            var sessionReconnectFrequency = ComputeReconnectFrequencyPerHour(reconnectTransitionCount, firstPeerSeenAtUtc, nowUtc);

            sessionRows.Add(new TransportSloSessionReadModel(
                SessionId: snapshot.SessionId,
                EndpointId: snapshot.EndpointId,
                OnlinePeerCount: onlinePeerCount,
                LastPeerState: ReadString(relayMetrics, "lastPeerState"),
                LastPeerSeenAtUtc: ReadDateTimeOffset(relayMetrics, "lastPeerSeenAtUtc"),
                LastRelayAckAtUtc: ReadDateTimeOffset(relayMetrics, "lastRelayAckAtUtc"),
                RelayReadyLatencyMs: ReadDouble(relayMetrics, "relayReadyLatencyMs"),
                ReconnectTransitionCount: reconnectTransitionCount,
                ReconnectFrequencyPerHour: sessionReconnectFrequency));
        }

        var reconnectFrequencyPerHour = sessionRows.Count == 0
            ? 0d
            : sessionRows.Average(static row => row.ReconnectFrequencyPerHour);
        var relayReadyLatencies = sessionRows
            .Select(static row => row.RelayReadyLatencyMs)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .OrderBy(static value => value)
            .ToArray();

        var alerts = EvaluateAlerts(
            ackTimeoutRate,
            Quantile(relayReadyLatencies, 0.95d),
            reconnectFrequencyPerHour);

        return new TransportSloSummaryReadModel(
            GeneratedAtUtc: nowUtc,
            WindowStartUtc: windowStartUtc,
            WindowMinutes: effectiveWindowMinutes,
            SessionCount: selectedSessions.Count,
            RelayCommandCount: relayCommandCount,
            RelayTimeoutCount: relayTimeoutCount,
            AckTimeoutRate: ackTimeoutRate,
            RelayReadyLatencyP50Ms: Quantile(relayReadyLatencies, 0.50d),
            RelayReadyLatencyP95Ms: Quantile(relayReadyLatencies, 0.95d),
            ReconnectFrequencyPerHour: reconnectFrequencyPerHour,
            Status: ResolveStatus(alerts),
            Alerts: alerts,
            Sessions: sessionRows
                .OrderByDescending(static row => row.ReconnectFrequencyPerHour)
                .ThenBy(static row => row.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Thresholds: new TransportSloThresholdsReadModel(
                RelayReadyLatencyWarnMs: _options.RelayReadyLatencyWarnMs,
                RelayReadyLatencyCriticalMs: _options.RelayReadyLatencyCriticalMs,
                AckTimeoutRateWarn: _options.AckTimeoutRateWarn,
                AckTimeoutRateCritical: _options.AckTimeoutRateCritical,
                ReconnectFrequencyWarnPerHour: _options.ReconnectFrequencyWarnPerHour,
                ReconnectFrequencyCriticalPerHour: _options.ReconnectFrequencyCriticalPerHour));
    }

    /// <summary>
    /// Resolves sessions visible to the caller, optionally narrowed by one session id.
    /// </summary>
    private static Task<IReadOnlyList<SessionSnapshot>> ResolveVisibleSessionsAsync(
        IReadOnlyList<SessionSnapshot> sessions,
        ApiCallerContext caller,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var snapshot = sessions.FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
            if (caller.IsPresent)
            {
                caller.EnsureSessionScope(snapshot);
            }

            return Task.FromResult<IReadOnlyList<SessionSnapshot>>([snapshot]);
        }

        var filtered = sessions
            .Where(snapshot => caller.CanAccessSession(snapshot))
            .OrderByDescending(static snapshot => snapshot.UpdatedAtUtc)
            .ToArray();
        return Task.FromResult<IReadOnlyList<SessionSnapshot>>(filtered);
    }

    /// <summary>
    /// Returns true when one command journal entry belongs to relay transport path.
    /// </summary>
    private static bool IsRelayCommandEntry(CommandJournalEntryBody entry)
    {
        if (ReadDouble(entry.Metrics, "transportRelayMode") is > 0)
        {
            return true;
        }

        if (ReadDouble(entry.Metrics, "transportBridgeMode") is > 0)
        {
            return true;
        }

        if (ReadDouble(entry.Metrics, "relayAdapterWsRoundtripMs").HasValue)
        {
            return true;
        }

        if (entry.Args.TryGetValue("transportProvider", out var providerRaw)
            && providerRaw is not null
            && RealtimeTransportProviderParser.TryParse(providerRaw.ToString(), out var provider))
        {
            return provider == RealtimeTransportProvider.WebRtcDataChannel;
        }

        return false;
    }

    /// <summary>
    /// Resolves one command timestamp used for diagnostics windows.
    /// </summary>
    private static DateTimeOffset ResolveCommandTimestamp(CommandJournalEntryBody entry)
        => entry.CompletedAtUtc ?? entry.CreatedAtUtc;

    /// <summary>
    /// Calculates reconnect frequency for one session.
    /// </summary>
    private static double ComputeReconnectFrequencyPerHour(
        int reconnectTransitionCount,
        DateTimeOffset? firstPeerSeenAtUtc,
        DateTimeOffset nowUtc)
    {
        if (reconnectTransitionCount <= 0 || firstPeerSeenAtUtc is null)
        {
            return 0d;
        }

        var elapsedHours = Math.Max(1d / 60d, (nowUtc - firstPeerSeenAtUtc.Value).TotalHours);
        return reconnectTransitionCount / elapsedHours;
    }

    /// <summary>
    /// Evaluates SLO alerts from aggregate values.
    /// </summary>
    private IReadOnlyList<TransportSloAlertReadModel> EvaluateAlerts(
        double ackTimeoutRate,
        double? relayReadyLatencyP95Ms,
        double reconnectFrequencyPerHour)
    {
        var alerts = new List<TransportSloAlertReadModel>(capacity: 3);

        AppendAlert(
            alerts,
            metric: "ack_timeout_rate",
            observed: ackTimeoutRate,
            warnThreshold: _options.AckTimeoutRateWarn,
            criticalThreshold: _options.AckTimeoutRateCritical,
            messageTemplate: "ACK timeout rate is {0:P1}, exceeding {1} threshold {2:P1}.");

        if (relayReadyLatencyP95Ms.HasValue)
        {
            AppendAlert(
                alerts,
                metric: "relay_ready_latency_p95_ms",
                observed: relayReadyLatencyP95Ms.Value,
                warnThreshold: _options.RelayReadyLatencyWarnMs,
                criticalThreshold: _options.RelayReadyLatencyCriticalMs,
                messageTemplate: "Relay-ready p95 latency is {0:N0}ms, exceeding {1} threshold {2:N0}ms.");
        }

        AppendAlert(
            alerts,
            metric: "reconnect_frequency_per_hour",
            observed: reconnectFrequencyPerHour,
            warnThreshold: _options.ReconnectFrequencyWarnPerHour,
            criticalThreshold: _options.ReconnectFrequencyCriticalPerHour,
            messageTemplate: "Reconnect frequency is {0:N2}/h, exceeding {1} threshold {2:N2}/h.");

        return alerts
            .OrderByDescending(static alert => ResolveSeverityRank(alert.Severity))
            .ThenBy(static alert => alert.Metric, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Appends warning or critical alert when observed metric crosses configured thresholds.
    /// </summary>
    private static void AppendAlert(
        ICollection<TransportSloAlertReadModel> alerts,
        string metric,
        double observed,
        double warnThreshold,
        double criticalThreshold,
        string messageTemplate)
    {
        string? severity = null;
        double threshold = 0d;
        if (observed >= criticalThreshold)
        {
            severity = "critical";
            threshold = criticalThreshold;
        }
        else if (observed >= warnThreshold)
        {
            severity = "warning";
            threshold = warnThreshold;
        }

        if (severity is null)
        {
            return;
        }

        var message = string.Format(
            CultureInfo.InvariantCulture,
            messageTemplate,
            observed,
            severity,
            threshold);
        alerts.Add(new TransportSloAlertReadModel(metric, severity, observed, threshold, message));
    }

    /// <summary>
    /// Resolves overall status from alerts.
    /// </summary>
    private static string ResolveStatus(IReadOnlyList<TransportSloAlertReadModel> alerts)
    {
        if (alerts.Any(static alert => string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase)))
        {
            return "critical";
        }

        if (alerts.Any(static alert => string.Equals(alert.Severity, "warning", StringComparison.OrdinalIgnoreCase)))
        {
            return "warning";
        }

        return "ok";
    }

    /// <summary>
    /// Computes one quantile over sorted values.
    /// </summary>
    private static double? Quantile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var normalized = Math.Clamp(percentile, 0d, 1d);
        var rank = normalized * (sortedValues.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
        {
            return sortedValues[lowerIndex];
        }

        var weight = rank - lowerIndex;
        return sortedValues[lowerIndex] + ((sortedValues[upperIndex] - sortedValues[lowerIndex]) * weight);
    }

    private static int ResolveSeverityRank(string severity)
        => severity switch
        {
            "critical" => 2,
            "warning" => 1,
            _ => 0,
        };

    private static string? ReadString(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            _ => value.ToString(),
        };
    }

    private static int ReadInt32(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        var value = ReadDouble(metrics, key);
        return value.HasValue
            ? (int)Math.Round(value.Value, MidpointRounding.AwayFromZero)
            : 0;
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, double>? metrics, string key)
        => metrics is null
            ? null
            : metrics.TryGetValue(key, out var value)
                ? value
                : null;

    private static double? ReadDouble(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            decimal number => (double)number,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFallback) ? parsedFallback : null,
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedFallback) ? parsedFallback : null,
        };
    }
}
