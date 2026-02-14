using System;
using System.Collections.Generic;
using System.Linq;
using HidControl.Core;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Builds interface list DTOs and device identifiers for HID endpoints.
/// </summary>
internal static class DeviceInterfaceMapper
{
    /// <summary>
    /// Builds a device list response from stored keyboard and mouse mappings.
    /// </summary>
    /// <param name="mouseStore">Mouse mapping store.</param>
    /// <param name="keyboardStore">Keyboard mapping store.</param>
    /// <returns>Device list payload.</returns>
    public static object BuildDevicesFromMappings(MouseMappingStore mouseStore, KeyboardMappingStore keyboardStore)
    {
        var interfaces = new List<object>();
        foreach (var entry in mouseStore.ListAll())
        {
            interfaces.Add(new
            {
                devAddr = 1,
                itf = entry.Itf,
                itfProtocol = (byte)2,
                protocol = (byte)1,
                inferredType = (byte)2,
                typeName = "mouse",
                deviceId = entry.DeviceId,
                mouseButtonsCount = entry.ButtonsCount,
                reportDescHash = entry.ReportDescHash,
                active = true,
                mounted = true
            });
        }
        foreach (var entry in keyboardStore.ListAll())
        {
            interfaces.Add(new
            {
                devAddr = 1,
                itf = entry.Itf,
                itfProtocol = (byte)0,
                protocol = (byte)1,
                inferredType = (byte)1,
                typeName = "keyboard",
                deviceId = entry.DeviceId,
                reportDescHash = entry.ReportDescHash,
                active = true,
                mounted = true
            });
        }
        return new
        {
            capturedAt = DateTimeOffset.UtcNow,
            interfaces
        };
    }

    /// <summary>
    /// Maps an interface list to a lightweight response payload.
    /// </summary>
    /// <param name="list">Interface list.</param>
    /// <returns>Interface list payload.</returns>
    public static object MapInterfaceList(HidInterfaceList list)
    {
        var interfaces = list.Interfaces.Select(itf =>
        {
            byte inferred = itf.InferredType;
            string typeName = InferTypeName(inferred, itf.ItfProtocol);
            return new
            {
                devAddr = itf.DevAddr,
                itf = itf.Itf,
                itfProtocol = itf.ItfProtocol,
                protocol = itf.Protocol,
                inferredType = inferred,
                typeName,
                active = itf.Active,
                mounted = itf.Mounted
            };
        }).ToList();
        return new
        {
            capturedAt = DateTimeOffset.UtcNow,
            interfaces
        };
    }

    /// <summary>
    /// Maps an interface list with report descriptors and layout details.
    /// </summary>
    /// <param name="list">Interface list.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="includeReportDesc">Whether to include report descriptors.</param>
    /// <returns>Detailed interface list payload.</returns>
    public static object MapInterfaceListDetailed(HidInterfaceList list, HidUartClient? uart, bool includeReportDesc)
    {
        var interfaces = list.Interfaces.Select(itf =>
        {
            byte inferred = itf.InferredType;
            string typeName = InferTypeName(inferred, itf.ItfProtocol);
            string? reportHex = null;
            int reportLen = 0;
            bool truncated = false;
            string? reportHash = null;
            byte? buttonsCount = null;
            byte? keyboardReportId = null;
            byte? keyboardReportLen = null;
            bool? keyboardHasReportId = null;
            if (includeReportDesc && uart is not null)
            {
                ReportDescriptorSnapshot? desc = uart.GetReportDescriptorCopy(itf.Itf);
                if (desc is not null)
                {
                    reportHex = Convert.ToHexString(desc.Data);
                    reportLen = desc.TotalLength;
                    truncated = desc.Truncated;
                    reportHash = ComputeReportDescHash(desc.Data);
                }
                ReportLayoutSnapshot? layout = uart.GetReportLayoutCopy(itf.Itf);
                if (layout?.Mouse is not null)
                {
                    buttonsCount = layout.Mouse.ButtonsCount;
                }
                if (layout?.Keyboard is not null)
                {
                    keyboardReportId = layout.Keyboard.ReportId;
                    keyboardReportLen = layout.Keyboard.ReportLen;
                    keyboardHasReportId = layout.Keyboard.HasReportId;
                }
            }
            return new
            {
                devAddr = itf.DevAddr,
                itf = itf.Itf,
                itfProtocol = itf.ItfProtocol,
                protocol = itf.Protocol,
                inferredType = inferred,
                typeName,
                deviceId = BuildDeviceId(inferred, itf.ItfProtocol, reportHash),
                mouseButtonsCount = buttonsCount,
                keyboardReportId,
                keyboardReportLen,
                keyboardHasReportId,
                reportDescHash = reportHash,
                reportDescHex = reportHex,
                reportDescTotalLen = reportLen,
                reportDescTruncated = truncated,
                active = itf.Active,
                mounted = itf.Mounted
            };
        }).ToList();
        return new
        {
            capturedAt = DateTimeOffset.UtcNow,
            interfaces
        };
    }

    /// <summary>
    /// Infers the device type name from interface metadata.
    /// </summary>
    /// <param name="inferredType">Inferred device type code.</param>
    /// <param name="itfProtocol">Interface protocol code.</param>
    /// <returns>Type name.</returns>
    public static string InferTypeName(byte inferredType, byte itfProtocol)
    {
        if (inferredType == 2 || itfProtocol == 2) return "mouse";
        if (inferredType == 1 || itfProtocol == 1) return "keyboard";
        return "unknown";
    }

    /// <summary>
    /// Builds a stable device identifier from type and descriptor hash.
    /// </summary>
    /// <param name="inferredType">Inferred device type code.</param>
    /// <param name="itfProtocol">Interface protocol code.</param>
    /// <param name="reportDescHash">Report descriptor hash.</param>
    /// <returns>Device identifier string.</returns>
    public static string? BuildDeviceId(byte inferredType, byte itfProtocol, string? reportDescHash)
    {
        if (string.IsNullOrWhiteSpace(reportDescHash)) return null;
        string typeName = InferTypeName(inferredType, itfProtocol);
        return $"{typeName}:{reportDescHash}";
    }

    /// <summary>
    /// Computes the SHA-256 hash of a report descriptor.
    /// </summary>
    /// <param name="data">Descriptor bytes.</param>
    /// <returns>Hex-encoded hash.</returns>
    public static string ComputeReportDescHash(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }
}
