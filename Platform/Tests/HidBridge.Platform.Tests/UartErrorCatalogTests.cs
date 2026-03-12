using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies normalization of raw UART firmware status bytes.
/// </summary>
public sealed class UartErrorCatalogTests
{
    [Fact]
    /// <summary>
    /// Verifies that a known firmware layout error is normalized as retryable UART failure.
    /// </summary>
    public void FromFirmwareCode_KnownLayoutMissingError_IsRetryable()
    {
        var error = UartErrorCatalog.FromFirmwareCode(0x04);

        Assert.Equal(ErrorDomain.Uart, error.Domain);
        Assert.Equal(UartErrorCatalog.UartLayoutMissing, error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    /// <summary>
    /// Verifies that unknown firmware codes remain stable through formatted fallback error ids.
    /// </summary>
    public void FromFirmwareCode_UnknownCode_UsesFormattedFallbackCode()
    {
        var error = UartErrorCatalog.FromFirmwareCode(0xFE);

        Assert.Equal("E_UART_DEVICE_ERROR_0xFE", error.Code);
        Assert.False(error.Retryable);
    }
}
