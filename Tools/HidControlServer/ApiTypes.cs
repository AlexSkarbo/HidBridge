using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HidControl.Contracts;
using HidControlServer.Services;

namespace HidControlServer;

/// <summary>
/// API contract model for Hex.
/// </summary>
public static class Hex
{
    /// <summary>
    /// Parses parse.
    /// </summary>
    /// <param name="hex">The hex.</param>
    /// <returns>Result.</returns>
    public static byte[] Parse(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        hex = hex.Replace(" ", "", StringComparison.Ordinal);
        if (hex.Length % 2 != 0) throw new FormatException("Hex length must be even.");

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Executes Format.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>Result.</returns>
    public static string Format(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(data);
    }
}

/// <summary>
/// API contract model for Options.
/// </summary>
/// <param name="SerialPort">SerialPort.</param>
/// <param name="SerialAuto">SerialAuto.</param>
/// <param name="SerialMatch">SerialMatch.</param>
/// <param name="Baud">Baud.</param>
/// <param name="Url">Url.</param>
/// <param name="BindAll">BindAll.</param>
/// <param name="MouseMappingDb">MouseMappingDb.</param>
/// <param name="PgConnectionString">PgConnectionString.</param>
/// <param name="MigrateSqliteToPg">MigrateSqliteToPg.</param>
/// <param name="VideoSources">VideoSources.</param>
/// <param name="VideoProfiles">VideoProfiles.</param>
/// <param name="ActiveVideoProfile">ActiveVideoProfile.</param>
/// <param name="MouseReportLen">MouseReportLen.</param>
/// <param name="InjectQueueCapacity">InjectQueueCapacity.</param>
/// <param name="InjectDropThreshold">InjectDropThreshold.</param>
/// <param name="InjectTimeoutMs">InjectTimeoutMs.</param>
/// <param name="InjectRetries">InjectRetries.</param>
/// <param name="MouseMoveTimeoutMs">MouseMoveTimeoutMs.</param>
/// <param name="MouseMoveDropIfBusy">MouseMoveDropIfBusy.</param>
/// <param name="MouseMoveAllowZero">MouseMoveAllowZero.</param>
/// <param name="MouseWheelTimeoutMs">MouseWheelTimeoutMs.</param>
/// <param name="MouseWheelDropIfBusy">MouseWheelDropIfBusy.</param>
/// <param name="MouseWheelAllowZero">MouseWheelAllowZero.</param>
/// <param name="KeyboardInjectTimeoutMs">KeyboardInjectTimeoutMs.</param>
/// <param name="KeyboardInjectRetries">KeyboardInjectRetries.</param>
/// <param name="MasterSecret">MasterSecret.</param>
/// <param name="MouseItfSel">MouseItfSel.</param>
/// <param name="KeyboardItfSel">KeyboardItfSel.</param>
/// <param name="MouseTypeName">MouseTypeName.</param>
/// <param name="KeyboardTypeName">KeyboardTypeName.</param>
/// <param name="DevicesAutoMuteLogs">DevicesAutoMuteLogs.</param>
/// <param name="DevicesAutoRefreshMs">DevicesAutoRefreshMs.</param>
/// <param name="DevicesIncludeReportDesc">DevicesIncludeReportDesc.</param>
/// <param name="DevicesCachePath">DevicesCachePath.</param>
/// <param name="UartHmacKey">UartHmacKey.</param>
/// <param name="MouseLeftMask">MouseLeftMask.</param>
/// <param name="MouseRightMask">MouseRightMask.</param>
/// <param name="MouseMiddleMask">MouseMiddleMask.</param>
/// <param name="MouseBackMask">MouseBackMask.</param>
/// <param name="MouseForwardMask">MouseForwardMask.</param>
/// <param name="Token">Token.</param>
/// <param name="FfmpegPath">FfmpegPath.</param>
/// <param name="FfmpegAutoStart">FfmpegAutoStart.</param>
/// <param name="FfmpegWatchdogEnabled">FfmpegWatchdogEnabled.</param>
/// <param name="FfmpegWatchdogIntervalMs">FfmpegWatchdogIntervalMs.</param>
/// <param name="FfmpegRestartDelayMs">FfmpegRestartDelayMs.</param>
/// <param name="FfmpegMaxRestarts">FfmpegMaxRestarts.</param>
/// <param name="FfmpegRestartWindowMs">FfmpegRestartWindowMs.</param>
/// <param name="VideoRtspPort">VideoRtspPort.</param>
/// <param name="VideoSrtBasePort">VideoSrtBasePort.</param>
/// <param name="VideoSrtLatencyMs">VideoSrtLatencyMs.</param>
/// <param name="VideoRtmpPort">VideoRtmpPort.</param>
/// <param name="VideoHlsDir">VideoHlsDir.</param>
/// <param name="VideoLlHls">VideoLlHls.</param>
/// <param name="VideoHlsSegmentSeconds">VideoHlsSegmentSeconds.</param>
/// <param name="VideoHlsListSize">VideoHlsListSize.</param>
/// <param name="VideoHlsDeleteSegments">VideoHlsDeleteSegments.</param>
/// <param name="VideoLogDir">VideoLogDir.</param>
/// <param name="VideoCaptureAutoStopFfmpeg">VideoCaptureAutoStopFfmpeg.</param>
/// <param name="VideoCaptureRetryCount">VideoCaptureRetryCount.</param>
/// <param name="VideoCaptureRetryDelayMs">VideoCaptureRetryDelayMs.</param>
/// <param name="VideoCaptureLockTimeoutMs">VideoCaptureLockTimeoutMs.</param>
/// <param name="VideoSecondaryCaptureEnabled">VideoSecondaryCaptureEnabled.</param>
/// <param name="VideoKillOrphanFfmpeg">VideoKillOrphanFfmpeg.</param>
/// <param name="VideoFlvBufferBytes">VideoFlvBufferBytes.</param>
/// <param name="VideoFlvReadBufferBytes">VideoFlvReadBufferBytes.</param>
/// <param name="VideoMjpegReadBufferBytes">VideoMjpegReadBufferBytes.</param>
/// <param name="VideoMjpegMaxFrameBytes">VideoMjpegMaxFrameBytes.</param>
/// <param name="VideoModesCacheTtlSeconds">VideoModesCacheTtlSeconds.</param>
/// <param name="DevicesCacheMaxAgeSeconds">DevicesCacheMaxAgeSeconds.</param>
/// <param name="ServerEventLogMaxEntries">ServerEventLogMaxEntries.</param>
/// <param name="WebRtcControlPeerAutoStart">WebRtcControlPeerAutoStart.</param>
/// <param name="WebRtcControlPeerRoom">WebRtcControlPeerRoom.</param>
/// <param name="WebRtcControlPeerStun">WebRtcControlPeerStun.</param>
/// <param name="WebRtcVideoPeerAutoStart">WebRtcVideoPeerAutoStart.</param>
/// <param name="WebRtcVideoPeerRoom">WebRtcVideoPeerRoom.</param>
/// <param name="WebRtcVideoPeerStun">WebRtcVideoPeerStun.</param>
/// <param name="WebRtcVideoPeerSourceMode">WebRtcVideoPeerSourceMode.</param>
/// <param name="WebRtcVideoPeerQualityPreset">WebRtcVideoPeerQualityPreset.</param>
/// <param name="WebRtcVideoPeerCaptureInput">WebRtcVideoPeerCaptureInput.</param>
/// <param name="WebRtcVideoPeerFfmpegArgs">WebRtcVideoPeerFfmpegArgs.</param>
/// <param name="WebRtcRoomsCleanupIntervalSeconds">WebRtcRoomsCleanupIntervalSeconds.</param>
/// <param name="WebRtcRoomIdleStopSeconds">WebRtcRoomIdleStopSeconds.</param>
/// <param name="WebRtcRoomsMaxHelpers">WebRtcRoomsMaxHelpers.</param>
/// <param name="WebRtcTurnUrls">WebRtcTurnUrls.</param>
/// <param name="WebRtcTurnSharedSecret">WebRtcTurnSharedSecret.</param>
/// <param name="WebRtcTurnTtlSeconds">WebRtcTurnTtlSeconds.</param>
/// <param name="WebRtcTurnUsername">WebRtcTurnUsername.</param>
/// <param name="WebRtcClientJoinTimeoutMs">WebRtcClientJoinTimeoutMs.</param>
/// <param name="WebRtcClientConnectTimeoutMs">WebRtcClientConnectTimeoutMs.</param>
/// <param name="WebRtcRoomsPersistEnabled">WebRtcRoomsPersistEnabled.</param>
/// <param name="WebRtcRoomsStorePath">WebRtcRoomsStorePath.</param>
/// <param name="WebRtcRoomsPersistTtlSeconds">WebRtcRoomsPersistTtlSeconds.</param>
public sealed record Options(
    string SerialPort,
    bool SerialAuto,
    string? SerialMatch,
    int Baud,
    string Url,
    bool BindAll,
    string MouseMappingDb,
    string PgConnectionString,
    bool MigrateSqliteToPg,
    IReadOnlyList<VideoSourceConfig> VideoSources,
    IReadOnlyList<VideoProfileConfig> VideoProfiles,
    string ActiveVideoProfile,
    int MouseReportLen,
    int InjectQueueCapacity,
    int InjectDropThreshold,
    int InjectTimeoutMs,
    int InjectRetries,
    int MouseMoveTimeoutMs,
    bool MouseMoveDropIfBusy,
    bool MouseMoveAllowZero,
    int MouseWheelTimeoutMs,
    bool MouseWheelDropIfBusy,
    bool MouseWheelAllowZero,
    int KeyboardInjectTimeoutMs,
    int KeyboardInjectRetries,
    string MasterSecret,
    byte MouseItfSel,
    byte KeyboardItfSel,
    string? MouseTypeName,
    string? KeyboardTypeName,
    bool DevicesAutoMuteLogs,
    int DevicesAutoRefreshMs,
    bool DevicesIncludeReportDesc,
    string DevicesCachePath,
    string UartHmacKey,
    byte MouseLeftMask,
    byte MouseRightMask,
    byte MouseMiddleMask,
    byte MouseBackMask,
    byte MouseForwardMask,
    string? Token,
    string FfmpegPath,
    bool FfmpegAutoStart,
    bool FfmpegWatchdogEnabled,
    int FfmpegWatchdogIntervalMs,
    int FfmpegRestartDelayMs,
    int FfmpegMaxRestarts,
    int FfmpegRestartWindowMs,
    int VideoRtspPort,
    int VideoSrtBasePort,
    int VideoSrtLatencyMs,
    int VideoRtmpPort,
    string VideoHlsDir,
    bool VideoLlHls,
    int VideoHlsSegmentSeconds,
    int VideoHlsListSize,
    bool VideoHlsDeleteSegments,
    string VideoLogDir,
    bool VideoCaptureAutoStopFfmpeg,
    int VideoCaptureRetryCount,
    int VideoCaptureRetryDelayMs,
    int VideoCaptureLockTimeoutMs,
    bool VideoSecondaryCaptureEnabled,
    bool VideoKillOrphanFfmpeg,
    int VideoFlvBufferBytes,
    int VideoFlvReadBufferBytes,
    int VideoMjpegReadBufferBytes,
    int VideoMjpegMaxFrameBytes,
    int VideoModesCacheTtlSeconds,
    int DevicesCacheMaxAgeSeconds,
    int ServerEventLogMaxEntries,
    bool WebRtcControlPeerAutoStart,
    string WebRtcControlPeerRoom,
    string WebRtcControlPeerStun,
    bool WebRtcVideoPeerAutoStart,
    string WebRtcVideoPeerRoom,
    string WebRtcVideoPeerStun,
    string WebRtcVideoPeerSourceMode,
    string WebRtcVideoPeerQualityPreset,
    string? WebRtcVideoPeerCaptureInput,
    string? WebRtcVideoPeerFfmpegArgs,
    int WebRtcRoomsCleanupIntervalSeconds,
    int WebRtcRoomIdleStopSeconds,
    int WebRtcRoomsMaxHelpers,
    IReadOnlyList<string> WebRtcTurnUrls,
    string WebRtcTurnSharedSecret,
    int WebRtcTurnTtlSeconds,
    string WebRtcTurnUsername,
    int WebRtcClientJoinTimeoutMs,
    int WebRtcClientConnectTimeoutMs,
    bool WebRtcRoomsPersistEnabled,
    string WebRtcRoomsStorePath,
    int WebRtcRoomsPersistTtlSeconds)
{
    /// <summary>
    /// Maps server options to minimal video options DTO.
    /// </summary>
    /// <returns>Video options DTO.</returns>
    public VideoOptions ToVideoOptions()
    {
        return new VideoOptions(
            Url,
            BindAll,
            VideoRtspPort,
            VideoSrtBasePort,
            VideoSrtLatencyMs,
            VideoRtmpPort,
            VideoHlsDir,
            VideoLlHls,
            VideoHlsSegmentSeconds,
            VideoHlsListSize,
            VideoHlsDeleteSegments);
    }

    /// <summary>
    /// Maps server options to video pipeline options DTO.
    /// </summary>
    /// <returns>Video pipeline options DTO.</returns>
    public VideoPipelineOptions ToVideoPipelineOptions()
    {
        return new VideoPipelineOptions(
            Url,
            BindAll,
            FfmpegPath,
            VideoRtspPort,
            VideoSrtBasePort,
            VideoSrtLatencyMs,
            VideoRtmpPort,
            VideoHlsDir,
            VideoLlHls,
            VideoHlsSegmentSeconds,
            VideoHlsListSize,
            VideoHlsDeleteSegments,
            VideoSecondaryCaptureEnabled,
            VideoModesCacheTtlSeconds,
            VideoKillOrphanFfmpeg);
    }

    /// <summary>
    /// Resolves config path.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <returns>Result.</returns>
    public static string ResolveConfigPath(string[] args)
    {
        static string? FindArgValue(string[] argv, string name)
        {
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == name && i + 1 < argv.Length)
                {
                    return argv[i + 1];
                }
            }
            return null;
        }

