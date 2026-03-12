using HidBridge.Hid;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies HID report serialization helpers used by the platform baseline.
/// </summary>
public sealed class HidReportsTests
{
    [Fact]
    /// <summary>
    /// Verifies that boot-mouse serialization clamps oversized relative axis values.
    /// </summary>
    public void BuildBootMouse_ClampsRelativeAxesToSignedByteRange()
    {
        var report = HidReports.BuildBootMouse(0x03, 500, -500, 12);

        Assert.Equal(new byte[] { 0x03, 127, 129, 12 }, report);
    }

    [Fact]
    /// <summary>
    /// Verifies that keyboard reports honor parsed layout length and report identifier.
    /// </summary>
    public void BuildKeyboardReport_UsesLayoutReportIdAndLength()
    {
        var layout = new KeyboardLayoutInfo(ReportId: 2, ReportLen: 9, HasReportId: true);

        var report = HidReports.BuildKeyboardReport(layout, modifiers: 0x02, 0x04, 0x05);

        Assert.Equal(9, report.Length);
        Assert.Equal(2, report[0]);
        Assert.Equal(0x02, report[1]);
        Assert.Equal(0x04, report[3]);
        Assert.Equal(0x05, report[4]);
    }

    [Fact]
    /// <summary>
    /// Verifies that shifted ASCII characters map to the expected modifier and HID usage.
    /// </summary>
    public void TryMapAsciiToHidKey_MapsShiftedAsciiCharacters()
    {
        var ok = HidReports.TryMapAsciiToHidKey('A', out var modifiers, out var usage);

        Assert.True(ok);
        Assert.Equal(0x02, modifiers);
        Assert.Equal(0x04, usage);
    }
}
