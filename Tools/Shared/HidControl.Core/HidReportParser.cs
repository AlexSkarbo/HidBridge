using System.Collections.Concurrent;

namespace HidControl.Core;

// HID descriptor parsing helpers for mouse layouts.
/// <summary>
/// Core model for HidField.
/// </summary>
/// <param name="UsagePage">UsagePage.</param>
/// <param name="Usage">Usage.</param>
/// <param name="BitOffset">BitOffset.</param>
/// <param name="BitSize">BitSize.</param>
public sealed record HidField(int UsagePage, int Usage, int BitOffset, int BitSize);

/// <summary>
/// Core model for MouseReportLayout.
/// </summary>
/// <param name="ReportId">ReportId.</param>
/// <param name="TotalBits">TotalBits.</param>
/// <param name="Fields">Fields.</param>
public sealed partial record MouseReportLayout(byte ReportId, int TotalBits, IReadOnlyList<HidField> Fields)
{
    /// <summary>
    /// Tries to parse.
    /// </summary>
    /// <param name="desc">The desc.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Result.</returns>
    public static bool TryParse(ReadOnlySpan<byte> desc, out MouseReportLayout layout)
    {
        return HidReportParser.TryParseMouseLayout(desc, out layout);
    }

    /// <summary>
    /// Builds build.
    /// </summary>
    /// <param name="buttons">The buttons.</param>
    /// <param name="dx">The dx.</param>
    /// <param name="dy">The dy.</param>
    /// <param name="wheel">The wheel.</param>
    /// <returns>Result.</returns>
    public byte[] Build(byte buttons, int dx, int dy, int wheel)
    {
        return HidReports.BuildMouseFromLayout(this, buttons, dx, dy, wheel);
    }

    /// <summary>
    /// Builds build.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="buttons">The buttons.</param>
    /// <param name="dx">The dx.</param>
    /// <param name="dy">The dy.</param>
    /// <param name="wheel">The wheel.</param>
    /// <returns>Result.</returns>
    public static byte[] Build(MouseReportLayout layout, byte buttons, int dx, int dy, int wheel)
    {
        return HidReports.BuildMouseFromLayout(layout, buttons, dx, dy, wheel);
    }
}

// Parses HID report descriptors into structured layouts.
/// <summary>
/// Core model for HidReportParser.
/// </summary>
public static class HidReportParser
{
    /// <summary>
    /// Tries to parse mouse layout.
    /// </summary>
    /// <param name="desc">The desc.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Result.</returns>
    public static bool TryParseMouseLayout(ReadOnlySpan<byte> desc, out MouseReportLayout layout)
    {
        layout = default!;
        if (desc.Length == 0) return false;

        var layouts = new Dictionary<byte, ReportLayout>();
        byte reportId = 0;

        int usagePage = 0;
        int reportSize = 0;
        int reportCount = 0;
        int usageMin = -1;
        int usageMax = -1;
        var usageList = new List<int>();

        int i = 0;
        while (i < desc.Length)
        {
            byte b = desc[i++];
            if (b == 0xFE)
            {
                if (i + 1 >= desc.Length) break;
                int dataLen = desc[i++];
                i++; // long item tag
                i += dataLen;
                continue;
            }

            int size = b & 0x03;
            if (size == 3) size = 4;
            int type = (b >> 2) & 0x03;
            int tag = (b >> 4) & 0x0F;

            if (i + size > desc.Length) break;
            int data = 0;
            for (int n = 0; n < size; n++)
            {
                data |= desc[i + n] << (8 * n);
            }
            i += size;

            switch (type)
            {
                case 0x01: // Global
                    switch (tag)
                    {
                        case 0x00: usagePage = data; break; // Usage Page
                        case 0x07: reportSize = data; break; // Report Size
                        case 0x09: reportCount = data; break; // Report Count
                        case 0x08: reportId = (byte)data; break; // Report ID
                    }
                    break;
                case 0x02: // Local
                    switch (tag)
                    {
                        case 0x00: usageList.Add(data); break; // Usage
                        case 0x01: usageMin = data; break; // Usage Min
                        case 0x02: usageMax = data; break; // Usage Max
                    }
                    break;
                case 0x00: // Main
                    if (tag == 0x08) // Input
                    {
                        if (reportSize <= 0 || reportCount <= 0)
                        {
                            usageList.Clear();
                            usageMin = usageMax = -1;
                            break;
                        }

                        bool isConstant = (data & 0x01) != 0;
                        ReportLayout layoutForId = GetLayout(layouts, reportId);

                        int startOffset = layoutForId.TotalBits;
                        layoutForId.TotalBits += reportSize * reportCount;

                        if (!isConstant)
                        {
                            var usages = BuildUsageList(usageList, usageMin, usageMax, reportCount);
                            for (int idx = 0; idx < reportCount; idx++)
                            {
                                int usage = usages.Count > idx ? usages[idx] : 0;
                                int bitOffset = startOffset + (idx * reportSize);
                                layoutForId.Fields.Add(new HidField(usagePage, usage, bitOffset, reportSize));
                            }
                        }

                        usageList.Clear();
                        usageMin = usageMax = -1;
                    }
                    else if (tag == 0x09) // Output
                    {
                        usageList.Clear();
                        usageMin = usageMax = -1;
                    }
                    else if (tag == 0x0B) // Feature
                    {
                        usageList.Clear();
                        usageMin = usageMax = -1;
                    }
                    break;
            }
        }

        MouseReportLayout? best = FindMouseLayout(layouts);
        if (best is null) return false;

        layout = best;
        return true;
    }

