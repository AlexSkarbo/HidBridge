namespace HidBridge.Contracts;

/// <summary>
/// Maps firmware-level UART status bytes to contract-level error descriptors.
/// </summary>
public static class UartErrorCatalog
{
    public const string UartBadLength = "E_UART_DEVICE_ERROR_0x01";
    public const string UartInjectFailed = "E_UART_DEVICE_ERROR_0x02";
    public const string UartDescriptorMissing = "E_UART_DEVICE_ERROR_0x03";
    public const string UartLayoutMissing = "E_UART_DEVICE_ERROR_0x04";

    /// <summary>
    /// Converts a firmware error code into a stable <see cref="ErrorInfo"/> contract value.
    /// </summary>
    /// <param name="code">The raw device status byte returned by the UART firmware.</param>
    /// <returns>A normalized error object that can be surfaced by connectors and APIs.</returns>
    public static ErrorInfo FromFirmwareCode(byte code)
    {
        return code switch
        {
            0x01 => new ErrorInfo(ErrorDomain.Uart, UartBadLength, "UART device reported bad length", false),
            0x02 => new ErrorInfo(ErrorDomain.Uart, UartInjectFailed, "UART device failed to inject report", true),
            0x03 => new ErrorInfo(ErrorDomain.Uart, UartDescriptorMissing, "UART device is missing report descriptor", true),
            0x04 => new ErrorInfo(ErrorDomain.Uart, UartLayoutMissing, "UART device is missing report layout", true),
            _ => new ErrorInfo(ErrorDomain.Uart, $"E_UART_DEVICE_ERROR_0x{code:X2}", "UART device returned unknown error", false),
        };
    }
}