        string? explicitPath = FindArgValue(args, "--config");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string cwd = Path.Combine(Directory.GetCurrentDirectory(), "hidcontrol.config.json");
        if (File.Exists(cwd))
        {
            return cwd;
        }

        string appBase = Path.Combine(AppContext.BaseDirectory, "hidcontrol.config.json");
        if (File.Exists(appBase))
        {
            return appBase;
        }

        return cwd;
    }

    /// <summary>
    /// Tries to update config video sources.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public static bool TryUpdateConfigVideoSources(string path, IReadOnlyList<VideoSourceConfig> sources, out string? error)
    {
        error = null;
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            root["videoSources"] = JsonSerializer.SerializeToNode(sources, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Tries to update config active video profile.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="activeProfile">The activeProfile.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public static bool TryUpdateConfigActiveVideoProfile(string path, string activeProfile, out string? error)
    {
        error = null;
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            root["activeVideoProfile"] = activeProfile;

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// API contract model for OptionsFile.
    /// </summary>
    /// <param name="SerialPort">SerialPort.</param>
    /// <param name="SerialAuto">SerialAuto.</param>
    /// <param name="SerialMatch">SerialMatch.</param>
    /// <param name="Baud">Baud.</param>
    /// <param name="Url">Url.</param>
    /// <param name="BindAll">BindAll.</param>
    /// <param name="MouseMappingDb">MouseMappingDb.</param>
    /// <param name="PgConnectionString">PgConnectionString.</param>
    /// <param name="MigrateSqliteToPg">MigrateSqliteToPg.</param>
    /// <param name="VideoSources">VideoSources.</param>
    /// <param name="VideoProfiles">VideoProfiles.</param>
    /// <param name="ActiveVideoProfile">ActiveVideoProfile.</param>
    /// <param name="MouseReportLen">MouseReportLen.</param>
    /// <param name="InjectQueueCapacity">InjectQueueCapacity.</param>
    /// <param name="InjectDropThreshold">InjectDropThreshold.</param>
    /// <param name="InjectTimeoutMs">InjectTimeoutMs.</param>
    /// <param name="InjectRetries">InjectRetries.</param>
    /// <param name="MouseMoveTimeoutMs">MouseMoveTimeoutMs.</param>
    /// <param name="MouseMoveDropIfBusy">MouseMoveDropIfBusy.</param>
    /// <param name="MouseMoveAllowZero">MouseMoveAllowZero.</param>
    /// <param name="MouseWheelTimeoutMs">MouseWheelTimeoutMs.</param>
    /// <param name="MouseWheelDropIfBusy">MouseWheelDropIfBusy.</param>
    /// <param name="MouseWheelAllowZero">MouseWheelAllowZero.</param>
    /// <param name="KeyboardInjectTimeoutMs">KeyboardInjectTimeoutMs.</param>
    /// <param name="KeyboardInjectRetries">KeyboardInjectRetries.</param>
    /// <param name="MasterSecret">MasterSecret.</param>
    /// <param name="MouseItfSel">MouseItfSel.</param>
    /// <param name="KeyboardItfSel">KeyboardItfSel.</param>
    /// <param name="MouseTypeName">MouseTypeName.</param>
    /// <param name="KeyboardTypeName">KeyboardTypeName.</param>
    /// <param name="DevicesAutoMuteLogs">DevicesAutoMuteLogs.</param>
    /// <param name="DevicesAutoRefreshMs">DevicesAutoRefreshMs.</param>
    /// <param name="DevicesIncludeReportDesc">DevicesIncludeReportDesc.</param>
    /// <param name="DevicesCachePath">DevicesCachePath.</param>
    /// <param name="UartHmacKey">UartHmacKey.</param>
    /// <param name="MouseLeftMask">MouseLeftMask.</param>
    /// <param name="MouseRightMask">MouseRightMask.</param>
    /// <param name="MouseMiddleMask">MouseMiddleMask.</param>
    /// <param name="MouseBackMask">MouseBackMask.</param>
    /// <param name="MouseForwardMask">MouseForwardMask.</param>
    /// <param name="Token">Token.</param>
    /// <param name="FfmpegPath">FfmpegPath.</param>
    /// <param name="FfmpegAutoStart">FfmpegAutoStart.</param>
    /// <param name="FfmpegWatchdogEnabled">FfmpegWatchdogEnabled.</param>
    /// <param name="FfmpegWatchdogIntervalMs">FfmpegWatchdogIntervalMs.</param>
    /// <param name="FfmpegRestartDelayMs">FfmpegRestartDelayMs.</param>
    /// <param name="FfmpegMaxRestarts">FfmpegMaxRestarts.</param>
    /// <param name="FfmpegRestartWindowMs">FfmpegRestartWindowMs.</param>
    /// <param name="VideoRtspPort">VideoRtspPort.</param>
    /// <param name="VideoSrtBasePort">VideoSrtBasePort.</param>
    /// <param name="VideoSrtLatencyMs">VideoSrtLatencyMs.</param>
    /// <param name="VideoRtmpPort">VideoRtmpPort.</param>
    /// <param name="VideoHlsDir">VideoHlsDir.</param>
    /// <param name="VideoLlHls">VideoLlHls.</param>
    /// <param name="VideoHlsSegmentSeconds">VideoHlsSegmentSeconds.</param>
    /// <param name="VideoHlsListSize">VideoHlsListSize.</param>
    /// <param name="VideoHlsDeleteSegments">VideoHlsDeleteSegments.</param>
    /// <param name="VideoLogDir">VideoLogDir.</param>
    /// <param name="VideoCaptureAutoStopFfmpeg">VideoCaptureAutoStopFfmpeg.</param>
    /// <param name="VideoCaptureRetryCount">VideoCaptureRetryCount.</param>
    /// <param name="VideoCaptureRetryDelayMs">VideoCaptureRetryDelayMs.</param>
    /// <param name="VideoCaptureLockTimeoutMs">VideoCaptureLockTimeoutMs.</param>
    /// <param name="VideoSecondaryCaptureEnabled">VideoSecondaryCaptureEnabled.</param>
    /// <param name="VideoKillOrphanFfmpeg">VideoKillOrphanFfmpeg.</param>
    /// <param name="VideoFlvBufferBytes">VideoFlvBufferBytes.</param>
    /// <param name="VideoFlvReadBufferBytes">VideoFlvReadBufferBytes.</param>
    /// <param name="VideoMjpegReadBufferBytes">VideoMjpegReadBufferBytes.</param>
    /// <param name="VideoMjpegMaxFrameBytes">VideoMjpegMaxFrameBytes.</param>
    /// <param name="VideoModesCacheTtlSeconds">VideoModesCacheTtlSeconds.</param>
    /// <param name="DevicesCacheMaxAgeSeconds">DevicesCacheMaxAgeSeconds.</param>
    /// <param name="ServerEventLogMaxEntries">ServerEventLogMaxEntries.</param>
    private sealed record OptionsFile(
        string? SerialPort,
        bool? SerialAuto,
        string? SerialMatch,
        int? Baud,
        string? Url,
        bool? BindAll,
        string? MouseMappingDb,
        string? PgConnectionString,
        bool? MigrateSqliteToPg,
        List<VideoSourceConfig>? VideoSources,
        List<VideoProfileConfig>? VideoProfiles,
        string? ActiveVideoProfile,
        int? MouseReportLen,
        int? InjectQueueCapacity,
        int? InjectDropThreshold,
        int? InjectTimeoutMs,
        int? InjectRetries,
        int? MouseMoveTimeoutMs,
        bool? MouseMoveDropIfBusy,
        bool? MouseMoveAllowZero,
        int? MouseWheelTimeoutMs,
        bool? MouseWheelDropIfBusy,
        bool? MouseWheelAllowZero,
        int? KeyboardInjectTimeoutMs,
        int? KeyboardInjectRetries,
        string? MasterSecret,
        byte? MouseItfSel,
        byte? KeyboardItfSel,
        string? MouseTypeName,
        string? KeyboardTypeName,
        bool? DevicesAutoMuteLogs,
        int? DevicesAutoRefreshMs,
        bool? DevicesIncludeReportDesc,
        string? DevicesCachePath,
        string? UartHmacKey,
        byte? MouseLeftMask,
        byte? MouseRightMask,
        byte? MouseMiddleMask,
        byte? MouseBackMask,
        byte? MouseForwardMask,
        string? Token,
        string? FfmpegPath,
        bool? FfmpegAutoStart,
        bool? FfmpegWatchdogEnabled,
        int? FfmpegWatchdogIntervalMs,
        int? FfmpegRestartDelayMs,
        int? FfmpegMaxRestarts,
        int? FfmpegRestartWindowMs,
        int? VideoRtspPort,
        int? VideoSrtBasePort,
        int? VideoSrtLatencyMs,
        int? VideoRtmpPort,
        string? VideoHlsDir,
        bool? VideoLlHls,
        int? VideoHlsSegmentSeconds,
        int? VideoHlsListSize,
        bool? VideoHlsDeleteSegments,
        string? VideoLogDir,
        bool? VideoCaptureAutoStopFfmpeg,
        int? VideoCaptureRetryCount,
        int? VideoCaptureRetryDelayMs,
        int? VideoCaptureLockTimeoutMs,
        bool? VideoSecondaryCaptureEnabled,
        bool? VideoKillOrphanFfmpeg,
        int? VideoFlvBufferBytes,
        int? VideoFlvReadBufferBytes,
        int? VideoMjpegReadBufferBytes,
        int? VideoMjpegMaxFrameBytes,
        int? VideoModesCacheTtlSeconds,
        int? DevicesCacheMaxAgeSeconds,
        int? ServerEventLogMaxEntries,
        bool? WebRtcControlPeerAutoStart,
        string? WebRtcControlPeerRoom,
        string? WebRtcControlPeerStun,
        bool? WebRtcVideoPeerAutoStart,
        string? WebRtcVideoPeerRoom,
        string? WebRtcVideoPeerStun,
        string? WebRtcVideoPeerSourceMode,
        string? WebRtcVideoPeerQualityPreset,
        string? WebRtcVideoPeerCaptureInput,
        string? WebRtcVideoPeerFfmpegArgs,
        int? WebRtcRoomsCleanupIntervalSeconds,
        int? WebRtcRoomIdleStopSeconds,
        int? WebRtcRoomsMaxHelpers,
        List<string>? WebRtcTurnUrls,
        string? WebRtcTurnSharedSecret,
        int? WebRtcTurnTtlSeconds,
        string? WebRtcTurnUsername,
        int? WebRtcClientJoinTimeoutMs,
        int? WebRtcClientConnectTimeoutMs,
        bool? WebRtcRoomsPersistEnabled,
        string? WebRtcRoomsStorePath,
        int? WebRtcRoomsPersistTtlSeconds);

    /// <summary>
    /// Parses parse.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <returns>Result.</returns>
    public static Options Parse(string[] args)
    {
        /// <summary>
        /// Tries to parse byte arg.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="b">The b.</param>
        /// <returns>Result.</returns>
        static bool TryParseByteArg(string value, out byte b)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return byte.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
            }
            return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b);
        }

        /// <summary>
        /// Tries to load config.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>Result.</returns>
        static OptionsFile? TryLoadConfig(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<OptionsFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (cfg is null) throw new InvalidOperationException("Config deserialized to null.");
                return cfg;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load config file '{path}': {ex.Message}", ex);
            }
        }

        string configPath = ResolveConfigPath(args);
        OptionsFile? cfg = TryLoadConfig(configPath);
        string baseDir = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            string? cfgDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (!string.IsNullOrWhiteSpace(cfgDir))
            {
                baseDir = cfgDir;
            }
        }

        /// <summary>
        /// Resolves path.
        /// </summary>
        /// <param name="baseDir">The baseDir.</param>
        /// <param name="value">The value.</param>
        /// <param name="fallbackName">The fallbackName.</param>
        /// <returns>Result.</returns>
        static string ResolvePath(string baseDir, string? value, string fallbackName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Path.Combine(baseDir, fallbackName);
            }
            if (Path.IsPathRooted(value))
            {
                return value;
            }
            return Path.Combine(baseDir, value);
        }

        /// <summary>
        /// Resolves executable path.
        /// </summary>
        /// <param name="baseDir">The baseDir.</param>
        /// <param name="value">The value.</param>
        /// <param name="fallbackName">The fallbackName.</param>
        /// <param name="depsName">The depsName.</param>
        /// <returns>Result.</returns>
        static string ResolveExecutablePath(string baseDir, string? value, string fallbackName, string depsName)
        {
            string raw = string.IsNullOrWhiteSpace(value) ? fallbackName : value;
            if (Path.IsPathRooted(raw))
            {
                return raw;
            }
            if (raw.Contains(Path.DirectorySeparatorChar) || raw.Contains(Path.AltDirectorySeparatorChar))
            {
                string combined = Path.Combine(baseDir, raw);
                if (File.Exists(combined))
                {
                    return combined;
                }
                string current = Path.Combine(Directory.GetCurrentDirectory(), raw);
                if (File.Exists(current))
                {
                    return current;
                }
                string appBase = Path.Combine(AppContext.BaseDirectory, raw);
                if (File.Exists(appBase))
                {
                    return appBase;
                }
                return combined;
            }
            if (ExecutableService.TryFindExecutableOnPath(raw, out string resolved))
            {
                return resolved;
            }
            string[] baseDirs = new[]
            {
                baseDir,
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };
            foreach (string root in baseDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string depsDir = Path.Combine(root, ".deps", depsName);
                if (!Directory.Exists(depsDir))
                {
                    continue;
                }
                string exeName = raw;
                if (OperatingSystem.IsWindows() && !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeName += ".exe";
                }
                string direct = Path.Combine(depsDir, exeName);
                if (File.Exists(direct))
                {
                    return direct;
                }
                string bin = Path.Combine(depsDir, "bin", exeName);
                if (File.Exists(bin))
                {
                    return bin;
                }
                string? deep = Directory.EnumerateFiles(depsDir, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(deep))
                {
                    return deep;
                }
            }
            return raw;
        }

        string serialPort = cfg?.SerialPort ?? "COM6";
        bool serialAuto = cfg?.SerialAuto ?? false;
        string? serialMatch = cfg?.SerialMatch;
        int baud = cfg?.Baud ?? 3_000_000;
        string url = cfg?.Url ?? "http://127.0.0.1:8080";
        bool bindAll = cfg?.BindAll ?? false;
        string mouseMappingDb = cfg?.MouseMappingDb ?? Path.Combine(Directory.GetCurrentDirectory(), "hidcontrol.db");
        string pgConnectionString = cfg?.PgConnectionString ?? "Host=localhost;Port=5432;Database=hidcontrol;Username=hidcontrol;Password=hidcontrol";
        bool migrateSqliteToPg = cfg?.MigrateSqliteToPg ?? false;
        IReadOnlyList<VideoSourceConfig> videoSources = cfg?.VideoSources ?? new List<VideoSourceConfig>();
        IReadOnlyList<VideoProfileConfig> videoProfiles = cfg?.VideoProfiles ?? new List<VideoProfileConfig>();
        string activeVideoProfile = cfg?.ActiveVideoProfile ?? "low-latency";
        int mouseReportLen = cfg?.MouseReportLen ?? 4;
        int injectQueueCapacity = cfg?.InjectQueueCapacity ?? 256;
        int injectDropThreshold = cfg?.InjectDropThreshold ?? 8;
        int injectTimeoutMs = cfg?.InjectTimeoutMs ?? 200;
        int injectRetries = cfg?.InjectRetries ?? 2;
        int mouseMoveTimeoutMs = cfg?.MouseMoveTimeoutMs ?? 50;
        bool mouseMoveDropIfBusy = cfg?.MouseMoveDropIfBusy ?? true;
        bool mouseMoveAllowZero = cfg?.MouseMoveAllowZero ?? false;
        int mouseWheelTimeoutMs = cfg?.MouseWheelTimeoutMs ?? 50;
        bool mouseWheelDropIfBusy = cfg?.MouseWheelDropIfBusy ?? true;
        bool mouseWheelAllowZero = cfg?.MouseWheelAllowZero ?? false;
        int keyboardInjectTimeoutMs = cfg?.KeyboardInjectTimeoutMs ?? injectTimeoutMs;
        int keyboardInjectRetries = cfg?.KeyboardInjectRetries ?? injectRetries;
        string masterSecret = cfg?.MasterSecret ?? "changeme-master-secret";
        byte mouseItfSel = cfg?.MouseItfSel ?? 0xFF;
        byte keyboardItfSel = cfg?.KeyboardItfSel ?? 0xFE;
        string? mouseTypeName = cfg?.MouseTypeName;
        string? keyboardTypeName = cfg?.KeyboardTypeName;
        bool devicesAutoMuteLogs = cfg?.DevicesAutoMuteLogs ?? true;
        int devicesAutoRefreshMs = cfg?.DevicesAutoRefreshMs ?? 0;
        bool devicesIncludeReportDesc = cfg?.DevicesIncludeReportDesc ?? true;
        string devicesCachePath = ResolvePath(baseDir, cfg?.DevicesCachePath, "devices_cache.json");
        string uartHmacKey = cfg?.UartHmacKey ?? "changeme";
        byte mouseLeftMask = cfg?.MouseLeftMask ?? 0x01;
        byte mouseRightMask = cfg?.MouseRightMask ?? 0x02;
        byte mouseMiddleMask = cfg?.MouseMiddleMask ?? 0x04;
        byte mouseBackMask = cfg?.MouseBackMask ?? 0x08;
        byte mouseForwardMask = cfg?.MouseForwardMask ?? 0x10;
        string? token = cfg?.Token;
        string ffmpegPath = ResolveExecutablePath(baseDir, cfg?.FfmpegPath, "ffmpeg", "ffmpeg");
        bool ffmpegAutoStart = cfg?.FfmpegAutoStart ?? false;
        bool ffmpegWatchdogEnabled = cfg?.FfmpegWatchdogEnabled ?? true;
        int ffmpegWatchdogIntervalMs = cfg?.FfmpegWatchdogIntervalMs ?? 1000;
        int ffmpegRestartDelayMs = cfg?.FfmpegRestartDelayMs ?? 1500;
        int ffmpegMaxRestarts = cfg?.FfmpegMaxRestarts ?? 10;
        int ffmpegRestartWindowMs = cfg?.FfmpegRestartWindowMs ?? 60_000;
        int videoRtspPort = cfg?.VideoRtspPort ?? 8554;
        int videoSrtBasePort = cfg?.VideoSrtBasePort ?? 9000;
        int videoSrtLatencyMs = cfg?.VideoSrtLatencyMs ?? 50;
        int videoRtmpPort = cfg?.VideoRtmpPort ?? 1935;
        string videoHlsDir = ResolvePath(baseDir, cfg?.VideoHlsDir, "video_hls");
        bool videoLlHls = cfg?.VideoLlHls ?? true;
        int videoHlsSegmentSeconds = cfg?.VideoHlsSegmentSeconds ?? 1;
        int videoHlsListSize = cfg?.VideoHlsListSize ?? 6;
        bool videoHlsDeleteSegments = cfg?.VideoHlsDeleteSegments ?? true;
        string videoLogDir = ResolvePath(baseDir, cfg?.VideoLogDir, "video_logs");
        bool videoCaptureAutoStopFfmpeg = cfg?.VideoCaptureAutoStopFfmpeg ?? true;
        int videoCaptureRetryCount = cfg?.VideoCaptureRetryCount ?? 3;
        int videoCaptureRetryDelayMs = cfg?.VideoCaptureRetryDelayMs ?? 500;
        int videoCaptureLockTimeoutMs = cfg?.VideoCaptureLockTimeoutMs ?? 2000;
        bool videoSecondaryCaptureEnabled = cfg?.VideoSecondaryCaptureEnabled ?? false;
        bool videoKillOrphanFfmpeg = cfg?.VideoKillOrphanFfmpeg ?? true;
        int videoFlvBufferBytes = cfg?.VideoFlvBufferBytes ?? (256 * 1024);
        int videoFlvReadBufferBytes = cfg?.VideoFlvReadBufferBytes ?? (64 * 1024);
        int videoMjpegReadBufferBytes = cfg?.VideoMjpegReadBufferBytes ?? (16 * 1024);
        int videoMjpegMaxFrameBytes = cfg?.VideoMjpegMaxFrameBytes ?? (4 * 1024 * 1024);
        int videoModesCacheTtlSeconds = cfg?.VideoModesCacheTtlSeconds ?? 300;
        int devicesCacheMaxAgeSeconds = cfg?.DevicesCacheMaxAgeSeconds ?? 600;
        int serverEventLogMaxEntries = cfg?.ServerEventLogMaxEntries ?? 200;
        bool webRtcControlPeerAutoStart = cfg?.WebRtcControlPeerAutoStart ?? false;
        string webRtcControlPeerRoom = cfg?.WebRtcControlPeerRoom ?? "control";
        string webRtcControlPeerStun = cfg?.WebRtcControlPeerStun ?? "stun:stun.l.google.com:19302";
        bool webRtcVideoPeerAutoStart = cfg?.WebRtcVideoPeerAutoStart ?? false;
        string webRtcVideoPeerRoom = cfg?.WebRtcVideoPeerRoom ?? "video";
        string webRtcVideoPeerStun = cfg?.WebRtcVideoPeerStun ?? webRtcControlPeerStun;
        string webRtcVideoPeerSourceMode = string.IsNullOrWhiteSpace(cfg?.WebRtcVideoPeerSourceMode)
            ? "testsrc"
            : (cfg?.WebRtcVideoPeerSourceMode?.Trim() ?? "testsrc");
        string webRtcVideoPeerQualityPreset = string.IsNullOrWhiteSpace(cfg?.WebRtcVideoPeerQualityPreset)
            ? "balanced"
            : (cfg?.WebRtcVideoPeerQualityPreset?.Trim().ToLowerInvariant() ?? "balanced");
        string? webRtcVideoPeerCaptureInput = string.IsNullOrWhiteSpace(cfg?.WebRtcVideoPeerCaptureInput)
            ? null
            : cfg?.WebRtcVideoPeerCaptureInput?.Trim();
        string? webRtcVideoPeerFfmpegArgs = string.IsNullOrWhiteSpace(cfg?.WebRtcVideoPeerFfmpegArgs)
            ? null
            : cfg?.WebRtcVideoPeerFfmpegArgs?.Trim();
        int webRtcRoomsCleanupIntervalSeconds = cfg?.WebRtcRoomsCleanupIntervalSeconds ?? 5;
        int webRtcRoomIdleStopSeconds = cfg?.WebRtcRoomIdleStopSeconds ?? 30;
        int webRtcRoomsMaxHelpers = cfg?.WebRtcRoomsMaxHelpers ?? 5;
        IReadOnlyList<string> webRtcTurnUrls = cfg?.WebRtcTurnUrls ?? new List<string>();
        string webRtcTurnSharedSecret = cfg?.WebRtcTurnSharedSecret ?? string.Empty;
        int webRtcTurnTtlSeconds = cfg?.WebRtcTurnTtlSeconds ?? 3600;
        string webRtcTurnUsername = cfg?.WebRtcTurnUsername ?? "hidbridge";
        // Intentionally small defaults for fast-fail on LAN. Increase for TURN/TCP-heavy environments.
        int webRtcClientJoinTimeoutMs = cfg?.WebRtcClientJoinTimeoutMs ?? 250;
        int webRtcClientConnectTimeoutMs = cfg?.WebRtcClientConnectTimeoutMs ?? 5000;
        bool webRtcRoomsPersistEnabled = cfg?.WebRtcRoomsPersistEnabled ?? false;
        string webRtcRoomsStorePath = ResolvePath(baseDir, cfg?.WebRtcRoomsStorePath, "webrtc_rooms.json");
        int webRtcRoomsPersistTtlSeconds = cfg?.WebRtcRoomsPersistTtlSeconds ?? 86_400;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? v = (i + 1 < args.Length) ? args[i + 1] : null;

            if (a == "--serial" && v is not null)
            {
                serialPort = v;
                i++;
            }
            else if (a == "--serialAuto" && v is not null && bool.TryParse(v, out bool sa))
            {
                serialAuto = sa;
                i++;
            }
            else if (a == "--serialMatch" && v is not null)
            {
                serialMatch = v;
                i++;
            }
            else if (a == "--baud" && v is not null && int.TryParse(v, out int b))
            {
                baud = b;
                i++;
            }
            else if (a == "--url" && v is not null)
            {
                url = v;
                i++;
            }
            else if (a == "--bindAll" && v is not null && bool.TryParse(v, out bool ba))
            {
                bindAll = ba;
                i++;
            }
            else if (a == "--mouseMappingDb" && v is not null)
            {
                mouseMappingDb = v;
                i++;
            }
            else if (a == "--pgConnectionString" && v is not null)
            {
                pgConnectionString = v;
                i++;
            }
            else if (a == "--migrateSqliteToPg" && v is not null && bool.TryParse(v, out bool migratePg))
            {
                migrateSqliteToPg = migratePg;
                i++;
            }
            else if (a == "--mouseReportLen" && v is not null && int.TryParse(v, out int mr))
            {
                mouseReportLen = mr;
                i++;
            }
            else if (a == "--injectQueueCapacity" && v is not null && int.TryParse(v, out int iqc))
            {
                injectQueueCapacity = iqc;
                i++;
            }
            else if (a == "--injectDropThreshold" && v is not null && int.TryParse(v, out int idt))
            {
                injectDropThreshold = idt;
                i++;
            }
            else if (a == "--injectTimeoutMs" && v is not null && int.TryParse(v, out int itm))
            {
                injectTimeoutMs = itm;
                i++;
            }
            else if (a == "--injectRetries" && v is not null && int.TryParse(v, out int ir))
            {
                injectRetries = ir;
                i++;
            }
            else if (a == "--mouseMoveTimeoutMs" && v is not null && int.TryParse(v, out int mmt))
            {
                mouseMoveTimeoutMs = mmt;
                i++;
            }
            else if (a == "--mouseMoveDropIfBusy" && v is not null && bool.TryParse(v, out bool mmd))
            {
                mouseMoveDropIfBusy = mmd;
                i++;
            }
            else if (a == "--mouseMoveAllowZero" && v is not null && bool.TryParse(v, out bool mma))
            {
                mouseMoveAllowZero = mma;
                i++;
            }
            else if (a == "--mouseWheelTimeoutMs" && v is not null && int.TryParse(v, out int mwt))
            {
                mouseWheelTimeoutMs = mwt;
                i++;
            }
            else if (a == "--mouseWheelDropIfBusy" && v is not null && bool.TryParse(v, out bool mwd))
            {
                mouseWheelDropIfBusy = mwd;
                i++;
            }
            else if (a == "--mouseWheelAllowZero" && v is not null && bool.TryParse(v, out bool mwa))
            {
                mouseWheelAllowZero = mwa;
                i++;
            }
            else if (a == "--keyboardInjectTimeoutMs" && v is not null && int.TryParse(v, out int kit))
            {
                keyboardInjectTimeoutMs = kit;
                i++;
            }
            else if (a == "--keyboardInjectRetries" && v is not null && int.TryParse(v, out int kir))
            {
                keyboardInjectRetries = kir;
                i++;
            }
            else if (a == "--masterSecret" && v is not null)
            {
                masterSecret = v;
                i++;
            }
            else if (a == "--mouseItfSel" && v is not null && TryParseByteArg(v, out byte mis))
            {
                mouseItfSel = mis;
                i++;
            }
            else if (a == "--keyboardItfSel" && v is not null && TryParseByteArg(v, out byte kis))
            {
                keyboardItfSel = kis;
                i++;
            }
            else if (a == "--mouseTypeName" && v is not null)
            {
                mouseTypeName = v;
                i++;
            }
            else if (a == "--keyboardTypeName" && v is not null)
            {
                keyboardTypeName = v;
                i++;
            }
            else if (a == "--devicesAutoMuteLogs" && v is not null && bool.TryParse(v, out bool ml))
            {
                devicesAutoMuteLogs = ml;
                i++;
            }
            else if (a == "--devicesAutoRefreshMs" && v is not null && int.TryParse(v, out int rm))
            {
                devicesAutoRefreshMs = rm;
                i++;
            }
            else if (a == "--devicesIncludeReportDesc" && v is not null && bool.TryParse(v, out bool rd))
            {
                devicesIncludeReportDesc = rd;
                i++;
            }
            else if (a == "--devicesCachePath" && v is not null)
            {
                devicesCachePath = v;
                i++;
            }
            else if (a == "--uartHmacKey" && v is not null)
            {
                uartHmacKey = v;
                i++;
            }
            else if (a == "--mouseLeftMask" && v is not null && TryParseByteArg(v, out byte mlm))
            {
                mouseLeftMask = mlm;
                i++;
            }
            else if (a == "--mouseRightMask" && v is not null && TryParseByteArg(v, out byte mrm))
            {
                mouseRightMask = mrm;
                i++;
            }
            else if (a == "--mouseMiddleMask" && v is not null && TryParseByteArg(v, out byte mmm))
            {
                mouseMiddleMask = mmm;
                i++;
            }
            else if (a == "--mouseBackMask" && v is not null && TryParseByteArg(v, out byte mbm))
            {
                mouseBackMask = mbm;
                i++;
            }
            else if (a == "--mouseForwardMask" && v is not null && TryParseByteArg(v, out byte mfm))
            {
                mouseForwardMask = mfm;
                i++;
            }
            else if (a == "--config" && v is not null)
            {
                // already loaded above; just skip value
                i++;
            }
            else if (a == "--token" && v is not null)
            {
                token = v;
                i++;
            }
            else if (a == "--ffmpegPath" && v is not null)
            {
                ffmpegPath = v;
                i++;
            }
            else if (a == "--ffmpegAutoStart" && v is not null && bool.TryParse(v, out bool fas))
            {
                ffmpegAutoStart = fas;
                i++;
            }
            else if (a == "--ffmpegWatchdogEnabled" && v is not null && bool.TryParse(v, out bool fwe))
            {
                ffmpegWatchdogEnabled = fwe;
                i++;
            }
            else if (a == "--ffmpegWatchdogIntervalMs" && v is not null && int.TryParse(v, out int fwi))
            {
                ffmpegWatchdogIntervalMs = fwi;
                i++;
            }
            else if (a == "--ffmpegRestartDelayMs" && v is not null && int.TryParse(v, out int frd))
            {
                ffmpegRestartDelayMs = frd;
                i++;
            }
            else if (a == "--ffmpegMaxRestarts" && v is not null && int.TryParse(v, out int fmr))
            {
                ffmpegMaxRestarts = fmr;
                i++;
            }
            else if (a == "--ffmpegRestartWindowMs" && v is not null && int.TryParse(v, out int frw))
            {
                ffmpegRestartWindowMs = frw;
                i++;
            }
            else if (a == "--videoRtspPort" && v is not null && int.TryParse(v, out int vrp))
            {
                videoRtspPort = vrp;
                i++;
            }
            else if (a == "--videoSrtBasePort" && v is not null && int.TryParse(v, out int vsp))
            {
                videoSrtBasePort = vsp;
                i++;
            }
            else if (a == "--videoSrtLatencyMs" && v is not null && int.TryParse(v, out int vsl))
            {
                videoSrtLatencyMs = vsl;
                i++;
            }
            else if (a == "--videoRtmpPort" && v is not null && int.TryParse(v, out int vrm))
            {
                videoRtmpPort = vrm;
                i++;
            }
            else if (a == "--videoHlsDir" && v is not null)
            {
                videoHlsDir = v;
                i++;
            }
            else if (a == "--videoLlHls" && v is not null && bool.TryParse(v, out bool vll))
            {
                videoLlHls = vll;
                i++;
            }
            else if (a == "--videoHlsSegmentSeconds" && v is not null && int.TryParse(v, out int vhs))
            {
                videoHlsSegmentSeconds = vhs;
                i++;
            }
            else if (a == "--videoHlsListSize" && v is not null && int.TryParse(v, out int vhl))
            {
                videoHlsListSize = vhl;
                i++;
            }
            else if (a == "--videoHlsDeleteSegments" && v is not null && bool.TryParse(v, out bool vhd))
            {
                videoHlsDeleteSegments = vhd;
                i++;
            }
            else if (a == "--videoLogDir" && v is not null)
            {
                videoLogDir = v;
                i++;
            }
            else if (a == "--videoCaptureAutoStopFfmpeg" && v is not null && bool.TryParse(v, out bool vcas))
            {
                videoCaptureAutoStopFfmpeg = vcas;
                i++;
            }
            else if (a == "--videoCaptureRetryCount" && v is not null && int.TryParse(v, out int vcrc))
            {
                videoCaptureRetryCount = vcrc;
                i++;
            }
            else if (a == "--videoCaptureRetryDelayMs" && v is not null && int.TryParse(v, out int vcrd))
            {
                videoCaptureRetryDelayMs = vcrd;
                i++;
            }
            else if (a == "--videoCaptureLockTimeoutMs" && v is not null && int.TryParse(v, out int vclt))
            {
                videoCaptureLockTimeoutMs = vclt;
                i++;
            }
            else if (a == "--videoSecondaryCaptureEnabled" && v is not null && bool.TryParse(v, out bool vsec))
            {
                videoSecondaryCaptureEnabled = vsec;
                i++;
            }
            else if (a == "--videoKillOrphanFfmpeg" && v is not null && bool.TryParse(v, out bool vok))
            {
                videoKillOrphanFfmpeg = vok;
                i++;
            }
            else if (a == "--videoFlvBufferBytes" && v is not null && int.TryParse(v, out int vfbb))
            {
                videoFlvBufferBytes = vfbb;
                i++;
            }
            else if (a == "--videoFlvReadBufferBytes" && v is not null && int.TryParse(v, out int vfrb))
            {
                videoFlvReadBufferBytes = vfrb;
                i++;
            }
            else if (a == "--videoMjpegReadBufferBytes" && v is not null && int.TryParse(v, out int vmrb))
            {
                videoMjpegReadBufferBytes = vmrb;
                i++;
            }
            else if (a == "--videoMjpegMaxFrameBytes" && v is not null && int.TryParse(v, out int vmmfb))
            {
                videoMjpegMaxFrameBytes = vmmfb;
                i++;
            }
            else if (a == "--videoModesCacheTtlSeconds" && v is not null && int.TryParse(v, out int vmct))
            {
                videoModesCacheTtlSeconds = vmct;
                i++;
            }
            else if (a == "--devicesCacheMaxAgeSeconds" && v is not null && int.TryParse(v, out int dcma))
            {
                devicesCacheMaxAgeSeconds = dcma;
                i++;
            }
        }

        ffmpegPath = ResolveExecutablePath(baseDir, ffmpegPath, "ffmpeg", "ffmpeg");

        /// <summary>
        /// Executes Options.
        /// </summary>
        /// <param name="serialPort">The serialPort.</param>
        /// <param name="serialAuto">The serialAuto.</param>
        /// <param name="serialMatch">The serialMatch.</param>
        /// <param name="baud">The baud.</param>
        /// <param name="url">The url.</param>
        /// <param name="bindAll">The bindAll.</param>
        /// <param name="mouseMappingDb">The mouseMappingDb.</param>
        /// <param name="pgConnectionString">The pgConnectionString.</param>
        /// <param name="migrateSqliteToPg">The migrateSqliteToPg.</param>
        /// <param name="videoSources">The videoSources.</param>
        /// <param name="videoProfiles">The videoProfiles.</param>
        /// <param name="activeVideoProfile">The activeVideoProfile.</param>
        /// <param name="mouseReportLen">The mouseReportLen.</param>
        /// <param name="injectQueueCapacity">The injectQueueCapacity.</param>
        /// <param name="injectDropThreshold">The injectDropThreshold.</param>
        /// <param name="injectTimeoutMs">The injectTimeoutMs.</param>
        /// <param name="injectRetries">The injectRetries.</param>
        /// <param name="mouseMoveTimeoutMs">The mouseMoveTimeoutMs.</param>
        /// <param name="mouseMoveDropIfBusy">The mouseMoveDropIfBusy.</param>
        /// <param name="mouseMoveAllowZero">The mouseMoveAllowZero.</param>
        /// <param name="mouseWheelTimeoutMs">The mouseWheelTimeoutMs.</param>
        /// <param name="mouseWheelDropIfBusy">The mouseWheelDropIfBusy.</param>
        /// <param name="mouseWheelAllowZero">The mouseWheelAllowZero.</param>
        /// <param name="keyboardInjectTimeoutMs">The keyboardInjectTimeoutMs.</param>
        /// <param name="keyboardInjectRetries">The keyboardInjectRetries.</param>
        /// <param name="masterSecret">The masterSecret.</param>
        /// <param name="mouseItfSel">The mouseItfSel.</param>
        /// <param name="keyboardItfSel">The keyboardItfSel.</param>
        /// <param name="mouseTypeName">The mouseTypeName.</param>
        /// <param name="keyboardTypeName">The keyboardTypeName.</param>
        /// <param name="devicesAutoMuteLogs">The devicesAutoMuteLogs.</param>
        /// <param name="devicesAutoRefreshMs">The devicesAutoRefreshMs.</param>
        /// <param name="devicesIncludeReportDesc">The devicesIncludeReportDesc.</param>
        /// <param name="devicesCachePath">The devicesCachePath.</param>
        /// <param name="uartHmacKey">The uartHmacKey.</param>
        /// <param name="mouseLeftMask">The mouseLeftMask.</param>
        /// <param name="mouseRightMask">The mouseRightMask.</param>
        /// <param name="mouseMiddleMask">The mouseMiddleMask.</param>
        /// <param name="mouseBackMask">The mouseBackMask.</param>
        /// <param name="mouseForwardMask">The mouseForwardMask.</param>
        /// <param name="token">The token.</param>
        /// <param name="ffmpegPath">The ffmpegPath.</param>
        /// <param name="ffmpegAutoStart">The ffmpegAutoStart.</param>
        /// <param name="ffmpegWatchdogEnabled">The ffmpegWatchdogEnabled.</param>
        /// <param name="ffmpegWatchdogIntervalMs">The ffmpegWatchdogIntervalMs.</param>
        /// <param name="ffmpegRestartDelayMs">The ffmpegRestartDelayMs.</param>
        /// <param name="ffmpegMaxRestarts">The ffmpegMaxRestarts.</param>
        /// <param name="ffmpegRestartWindowMs">The ffmpegRestartWindowMs.</param>
        /// <param name="videoRtspPort">The videoRtspPort.</param>
        /// <param name="videoSrtBasePort">The videoSrtBasePort.</param>
        /// <param name="videoSrtLatencyMs">The videoSrtLatencyMs.</param>
        /// <param name="videoRtmpPort">The videoRtmpPort.</param>
        /// <param name="videoHlsDir">The videoHlsDir.</param>
        /// <param name="videoLlHls">The videoLlHls.</param>
        /// <param name="videoHlsSegmentSeconds">The videoHlsSegmentSeconds.</param>
        /// <param name="videoHlsListSize">The videoHlsListSize.</param>
        /// <param name="videoHlsDeleteSegments">The videoHlsDeleteSegments.</param>
        /// <param name="videoLogDir">The videoLogDir.</param>
        /// <param name="videoCaptureAutoStopFfmpeg">The videoCaptureAutoStopFfmpeg.</param>
        /// <param name="videoCaptureRetryCount">The videoCaptureRetryCount.</param>
        /// <param name="videoCaptureRetryDelayMs">The videoCaptureRetryDelayMs.</param>
        /// <param name="videoCaptureLockTimeoutMs">The videoCaptureLockTimeoutMs.</param>
        /// <param name="videoSecondaryCaptureEnabled">The videoSecondaryCaptureEnabled.</param>
        /// <param name="videoKillOrphanFfmpeg">The videoKillOrphanFfmpeg.</param>
        /// <param name="videoFlvBufferBytes">The videoFlvBufferBytes.</param>
        /// <param name="videoFlvReadBufferBytes">The videoFlvReadBufferBytes.</param>
        /// <param name="videoMjpegReadBufferBytes">The videoMjpegReadBufferBytes.</param>
        /// <param name="videoMjpegMaxFrameBytes">The videoMjpegMaxFrameBytes.</param>
        /// <param name="videoModesCacheTtlSeconds">The videoModesCacheTtlSeconds.</param>
        /// <param name="devicesCacheMaxAgeSeconds">The devicesCacheMaxAgeSeconds.</param>
        /// <param name="serverEventLogMaxEntries">The serverEventLogMaxEntries.</param>
        /// <returns>Result.</returns>
        return new Options(
            serialPort, serialAuto, serialMatch, baud, url, bindAll,
            mouseMappingDb, pgConnectionString, migrateSqliteToPg,
            videoSources, videoProfiles, activeVideoProfile,
            mouseReportLen, injectQueueCapacity, injectDropThreshold, injectTimeoutMs, injectRetries,
            mouseMoveTimeoutMs, mouseMoveDropIfBusy, mouseMoveAllowZero,
            mouseWheelTimeoutMs, mouseWheelDropIfBusy, mouseWheelAllowZero,
            keyboardInjectTimeoutMs, keyboardInjectRetries,
            masterSecret, mouseItfSel, keyboardItfSel, mouseTypeName, keyboardTypeName,
            devicesAutoMuteLogs, devicesAutoRefreshMs, devicesIncludeReportDesc, devicesCachePath,
            uartHmacKey,
            mouseLeftMask, mouseRightMask, mouseMiddleMask, mouseBackMask, mouseForwardMask,
            token,
            ffmpegPath, ffmpegAutoStart,
            ffmpegWatchdogEnabled, ffmpegWatchdogIntervalMs, ffmpegRestartDelayMs, ffmpegMaxRestarts, ffmpegRestartWindowMs,
            videoRtspPort, videoSrtBasePort, videoSrtLatencyMs, videoRtmpPort,
            videoHlsDir, videoLlHls, videoHlsSegmentSeconds, videoHlsListSize, videoHlsDeleteSegments,
            videoLogDir,
            videoCaptureAutoStopFfmpeg, videoCaptureRetryCount, videoCaptureRetryDelayMs, videoCaptureLockTimeoutMs,
            videoSecondaryCaptureEnabled,
            videoKillOrphanFfmpeg,
            videoFlvBufferBytes, videoFlvReadBufferBytes,
            videoMjpegReadBufferBytes, videoMjpegMaxFrameBytes,
            videoModesCacheTtlSeconds,
            devicesCacheMaxAgeSeconds,
            serverEventLogMaxEntries,
            webRtcControlPeerAutoStart,
            webRtcControlPeerRoom,
            webRtcControlPeerStun,
            webRtcVideoPeerAutoStart,
            webRtcVideoPeerRoom,
            webRtcVideoPeerStun,
            webRtcVideoPeerSourceMode,
            webRtcVideoPeerQualityPreset,
            webRtcVideoPeerCaptureInput,
            webRtcVideoPeerFfmpegArgs,
            webRtcRoomsCleanupIntervalSeconds,
            webRtcRoomIdleStopSeconds,
            webRtcRoomsMaxHelpers,
            webRtcTurnUrls,
            webRtcTurnSharedSecret,
            webRtcTurnTtlSeconds,
            webRtcTurnUsername,
            webRtcClientJoinTimeoutMs,
            webRtcClientConnectTimeoutMs,
            webRtcRoomsPersistEnabled,
            webRtcRoomsStorePath,
            webRtcRoomsPersistTtlSeconds);
    }
}