    /// <summary>
    /// Gets layout.
    /// </summary>
    /// <param name="layouts">The layouts.</param>
    /// <param name="reportId">The reportId.</param>
    /// <returns>Result.</returns>
    private static ReportLayout GetLayout(Dictionary<byte, ReportLayout> layouts, byte reportId)
    {
        if (!layouts.TryGetValue(reportId, out ReportLayout? layout))
        {
            layout = new ReportLayout(reportId);
            layouts[reportId] = layout;
        }
        return layout;
    }

    /// <summary>
    /// Builds usage list.
    /// </summary>
    /// <param name="usageList">The usageList.</param>
    /// <param name="min">The min.</param>
    /// <param name="max">The max.</param>
    /// <param name="count">The count.</param>
    /// <returns>Result.</returns>
    private static List<int> BuildUsageList(List<int> usageList, int min, int max, int count)
    {
        var result = new List<int>(count);
        if (usageList.Count > 0)
        {
            result.AddRange(usageList);
            return result;
        }

        if (min >= 0 && max >= min)
        {
            for (int u = min; u <= max && result.Count < count; u++)
            {
                result.Add(u);
            }
        }
        return result;
    }

    /// <summary>
    /// Executes FindMouseLayout.
    /// </summary>
    /// <param name="layouts">The layouts.</param>
    /// <returns>Result.</returns>
    private static MouseReportLayout? FindMouseLayout(Dictionary<byte, ReportLayout> layouts)
    {
        foreach (ReportLayout layout in layouts.Values)
        {
            bool hasX = layout.Fields.Exists(f => f.UsagePage == 0x01 && f.Usage == 0x30);
            bool hasY = layout.Fields.Exists(f => f.UsagePage == 0x01 && f.Usage == 0x31);
            if (hasX && hasY)
            {
                return new MouseReportLayout(layout.ReportId, layout.TotalBits, layout.Fields);
            }
        }
        return null;
    }

    /// <summary>
    /// Core model for ReportLayout.
    /// </summary>
    private sealed class ReportLayout
    {
        public byte ReportId { get; }
        public int TotalBits { get; set; }
        public List<HidField> Fields { get; } = new();

        /// <summary>
        /// Executes ReportLayout.
        /// </summary>
        /// <param name="reportId">The reportId.</param>
        public ReportLayout(byte reportId)
        {
            ReportId = reportId;
        }
    }
}