/// <summary>
/// API contract model for MouseState.
/// </summary>
public sealed class MouseState
{
    private readonly object _lock = new();

    public byte Buttons { get; private set; }

    /// <summary>
    /// Gets buttons.
    /// </summary>
    /// <returns>Result.</returns>
    public byte GetButtons()
    {
        lock (_lock)
        {
            return Buttons;
        }
    }

    /// <summary>
    /// Sets buttons mask.
    /// </summary>
    /// <param name="mask">The mask.</param>
    public void SetButtonsMask(byte mask)
    {
        lock (_lock)
        {
            Buttons = mask;
        }
    }
}

/// <summary>
/// API contract model for KeyboardState.
/// </summary>
public sealed class KeyboardState
{
    private readonly object _lock = new();
    private readonly List<byte> _keys = new(6);

    public byte Modifiers { get; private set; }

    /// <summary>
    /// Executes Snapshot.
    /// </summary>
    /// <returns>Result.</returns>
    public KeyboardSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new KeyboardSnapshot(Modifiers, _keys.ToArray());
        }
    }

    public IReadOnlyList<byte> Keys
    {
        get
        {
            lock (_lock)
            {
                return _keys.ToArray();
            }
        }
    }

    /// <summary>
    /// Executes KeyDown.
    /// </summary>
    /// <param name="usage">The usage.</param>
    /// <param name="modifiers">The modifiers.</param>
    public void KeyDown(byte usage, byte? modifiers)
    {
        lock (_lock)
        {
            if (modifiers.HasValue) Modifiers = modifiers.Value;
            if (_keys.Contains(usage)) return;
            if (_keys.Count >= 6) return;
            _keys.Add(usage);
        }
    }

    /// <summary>
    /// Executes KeyUp.
    /// </summary>
    /// <param name="usage">The usage.</param>
    /// <param name="modifiers">The modifiers.</param>
    public void KeyUp(byte usage, byte? modifiers)
    {
        lock (_lock)
        {
            if (modifiers.HasValue) Modifiers = modifiers.Value;
            _keys.Remove(usage);
        }
    }

    /// <summary>
    /// Executes ModifierDown.
    /// </summary>
    /// <param name="bit">The bit.</param>
    /// <param name="modifiers">The modifiers.</param>
    public void ModifierDown(byte bit, byte? modifiers)
    {
        lock (_lock)
        {
            if (modifiers.HasValue) Modifiers = modifiers.Value;
            Modifiers = (byte)(Modifiers | bit);
        }
    }

    /// <summary>
    /// Executes ModifierUp.
    /// </summary>
    /// <param name="bit">The bit.</param>
    /// <param name="modifiers">The modifiers.</param>
    public void ModifierUp(byte bit, byte? modifiers)
    {
        lock (_lock)
        {
            if (modifiers.HasValue) Modifiers = modifiers.Value;
            Modifiers = (byte)(Modifiers & ~bit);
        }
    }

    /// <summary>
    /// Clears clear.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Modifiers = 0;
            _keys.Clear();
        }
    }
}
