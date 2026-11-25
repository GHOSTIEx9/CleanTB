using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CleanTB;

/// <summary>
/// Queries battery percentages for 2.4 GHz USB dongles, wireless receivers, and USB-HID controllers
/// that do not populate Windows Bluetooth PKEY_Device_BatteryLifePercent.
/// Supports:
/// - Standard USB HID Battery System / Power Device (UsagePage 0x85 / 0x84 / 0x06)
/// - Logitech HID++ 2.0 / 1.0 (Unifying, Bolt, Lightspeed receivers, VID 0x046D)
/// - Razer HyperSpeed / Wireless mice &amp; keyboards (OpenRazer 90/91-byte Feature Report, VID 0x1532)
/// - Sony DualSense / DualSense Edge &amp; DualShock 4 / CUH-ZWA1 dongle (VID 0x054C)
/// - SteelSeries Arctis / Nova wireless headsets (VID 0x1038)
/// - Corsair Void / Virtuoso / HS70 / HS80 wireless headsets (VID 0x1B1C)
/// - HyperX Cloud II Wireless / Cloud Alpha Wireless (VID 0x03F0 / 0x0951)
/// </summary>
internal sealed class DongleBatteryMonitor : IDisposable
{
    private const ushort VendorLogitech = 0x046D;
    private const ushort VendorRazer = 0x1532;
    private const ushort VendorSony = 0x054C;
    private const ushort VendorSteelSeries = 0x1038;
    private const ushort VendorCorsair = 0x1B1C;
    private const ushort VendorHyperXHp = 0x03F0;
    private const ushort VendorHyperXKingston = 0x0951;

    private const byte HidppSoftwareId = 0x0A; // CleanTB distinct HID++ SW ID (1..15)
    private const int HidIoTimeoutMs = 90;

    // Cache Logitech paired slot resolution per HID path to avoid scanning unused slots 1..6 every cycle.
    private sealed class LogitechSlotCache
    {
        public byte DeviceIndex;
        public byte FeatureIndex;
        public ushort FeatureId; // 0x1004, 0x1000, or 0x0081 (HID++ 1.0 reg)
        public string? Name;
        public PeripheralType Type;
    }

    private readonly Dictionary<string, List<LogitechSlotCache>> _logitechCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _unsupportedBackoff = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void InvalidateCache()
    {
        lock (_logitechCache)
        {
            _logitechCache.Clear();
            _unsupportedBackoff.Clear();
        }
        DongleBrandProtocols.ClearCache();
    }

    public List<BatteryDeviceInfo> PollHidDongles()
    {
        var results = new List<BatteryDeviceInfo>();
        if (_disposed) return results;

        var enumerated = EnumerateHidDevicePaths();
        if (enumerated.Count == 0) return results;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logicalGroups = new Dictionary<string, List<HidChannelInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in enumerated)
        {
            if (_disposed) break;

            if (ShouldSkipBackedOffPath(path))
                continue;

            try
            {
                PollSingleHidInterface(path, results, seenIds, logicalGroups);
            }
            catch
            {
                MarkBackoff(path, 10);
            }
        }

        // Dispatch grouped multi-collection dongles (ROG Omni/Mice/Headsets, Turtle Beach, GameSir,
        // FlyDigi, Switch Pro, 8BitDo, BEITONG, CompX/Redragon, Beken/Attack Shark, SinoWealth, Keychron, etc.)
        foreach (var group in logicalGroups.Values)
        {
            if (_disposed) break;
            try
            {
                DongleBrandProtocols.PollLogicalGroup(group, results, seenIds);
            }
            catch { }
        }

        return results;
    }

    private bool ShouldSkipBackedOffPath(string path)
    {
        lock (_logitechCache)
        {
            if (_unsupportedBackoff.TryGetValue(path, out int ticksRemaining) && ticksRemaining > 0)
            {
                _unsupportedBackoff[path] = ticksRemaining - 1;
                return true;
            }
            return false;
        }
    }

    private void MarkBackoff(string path, int polls)
    {
        lock (_logitechCache)
        {
            _unsupportedBackoff[path] = polls;
        }
    }

    private void PollSingleHidInterface(
        string path,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds,
        Dictionary<string, List<HidChannelInfo>> logicalGroups)
    {
        // Step 1: Open with dwDesiredAccess = 0 to inspect VID/PID & HID caps without exclusive lock interference.
        using var metaHandle = OpenHidHandle(path, readWrite: false, overlapped: false, desiredAccessZero: true);
        if (metaHandle == null || metaHandle.IsInvalid)
            return;

        if (!TryGetAttributes(metaHandle, out ushort vid, out ushort pid, out _))
            return;

        if (!TryGetCaps(metaHandle, out HIDP_CAPS caps))
            return;

        string? usbProductName = GetProductString(metaHandle);

        // Record non-standard / brand channels into logical receiver groups so dual-interface dongles can pair collections.
        if (vid != 0)
        {
            string instanceKey = ExtractReceiverInstanceKey(path);
            string groupKey = $"{vid:X4}:{pid:X4}:{instanceKey}";
            if (!logicalGroups.TryGetValue(groupKey, out var group))
            {
                group = new List<HidChannelInfo>();
                logicalGroups[groupKey] = group;
            }
            group.Add(new HidChannelInfo
            {
                Path = path,
                InstanceKey = instanceKey,
                VendorId = vid,
                ProductId = pid,
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                InputReportByteLength = caps.InputReportByteLength,
                OutputReportByteLength = caps.OutputReportByteLength,
                FeatureReportByteLength = caps.FeatureReportByteLength,
                ProductName = usbProductName
            });
        }

        // Route 1: Logitech Unifying / Bolt / Lightspeed Receivers (HID++ on UsagePage 0xFF00 / 0xFF43)
        if (vid == VendorLogitech && (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF43))
        {
            // Focus on Long HID++ (20 bytes) or Short HID++ (7 bytes) collections
            if (caps.OutputReportByteLength is 20 or 7 || caps.UsagePage == 0xFF43)
            {
                PollLogitechHidpp(path, pid, caps, usbProductName, results, seenIds);
                return;
            }
        }

        // Route 2: Razer Wireless Mice / Keyboards / Receivers (90 or 91 byte Feature Report)
        if (vid == VendorRazer && (caps.FeatureReportByteLength == 91 || caps.FeatureReportByteLength == 90))
        {
            PollRazerFeatureReport(path, pid, caps, usbProductName, results, seenIds);
            return;
        }

        // Route 3: Sony DualSense / DualShock 4 / DS4 USB Wireless Dongle (0x0BA0)
        if (vid == VendorSony && caps.UsagePage == 0x01 && (caps.Usage == 0x05 || caps.Usage == 0x04))
        {
            PollSonyController(path, pid, caps, usbProductName, results, seenIds);
            return;
        }

        // Route 4: SteelSeries Wireless Headsets (Arctis / Nova)
        if (vid == VendorSteelSeries && (caps.UsagePage == 0xFFC0 || caps.UsagePage == 0xFF43 || caps.UsagePage == 0xFF00))
        {
            PollSteelSeriesHeadset(path, pid, caps, usbProductName, results, seenIds);
            return;
        }

        // Route 5: Corsair Wireless Headsets (Void / HS70 / HS80 / Virtuoso)
        if (vid == VendorCorsair && (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF42 || caps.UsagePage == 0xFFC5 || IsCorsairV2HeadsetPid(pid)))
        {
            PollCorsairHeadset(path, pid, caps, usbProductName, results, seenIds);
            return;
        }

        // Route 6: HyperX Wireless Headsets (Cloud II Wireless / Cloud Alpha Wireless)
        if ((vid == VendorHyperXHp || vid == VendorHyperXKingston) &&
            (caps.UsagePage == 0xFF90 || caps.UsagePage == 0xFF00 || caps.UsagePage == 0x01C0 || caps.UsagePage >= 0xFF00))
        {
            PollHyperXHeadset(path, vid, pid, caps, usbProductName, results, seenIds);
            return;
        }

        // Route 7: Generic USB HID Battery System / Power Device (UsagePage 0x85)
        if (vid != 0 && TryPollStandardHidBattery(metaHandle, path, vid, pid, caps, usbProductName, out var stdDev))
        {
            if (seenIds.Add(stdDev.Id))
                results.Add(stdDev);
            return;
        }

        // Leave known brand/gamepad collections un-backed-off so grouped polling reaches all channels;
        // back off unrelated standard desktop mice/keyboard/consumer collections.
        if (caps.UsagePage < 0xFF00 && vid is not (DongleBrandProtocols.VidGameSir or DongleBrandProtocols.VidFlyDigi
                or DongleBrandProtocols.VidNintendo or DongleBrandProtocols.Vid8BitDo or DongleBrandProtocols.VidBeken
                or DongleBrandProtocols.VidBeitong or DongleBrandProtocols.VidMchose))
        {
            MarkBackoff(path, 20);
        }
    }

    internal static string ExtractReceiverInstanceKey(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string[] parts = path.ToLowerInvariant().Split('#');
        if (parts.Length < 3) return path.ToLowerInvariant();
        string inst = parts[2];
        int cut = inst.LastIndexOf('&');
        return cut > 0 ? inst[..cut] : inst;
    }

    // =========================================================================
    //  1. Standard USB HID Battery System (UsagePage 0x85)
    // =========================================================================

    private static bool TryPollStandardHidBattery(
        SafeFileHandle metaHandle,
        string path,
        ushort vid,
        ushort pid,
        HIDP_CAPS caps,
        string? productName,
        out BatteryDeviceInfo device)
    {
        device = default;

        // Only query explicit HID Battery System collections or devices that expose UsagePage 0x85 Feature values.
        if (caps.NumberFeatureValueCaps == 0 && caps.UsagePage != 0x85)
            return false;

        // Skip dedicated UPS desktop power supplies (UsagePage 0x84 with 0x85 UPS usages like APC / CyberPower)
        if (vid == 0x051D || vid == 0x0764 || vid == 0x09AE)
            return false;

        if (!HidD_GetPreparsedData(metaHandle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            return false;

        try
        {
            if (caps.NumberFeatureValueCaps == 0)
                return false;

            ushort count = caps.NumberFeatureValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[count];
            if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Feature, valueCaps, ref count, preparsed) != HIDP_STATUS_SUCCESS)
                return false;

            foreach (var vc in valueCaps)
            {
                if (vc.UsagePage != 0x85) continue;
                ushort usage = vc.IsRange != 0 ? vc.Range.UsageMin : vc.NotRange.Usage;
                // 0x65 = AbsoluteStateOfCharge, 0x66 = RelativeStateOfCharge, 0x67 = RemainingCapacity
                if (usage != 0x65 && usage != 0x66 && usage != 0x67) continue;
                if (caps.FeatureReportByteLength <= 1) continue;

                byte[] report = new byte[caps.FeatureReportByteLength];
                report[0] = vc.ReportID;
                if (!HidD_GetFeature(metaHandle, report, (uint)report.Length))
                    continue;

                if (HidP_GetUsageValue(
                        HIDP_REPORT_TYPE.HidP_Feature,
                        vc.UsagePage,
                        vc.LinkCollection,
                        usage,
                        out uint rawValue,
                        preparsed,
                        report,
                        (uint)report.Length) == HIDP_STATUS_SUCCESS)
                {
                    int pct = NormalizeHidBatteryPercent(rawValue, vc.LogicalMin, vc.LogicalMax);
                    if (pct >= 0 && pct <= 100)
                    {
                        string name = CleanDongleName(productName, $"HID Device ({vid:X4}:{pid:X4})");
                        var type = PeripheralBatteryMonitor.ClassifyDevice(name);
                        device = new BatteryDeviceInfo($"HID_{vid:X4}_{pid:X4}_{vc.ReportID}", name, type, pct, true);
                        return true;
                    }
                }
            }
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }

        return false;
    }

    internal static int NormalizeHidBatteryPercent(uint rawValue, int logicalMin, int logicalMax)
    {
        if (logicalMax > logicalMin && logicalMax != 100)
        {
            double norm = (double)(rawValue - logicalMin) / (logicalMax - logicalMin) * 100.0;
            return Math.Clamp((int)Math.Round(norm), 0, 100);
        }
        return rawValue <= 100 ? (int)rawValue : -1;
    }

    // =========================================================================
    //  2. Logitech HID++ 2.0 / 1.0 (Unifying, Bolt, Lightspeed Receivers)
    // =========================================================================

    private void PollLogitechHidpp(
        string path,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        using var ioHandle = OpenHidHandle(path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (ioHandle == null || ioHandle.IsInvalid)
        {
            MarkBackoff(path, 5);
            return;
        }

        DrainPendingReads(ioHandle, caps.InputReportByteLength);

        List<LogitechSlotCache>? cachedSlots;
        lock (_logitechCache)
        {
            _logitechCache.TryGetValue(path, out cachedSlots);
        }

        bool useLongReport = caps.OutputReportByteLength >= 20;
        int outLen = Math.Max((int)caps.OutputReportByteLength, useLongReport ? 20 : 7);
        int inLen = Math.Max((int)caps.InputReportByteLength, 20);

        if (cachedSlots != null && cachedSlots.Count > 0)
        {
            bool anyOk = false;
            foreach (var slot in cachedSlots)
            {
                if (TryQueryLogitechCachedSlot(ioHandle, outLen, inLen, useLongReport, slot, out int pct))
                {
                    anyOk = true;
                    string id = $"HIDPP_{pid:X4}_{slot.DeviceIndex}";
                    if (seenIds.Add(id))
                    {
                        string name = slot.Name ?? CleanDongleName(usbProductName, "Logitech Wireless");
                        results.Add(new BatteryDeviceInfo(id, name, slot.Type, pct, true));
                    }
                }
            }
            if (anyOk) return;

            // Receiver state changed (mouse went to sleep/reconnected) -> invalidate cache cleanly
            lock (_logitechCache) _logitechCache.Remove(path);
        }

        // Full slot discovery: 0xFF (direct Lightspeed/wireless USB) and 0x01..0x06 (Unifying/Bolt slots)
        var discovered = new List<LogitechSlotCache>();
        byte[] indices = { 0xFF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };

        foreach (byte devIdx in indices)
        {
            if (TryDiscoverLogitechSlot(ioHandle, outLen, inLen, useLongReport, devIdx, usbProductName, out var slot, out int pct))
            {
                discovered.Add(slot);
                string id = $"HIDPP_{pid:X4}_{devIdx}";
                if (seenIds.Add(id))
                {
                    string name = slot.Name ?? CleanDongleName(usbProductName, "Logitech Wireless");
                    results.Add(new BatteryDeviceInfo(id, name, slot.Type, pct, true));
                }

                // Direct 0xFF device normally doesn't host sub-devices 1..6 unless unified receiver
                if (devIdx == 0xFF && discovered.Count > 0)
                    break;
            }
        }

        if (discovered.Count > 0)
        {
            lock (_logitechCache) _logitechCache[path] = discovered;
        }
        else
        {
            // Back off briefly if receiver currently has no awake/paired devices
            MarkBackoff(path, 2);
        }
    }

    private static bool TryDiscoverLogitechSlot(
        SafeFileHandle ioHandle,
        int outLen,
        int inLen,
        bool useLongReport,
        byte devIdx,
        string? usbProductName,
        out LogitechSlotCache slot,
        out int pct)
    {
        slot = null!;
        pct = -1;

        // Try UnifiedBattery (0x1004) then BatteryLevelStatus (0x1000) via Root (0x00) Func 0
        ushort[] candidateFeatures = { 0x1004, 0x1000 };
        foreach (ushort featId in candidateFeatures)
        {
            byte[] req = BuildHidppPacket(outLen, useLongReport, devIdx, 0x00, 0x00, HidppSoftwareId,
                (byte)(featId >> 8), (byte)(featId & 0xFF));

            if (TryHidppExchange(ioHandle, req, inLen, devIdx, out byte[] resp, expectedSwId: HidppSoftwareId))
            {
                if (IsHidppErrorResponse(resp))
                    continue;

                byte featIdx = resp[4];
                if (featIdx == 0)
                    continue;

                int funcId = featId == 0x1004 ? 0x01 : 0x00;
                byte[] batReq = BuildHidppPacket(outLen, useLongReport, devIdx, featIdx, (byte)funcId, HidppSoftwareId);
                if (TryHidppExchange(ioHandle, batReq, inLen, devIdx, out byte[] batResp, expectedSwId: HidppSoftwareId) &&
                    TryParseHidpp20Battery(batResp, featId, out pct))
                {
                    var (resolvedName, resolvedType) = QueryLogitechNameAndType(ioHandle, outLen, inLen, useLongReport, devIdx, usbProductName);
                    slot = new LogitechSlotCache
                    {
                        DeviceIndex = devIdx,
                        FeatureIndex = featIdx,
                        FeatureId = featId,
                        Name = resolvedName,
                        Type = resolvedType
                    };
                    return true;
                }
            }
        }

        // Legacy HID++ 1.0 Unifying fallback (Register 0x0D Battery Mileage, then 0x07 Battery Status)
        if (devIdx <= 0x06)
        {
            foreach (byte reg in new byte[] { 0x0D, 0x07 })
            {
                byte[] regReq = new byte[Math.Max(outLen, 7)];
                regReq[0] = 0x10;
                regReq[1] = devIdx;
                regReq[2] = 0x81; // GET_REGISTER
                regReq[3] = reg;

                if (TryHidppExchange(ioHandle, regReq, inLen, devIdx, out byte[] regResp, expectedSwId: null))
                {
                    if (TryParseHidpp10Battery(regResp, reg, out pct))
                    {
                        string name = CleanDongleName(usbProductName, $"Logitech Wireless #{devIdx}");
                        slot = new LogitechSlotCache
                        {
                            DeviceIndex = devIdx,
                            FeatureIndex = 0x81,
                            FeatureId = reg,
                            Name = name,
                            Type = PeripheralBatteryMonitor.ClassifyDevice(name)
                        };
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryQueryLogitechCachedSlot(
        SafeFileHandle ioHandle,
        int outLen,
        int inLen,
        bool useLongReport,
        LogitechSlotCache slot,
        out int pct)
    {
        pct = -1;
        if (slot.FeatureId is 0x1004 or 0x1000)
        {
            int funcId = slot.FeatureId == 0x1004 ? 0x01 : 0x00;
            byte[] req = BuildHidppPacket(outLen, useLongReport, slot.DeviceIndex, slot.FeatureIndex, (byte)funcId, HidppSoftwareId);
            if (TryHidppExchange(ioHandle, req, inLen, slot.DeviceIndex, out byte[] resp, expectedSwId: HidppSoftwareId))
                return TryParseHidpp20Battery(resp, slot.FeatureId, out pct);
            return false;
        }

        byte[] regReq = new byte[Math.Max(outLen, 7)];
        regReq[0] = 0x10;
        regReq[1] = slot.DeviceIndex;
        regReq[2] = 0x81;
        regReq[3] = (byte)slot.FeatureId;
        if (TryHidppExchange(ioHandle, regReq, inLen, slot.DeviceIndex, out byte[] regResp, expectedSwId: null))
            return TryParseHidpp10Battery(regResp, (byte)slot.FeatureId, out pct);

        return false;
    }

    private static (string Name, PeripheralType Type) QueryLogitechNameAndType(
        SafeFileHandle ioHandle,
        int outLen,
        int inLen,
        bool useLongReport,
        byte devIdx,
        string? fallbackName)
    {
        string name = CleanDongleName(fallbackName, "Logitech Wireless");
        PeripheralType type = PeripheralBatteryMonitor.ClassifyDevice(name);

        // Query Feature 0x0005 (DeviceTypeAndName) via Root 0x00
        byte[] rootReq = BuildHidppPacket(outLen, useLongReport, devIdx, 0x00, 0x00, HidppSoftwareId, 0x00, 0x05);
        if (!TryHidppExchange(ioHandle, rootReq, inLen, devIdx, out byte[] rootResp, expectedSwId: HidppSoftwareId) ||
            IsHidppErrorResponse(rootResp) || rootResp[4] == 0)
        {
            return (name, type);
        }

        byte feat0005 = rootResp[4];

        // Func 2: getDeviceType -> byte 4: 0=Keyboard, 3=Mouse, 4=Trackpad, 5=Trackball, 8=Headset, 11=Joystick, 12=Gamepad
        byte[] typeReq = BuildHidppPacket(outLen, useLongReport, devIdx, feat0005, 0x02, HidppSoftwareId);
        if (TryHidppExchange(ioHandle, typeReq, inLen, devIdx, out byte[] typeResp, expectedSwId: HidppSoftwareId) &&
            !IsHidppErrorResponse(typeResp) && typeResp.Length > 4)
        {
            type = typeResp[4] switch
            {
                0 or 2 or 13 => PeripheralType.Keyboard,
                3 or 4 or 5 => PeripheralType.Mouse,
                8 or 14 => PeripheralType.Headphones,
                10 or 11 or 12 => PeripheralType.Controller,
                _ => type
            };
        }

        // Func 0: getDeviceNameCount -> byte 4 = length
        byte[] lenReq = BuildHidppPacket(outLen, useLongReport, devIdx, feat0005, 0x00, HidppSoftwareId);
        if (TryHidppExchange(ioHandle, lenReq, inLen, devIdx, out byte[] lenResp, expectedSwId: HidppSoftwareId) &&
            !IsHidppErrorResponse(lenResp) && lenResp.Length > 4 && lenResp[4] > 0 && lenResp[4] <= 64)
        {
            int targetLen = lenResp[4];
            var sb = new List<byte>(targetLen);
            for (int offset = 0; offset < targetLen; offset += 16)
            {
                byte[] chunkReq = BuildHidppPacket(outLen, useLongReport, devIdx, feat0005, 0x01, HidppSoftwareId, (byte)offset);
                if (!TryHidppExchange(ioHandle, chunkReq, inLen, devIdx, out byte[] chunkResp, expectedSwId: HidppSoftwareId) ||
                    IsHidppErrorResponse(chunkResp))
                    break;

                int copyCount = Math.Min(targetLen - offset, Math.Max(0, chunkResp.Length - 4));
                for (int i = 0; i < copyCount; i++)
                {
                    byte b = chunkResp[4 + i];
                    if (b == 0) break;
                    sb.Add(b);
                }
            }

            if (sb.Count >= 2)
            {
                string parsed = CleanDongleName(Encoding.UTF8.GetString(sb.ToArray()), name);
                name = parsed;
                // Re-classify using full marketing name if generic or Unknown
                if (type == PeripheralType.Other)
                    type = PeripheralBatteryMonitor.ClassifyDevice(name);
            }
        }

        return (name, type);
    }

    internal static byte[] BuildHidppPacket(
        int length,
        bool longReport,
        byte devIdx,
        byte featIdx,
        byte funcId,
        byte swId,
        byte p0 = 0,
        byte p1 = 0,
        byte p2 = 0)
    {
        byte[] pkt = new byte[length];
        pkt[0] = longReport ? (byte)0x11 : (byte)0x10;
        pkt[1] = devIdx;
        pkt[2] = featIdx;
        pkt[3] = (byte)(((funcId & 0x0F) << 4) | (swId & 0x0F));
        if (length > 4) pkt[4] = p0;
        if (length > 5) pkt[5] = p1;
        if (length > 6) pkt[6] = p2;
        return pkt;
    }

    internal static bool IsHidppErrorResponse(byte[] resp)
    {
        // HID++ 2.0 error is featIdx 0xFF; HID++ 1.0 error is subId 0x8F
        return resp.Length >= 4 && (resp[2] == 0xFF || resp[2] == 0x8F);
    }

    internal static bool TryParseHidpp20Battery(byte[] resp, ushort featureId, out int percent)
    {
        percent = -1;
        if (resp.Length < 7 || IsHidppErrorResponse(resp))
            return false;

        if (featureId == 0x1004)
        {
            // UnifiedBattery: resp[4] = state_of_charge (0..100), resp[6] = charging_status (0..3, 5=error)
            if (resp[6] >= 5) return false;
            int raw = resp[4];
            if (raw > 100) return false;
            percent = raw;
            return true;
        }

        if (featureId == 0x1000)
        {
            // BatteryLevelStatus: resp[4] = discharge level % (0..100)
            int raw = resp[4];
            if (raw > 100) return false;
            percent = raw;
            return true;
        }

        return false;
    }

    internal static bool TryParseHidpp10Battery(byte[] resp, byte register, out int percent)
    {
        percent = -1;
        if (resp.Length < 6 || resp[2] != 0x81 || resp[3] != register)
            return false;

        if (register == 0x0D)
        {
            // Register 0x0D: resp[4] is exact % 0..100
            if (resp[4] > 100) return false;
            percent = resp[4];
            return true;
        }

        if (register == 0x07)
        {
            // Register 0x07 discrete levels: 1=Critical(5%), 3=Low(25%), 5=Good(60%), 7=Full(100%)
            percent = resp[4] switch
            {
                1 => 5,
                3 => 30,
                5 => 65,
                7 => 95,
                _ => -1
            };
            return percent >= 0;
        }

        return false;
    }

    private static bool TryHidppExchange(
        SafeFileHandle handle,
        byte[] request,
        int inputLen,
        byte expectedDevIdx,
        out byte[] response,
        byte? expectedSwId)
    {
        response = Array.Empty<byte>();
        if (!WriteHidOverlapped(handle, request, HidIoTimeoutMs))
            return false;

        long deadline = Environment.TickCount64 + HidIoTimeoutMs;
        byte[] buf = new byte[Math.Max(inputLen, 20)];

        for (int attempt = 0; attempt < 6; attempt++)
        {
            int remain = (int)(deadline - Environment.TickCount64);
            if (remain <= 0) break;

            int read = ReadHidOverlapped(handle, buf, remain);
            if (read < 4) break;
            if (buf[0] != 0x10 && buf[0] != 0x11) continue;
            if (buf[1] != expectedDevIdx) continue;
            if (expectedSwId.HasValue && buf[2] != 0xFF && buf[2] != 0x8F && (buf[3] & 0x0F) != expectedSwId.Value)
                continue;

            response = new byte[read];
            Buffer.BlockCopy(buf, 0, response, 0, read);
            return true;
        }

        return false;
    }

    // =========================================================================
    //  3. Razer Wireless Mice / Keyboards / Receivers (OpenRazer Feature Report)
    // =========================================================================

    private void PollRazerFeatureReport(
        string path,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        // Fast guard against wired Razer mice (like DeathAdder Essential): skip clearly wired-only product IDs or names if needed,
        // and verify strict RAZER_CMD_SUCCESSFUL (0x02) + matching command/crc response.
        using var handle = OpenHidHandle(path, readWrite: true, overlapped: false, desiredAccessZero: false);
        if (handle == null || handle.IsInvalid)
        {
            MarkBackoff(path, 5);
            return;
        }

        bool hasReportId = caps.FeatureReportByteLength == 91;
        int bufLen = hasReportId ? 91 : 90;

        byte[] txCandidates = { 0x1F, 0x3F, 0xFF };
        foreach (byte txId in txCandidates)
        {
            byte[] report = BuildRazerBatteryRequest(hasReportId, txId);
            if (!HidD_SetFeature(handle, report, (uint)bufLen))
                continue;

            // Razer dongles need a tiny RF turnaround before HidD_GetFeature
            Thread.Sleep(4);

            byte[] resp = new byte[bufLen];
            if (hasReportId) resp[0] = 0x00;

            if (!HidD_GetFeature(handle, resp, (uint)bufLen))
                continue;

            if (TryParseRazerBatteryResponse(resp, hasReportId, txId, out int pct, out bool rejectedAsUnsupported))
            {
                string id = $"RAZER_{pid:X4}";
                if (seenIds.Add(id))
                {
                    string name = CleanDongleName(usbProductName, $"Razer Wireless ({pid:X4})");
                    var type = PeripheralBatteryMonitor.ClassifyDevice(name);
                    if (type == PeripheralType.Other)
                        type = PeripheralType.Mouse;
                    results.Add(new BatteryDeviceInfo(id, name, type, pct, true));
                }
                return;
            }

            if (rejectedAsUnsupported)
            {
                MarkBackoff(path, 30);
                return;
            }
        }
    }

    internal static byte[] BuildRazerBatteryRequest(bool hasLeadingReportId, byte transactionId)
    {
        int offset = hasLeadingReportId ? 1 : 0;
        byte[] buf = new byte[hasLeadingReportId ? 91 : 90];
        buf[offset + 0] = 0x00;          // status: new command
        buf[offset + 1] = transactionId; // 0x1F / 0x3F / 0xFF
        buf[offset + 2] = 0x00;          // remaining_packets hi
        buf[offset + 3] = 0x00;          // remaining_packets lo
        buf[offset + 4] = 0x00;          // protocol_type
        buf[offset + 5] = 0x02;          // data_size
        buf[offset + 6] = 0x07;          // command_class: Power/Battery
        buf[offset + 7] = 0x80;          // command_id: Get Battery Level
        buf[offset + 88] = ComputeRazerCrc(buf, offset);
        return buf;
    }

    internal static byte ComputeRazerCrc(byte[] buf, int offset)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++)
            crc ^= buf[offset + i];
        return crc;
    }

    internal static bool TryParseRazerBatteryResponse(
        byte[] resp,
        bool hasLeadingReportId,
        byte expectedTxId,
        out int percent,
        out bool unsupportedDevice)
    {
        percent = -1;
        unsupportedDevice = false;

        int offset = hasLeadingReportId ? 1 : 0;
        if (resp.Length < offset + 90)
            return false;

        byte status = resp[offset + 0];
        byte txId = resp[offset + 1];
        byte cmdClass = resp[offset + 6];
        byte cmdId = resp[offset + 7];

        // 0x05 = RAZER_CMD_NOT_SUPPORTED (typical for wired-only Razer mice)
        if (status == 0x05)
        {
            unsupportedDevice = true;
            return false;
        }

        // Must be RAZER_CMD_SUCCESSFUL (0x02) matching our command & transaction ID
        if (status != 0x02 || txId != expectedTxId || cmdClass != 0x07 || cmdId != 0x80)
            return false;

        // Verify XOR CRC when present
        byte expectedCrc = ComputeRazerCrc(resp, offset);
        if (resp[offset + 88] != expectedCrc && resp[offset + 88] != 0)
            return false;

        // arguments[1] is resp[offset + 9], raw value 0..255.
        // 0 means wireless mouse is turned off / sleeping / wired device returning zero-payload.
        byte rawLevel = resp[offset + 9];
        if (rawLevel == 0)
            return false;

        percent = Math.Clamp((int)Math.Round(rawLevel * 100.0 / 255.0), 1, 100);
        return true;
    }

    // =========================================================================
    //  4. Sony DualSense / DualSense Edge & DualShock 4 (+ USB Dongle 0x0BA0)
    // =========================================================================

    private void PollSonyController(
        string path,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        bool isDualSense = pid is 0x0CE6 or 0x0DF2;
        bool isDualShock4 = pid is 0x05C4 or 0x09CC or 0x0BA0;
        if (!isDualSense && !isDualShock4)
            return;

        // Opening with GENERIC_READ only (overlapped) works even alongside games/Steam Input!
        using var handle = OpenHidHandle(path, readWrite: false, overlapped: true, desiredAccessZero: false);
        if (handle == null || ioIsInvalid(handle))
            return;

        int readLen = Math.Max((int)caps.InputReportByteLength, isDualSense ? 78 : 64);
        byte[] buf = new byte[readLen];
        int bytesRead = ReadHidOverlapped(handle, buf, 45);
        if (bytesRead <= 0)
            return;

        int pct = -1;
        bool parsed = isDualSense
            ? TryParseDualSenseBattery(buf, bytesRead, out pct)
            : TryParseDualShock4Battery(buf, bytesRead, isDongle: pid == 0x0BA0, out pct);

        if (parsed)
        {
            string id = $"SONY_{pid:X4}";
            if (seenIds.Add(id))
            {
                string fallback = isDualSense ? "DualSense Wireless Controller" : "DualShock 4 Controller";
                string name = CleanDongleName(usbProductName, fallback);
                results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Controller, pct, true));
            }
        }
    }

    private static bool ioIsInvalid(SafeFileHandle h) => h.IsInvalid;

    internal static bool TryParseDualSenseBattery(byte[] report, int len, out int percent)
    {
        percent = -1;
        if (len < 54) return false;

        int idx;
        if (report[0] == 0x01 && len >= 54) idx = 53;       // USB report 0x01
        else if (report[0] == 0x31 && len >= 55) idx = 54;  // BT report 0x31 (2-byte header)
        else return false;

        byte raw = report[idx];
        int capStep = raw & 0x0F;
        int chargeStatus = (raw >> 4) & 0x0F;

        // 0x0A/0x0B/0x0F = temp/voltage/charging error
        if (chargeStatus is 0x0A or 0x0B or 0x0F)
            return false;

        if (chargeStatus == 0x02) // Full
        {
            percent = 100;
            return true;
        }

        percent = Math.Clamp(capStep * 10 + 5, 0, 100);
        return true;
    }

    internal static bool TryParseDualShock4Battery(byte[] report, int len, bool isDongle, out int percent)
    {
        percent = -1;
        int idx;
        if (report[0] == 0x01 && len >= 31) idx = 30;       // USB / 0x0BA0 dongle
        else if (report[0] == 0x11 && len >= 33) idx = 32;  // Bluetooth full report
        else return false;

        // If dongle has no controller linked yet, battery/status byte is 0x00 and frame bytes are blank
        byte status0 = report[idx];
        int cap = status0 & 0x0F;
        bool cable = (status0 & 0x10) != 0;

        if (isDongle && status0 == 0x00)
            return false;

        if (cable)
            percent = cap >= 10 ? 100 : Math.Clamp(cap * 10 + 5, 0, 100);
        else
            percent = Math.Clamp(cap * 10 + 5, 0, 100);

        return true;
    }

    // =========================================================================
    //  5. SteelSeries Arctis / Nova Wireless Headsets (VID 0x1038)
    // =========================================================================

    private void PollSteelSeriesHeadset(
        string path,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        using var handle = OpenHidHandle(path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (handle == null || handle.IsInvalid)
            return;

        DrainPendingReads(handle, caps.InputReportByteLength);

        int outLen = Math.Max((int)caps.OutputReportByteLength, 64);
        int inLen = Math.Max((int)caps.InputReportByteLength, 64);

        byte[]? req = null;
        if (IsSteelSeriesArctis7LegacyPid(pid))
            req = PadPacket(outLen, 0x06, 0x18);
        else if (IsSteelSeriesArctis1Pid(pid))
            req = PadPacket(outLen, 0x06, 0x12);
        else if (pid is 0x12E0 or 0x12E5) // Nova Pro Wireless
            req = PadPacket(Math.Max(outLen, 31), 0x06, 0xB0);
        else if (IsSteelSeriesNovaOr7PlusPid(pid))
            req = PadPacket(outLen, 0x00, 0xB0);
        else if (pid is 0x12C2 or 0x12C4) // Arctis 9
            req = PadPacket(outLen, 0x00, 0x20);
        else
            return;

        if (!WriteHidOverlapped(handle, req, HidIoTimeoutMs))
            return;

        byte[] resp = new byte[Math.Max(inLen, 64)];
        int read = ReadHidOverlapped(handle, resp, HidIoTimeoutMs);
        if (read <= 0) return;

        if (TryParseSteelSeriesBattery(pid, resp, read, out int pct))
        {
            string id = $"STEELSERIES_{pid:X4}";
            if (seenIds.Add(id))
            {
                string name = CleanDongleName(usbProductName, "SteelSeries Arctis Wireless");
                results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, pct, true));
            }
        }
    }

    internal static bool TryParseSteelSeriesBattery(ushort pid, byte[] resp, int len, out int percent)
    {
        percent = -1;
        if (len < 4) return false;

        if (IsSteelSeriesArctis7LegacyPid(pid))
        {
            if (resp[2] > 100) return false;
            percent = resp[2];
            return true;
        }

        if (IsSteelSeriesArctis1Pid(pid))
        {
            if (resp[2] == 0x01 || resp[3] > 100) return false; // 0x01 = offline
            percent = resp[3];
            return true;
        }

        if (pid is 0x12E0 or 0x12E5) // Nova Pro Wireless Base Station
        {
            if (len < 16 || resp[15] == 0x01 || resp[6] > 8) return false;
            percent = Math.Clamp((int)Math.Round(resp[6] * 100.0 / 8.0), 0, 100);
            return true;
        }

        if (pid is 0x220E or 0x2212 or 0x2216 or 0x2236) // Arctis 7+
        {
            if (resp[1] == 0x01 || resp[2] > 4) return false;
            percent = Math.Clamp((int)Math.Round(resp[2] * 100.0 / 4.0), 0, 100);
            return true;
        }

        if (pid is 0x2232 or 0x2253) // Arctis Nova 5
        {
            if (resp[1] == 0x02 || resp[3] > 100) return false;
            percent = resp[3];
            return true;
        }

        if (IsSteelSeriesNovaOr7PlusPid(pid)) // Arctis Nova 7 family
        {
            if (resp[3] == 0x00) return false; // headset offline
            bool isDiscrete = pid is 0x2202 or 0x2206 or 0x220A or 0x223A or 0x227A or 0x22A4;
            if (isDiscrete)
            {
                if (resp[2] > 4) return false;
                percent = Math.Clamp((int)Math.Round(resp[2] * 100.0 / 4.0), 0, 100);
            }
            else
            {
                if (resp[2] > 100) return false;
                percent = resp[2];
            }
            return true;
        }

        if (pid is 0x12C2 or 0x12C4) // Arctis 9
        {
            if (len < 5 || resp[4] == 0xFF || resp[3] > 100) return false;
            percent = resp[3];
            return true;
        }

        return false;
    }

    private static bool IsSteelSeriesArctis7LegacyPid(ushort pid) =>
        pid is 0x1260 or 0x12AD or 0x1252 or 0x1280;

    private static bool IsSteelSeriesArctis1Pid(ushort pid) =>
        pid is 0x12B3 or 0x12B6 or 0x12D5 or 0x12D7;

    private static bool IsSteelSeriesNovaOr7PlusPid(ushort pid) =>
        pid is 0x220E or 0x2212 or 0x2216 or 0x2236
            or 0x2202 or 0x22A1 or 0x227E or 0x2206 or 0x2258 or 0x229E or 0x22AD
            or 0x223A or 0x22A9 or 0x227A or 0x22A4 or 0x22A5 or 0x220A or 0x22A7
            or 0x2232 or 0x2253;

    // =========================================================================
    //  6. Corsair Void / HS70 / Virtuoso / HS80 Wireless (VID 0x1B1C)
    // =========================================================================

    private void PollCorsairHeadset(
        string path,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        using var handle = OpenHidHandle(path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (handle == null || handle.IsInvalid)
            return;

        DrainPendingReads(handle, caps.InputReportByteLength);

        if (IsCorsairV2HeadsetPid(pid))
        {
            int writeLen = Math.Max((int)caps.OutputReportByteLength, 65);
            byte[] batReq = new byte[writeLen];
            batReq[0] = 0x00;
            batReq[1] = 0x02;
            batReq[2] = 0x09; // HEADSET_ENDPOINT
            batReq[3] = 0x02;
            batReq[4] = 0x0F;

            if (!WriteHidOverlapped(handle, batReq, HidIoTimeoutMs))
                return;

            byte[] resp = new byte[Math.Max((int)caps.InputReportByteLength, 64)];
            int read = ReadHidOverlapped(handle, resp, HidIoTimeoutMs);
            if (read >= 6)
            {
                int raw = resp[4] | (resp[5] << 8);
                // Scale 0..1000 tenths of a percent or direct 0..100
                int pct = raw > 100 ? Math.Clamp((int)Math.Round(raw / 10.0), 0, 100) : Math.Clamp(raw, 0, 100);
                if (raw > 0)
                {
                    string id = $"CORSAIR_{pid:X4}";
                    if (seenIds.Add(id))
                    {
                        string name = CleanDongleName(usbProductName, "Corsair Wireless Headset");
                        results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, pct, true));
                    }
                }
            }
            return;
        }

        // Legacy Corsair Void / Void Pro / HS70 Wireless (0xC9 0x64)
        byte[] legacyReq = PadPacket(Math.Max((int)caps.OutputReportByteLength, 2), 0xC9, 0x64);
        if (!WriteHidOverlapped(handle, legacyReq, HidIoTimeoutMs))
            return;

        byte[] legacyResp = new byte[Math.Max((int)caps.InputReportByteLength, 5)];
        int legacyRead = ReadHidOverlapped(handle, legacyResp, HidIoTimeoutMs);
        if (legacyRead > 0 && TryParseCorsairVoidBattery(legacyResp, legacyRead, out int pctLegacy))
        {
            string id = $"CORSAIR_{pid:X4}";
            if (seenIds.Add(id))
            {
                string name = CleanDongleName(usbProductName, "Corsair Wireless Headset");
                results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, pctLegacy, true));
            }
        }
    }

    internal static bool TryParseCorsairVoidBattery(byte[] resp, int len, out int percent)
    {
        percent = -1;
        if (len < 5) return false;
        byte status = resp[4];
        // 0 = disconnected, 1 = normal, 2 = low, 4/5 = charging
        if (status != 1 && status != 2 && status != 4 && status != 5)
            return false;

        int lvl = resp[2] & 0x7F;
        if (lvl > 100) return false;
        percent = lvl;
        return true;
    }

    private static bool IsCorsairV2HeadsetPid(ushort pid) => pid is 0x2A08 or 0x2A02 or 0x0A97;

    // =========================================================================
    //  7. HyperX Cloud II Wireless & Cloud Alpha Wireless (0x03F0 / 0x0951)
    // =========================================================================

    private void PollHyperXHeadset(
        string path,
        ushort vid,
        ushort pid,
        HIDP_CAPS caps,
        string? usbProductName,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        using var handle = OpenHidHandle(path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (handle == null || handle.IsInvalid)
            return;

        DrainPendingReads(handle, caps.InputReportByteLength);

        if (vid == VendorHyperXHp && pid == 0x098D) // Cloud Alpha Wireless
        {
            byte[] connReq = PadPacket(Math.Max((int)caps.OutputReportByteLength, 31), 0x21, 0xBB, 0x03);
            if (!WriteHidOverlapped(handle, connReq, HidIoTimeoutMs)) return;

            byte[] connResp = new byte[Math.Max((int)caps.InputReportByteLength, 31)];
            if (ReadHidOverlapped(handle, connResp, HidIoTimeoutMs) < 4 || connResp[3] == 0x01) return;

            byte[] batReq = PadPacket(Math.Max((int)caps.OutputReportByteLength, 31), 0x21, 0xBB, 0x0B);
            if (!WriteHidOverlapped(handle, batReq, HidIoTimeoutMs)) return;

            byte[] batResp = new byte[Math.Max((int)caps.InputReportByteLength, 31)];
            if (ReadHidOverlapped(handle, batResp, HidIoTimeoutMs) >= 4 && batResp[3] <= 100)
            {
                string id = $"HYPERX_{vid:X4}_{pid:X4}";
                if (seenIds.Add(id))
                {
                    string name = CleanDongleName(usbProductName, "HyperX Cloud Alpha Wireless");
                    results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, batResp[3], true));
                }
            }
            return;
        }

        if (vid == VendorHyperXHp && (pid == 0x0696 || pid == 0x018B)) // HP Cloud II Wireless
        {
            byte[] req = PadPacket(Math.Max((int)caps.OutputReportByteLength, 52), 0x06, 0xFF, 0xBB, 0x02);
            if (!WriteHidOverlapped(handle, req, HidIoTimeoutMs)) return;

            byte[] resp = new byte[Math.Max((int)caps.InputReportByteLength, 20)];
            int read = ReadHidOverlapped(handle, resp, HidIoTimeoutMs);
            if (read >= 8 && resp[0] == 0x06 && resp[1] == 0xFF && resp[2] == 0xBB && resp[3] == 0x02 && resp[7] <= 100)
            {
                string id = $"HYPERX_{vid:X4}_{pid:X4}";
                if (seenIds.Add(id))
                {
                    string name = CleanDongleName(usbProductName, "HyperX Cloud II Wireless");
                    results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, resp[7], true));
                }
            }
            return;
        }

        if (vid == VendorHyperXKingston) // Kingston Cloud II Wireless
        {
            int wLen = Math.Max((int)caps.OutputReportByteLength, 62);
            byte[] req = new byte[wLen];
            req[0] = 0x06; req[2] = 0x02; req[4] = 0x9A; req[7] = 0x68;
            req[8] = 0x4A; req[9] = 0x8E; req[10] = 0x0A; req[14] = 0xBB; req[15] = 0x02;

            if (!WriteHidOverlapped(handle, req, HidIoTimeoutMs)) return;

            byte[] resp = new byte[Math.Max((int)caps.InputReportByteLength, 64)];
            int read = ReadHidOverlapped(handle, resp, HidIoTimeoutMs);
            if (read >= 8 && resp[0] == 0x0B && resp[2] == 0xBB && resp[3] == 0x02 && resp[7] <= 100)
            {
                string id = $"HYPERX_{vid:X4}_{pid:X4}";
                if (seenIds.Add(id))
                {
                    string name = CleanDongleName(usbProductName, "HyperX Cloud II Wireless");
                    results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Headphones, resp[7], true));
                }
            }
        }
    }

    // =========================================================================
    //  Helpers & Overlapped Win32 HID I/O
    // =========================================================================

    private static byte[] PadPacket(int length, params byte[] prefix)
    {
        byte[] arr = new byte[Math.Max(length, prefix.Length)];
        Buffer.BlockCopy(prefix, 0, arr, 0, prefix.Length);
        return arr;
    }

    internal static string CleanDongleName(string? rawName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return fallback;

        string clean = rawName.Trim();
        clean = clean.Replace("USB Receiver", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("Wireless Receiver", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("Lightspeed Receiver", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("Dongle", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("  ", " ")
                     .Trim();

        return clean.Length >= 3 ? PeripheralBatteryMonitor.CleanDeviceName(clean) : fallback;
    }

    private static List<string> EnumerateHidDevicePaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
            return paths;

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref ifaceData); i++)
            {
                uint reqSize = 0;
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                if (reqSize <= 4) continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    // On 64-bit Windows cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8, on 32-bit it is 6
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detailBuf, reqSize, out _, IntPtr.Zero))
                    {
                        string? devPath = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                        if (!string.IsNullOrEmpty(devPath))
                            paths.Add(devPath);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return paths;
    }

    internal static SafeFileHandle? OpenHidHandle(string path, bool readWrite, bool overlapped, bool desiredAccessZero)
    {
        uint access = desiredAccessZero ? 0 : (readWrite ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ);
        uint flags = overlapped ? FILE_FLAG_OVERLAPPED : 0;

        SafeFileHandle h = CreateFileW(
            path,
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        return h.IsInvalid ? null : h;
    }

    private static bool TryGetAttributes(SafeFileHandle handle, out ushort vid, out ushort pid, out ushort version)
    {
        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (HidD_GetAttributes(handle, ref attrs))
        {
            vid = attrs.VendorID;
            pid = attrs.ProductID;
            version = attrs.VersionNumber;
            return true;
        }
        vid = pid = version = 0;
        return false;
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            return false;
        try
        {
            return HidP_GetCaps(preparsed, out caps) == HIDP_STATUS_SUCCESS;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? GetProductString(SafeFileHandle handle)
    {
        byte[] buf = new byte[256];
        if (!HidD_GetProductString(handle, buf, (uint)buf.Length))
            return null;

        string s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    internal static void DrainPendingReads(SafeFileHandle overlappedHandle, ushort inputReportLen)
    {
        if (inputReportLen == 0) return;
        byte[] dump = new byte[inputReportLen];
        for (int i = 0; i < 4; i++)
        {
            if (ReadHidOverlapped(overlappedHandle, dump, 2) <= 0)
                break;
        }
    }

    internal static bool WriteHidOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
        if (ev == IntPtr.Zero) return false;

        GCHandle pinned = default;
        IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var ov = new NativeOverlapped { EventHandle = ev };
            Marshal.StructureToPtr(ov, ovPtr, false);

            bool ok = WriteFile(handle.DangerousGetHandle(), pinned.AddrOfPinnedObject(), (uint)buffer.Length, out _, ovPtr);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return false;

                if (WaitForSingleObject(ev, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelIoEx(handle.DangerousGetHandle(), ovPtr);
                    GetOverlappedResult(handle.DangerousGetHandle(), ovPtr, out _, true);
                    return false;
                }
                return GetOverlappedResult(handle.DangerousGetHandle(), ovPtr, out _, false);
            }
            return true;
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            Marshal.FreeHGlobal(ovPtr);
            CloseHandle(ev);
        }
    }

    internal static int ReadHidOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
        if (ev == IntPtr.Zero) return -1;

        GCHandle pinned = default;
        IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var ov = new NativeOverlapped { EventHandle = ev };
            Marshal.StructureToPtr(ov, ovPtr, false);

            bool ok = ReadFile(handle.DangerousGetHandle(), pinned.AddrOfPinnedObject(), (uint)buffer.Length, out uint read, ovPtr);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return -1;

                if (WaitForSingleObject(ev, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelIoEx(handle.DangerousGetHandle(), ovPtr);
                    GetOverlappedResult(handle.DangerousGetHandle(), ovPtr, out _, true);
                    return 0;
                }

                if (!GetOverlappedResult(handle.DangerousGetHandle(), ovPtr, out read, false))
                    return -1;
            }
            return (int)read;
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            Marshal.FreeHGlobal(ovPtr);
            CloseHandle(ev);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        InvalidateCache();
    }

    // =========================================================================
    //  Win32 HID / SetupAPI / Kernel32 P/Invoke
    // =========================================================================

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    private enum HIDP_REPORT_TYPE
    {
        HidP_Input = 0,
        HidP_Output = 1,
        HidP_Feature = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(17)] public byte Reserved;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public HIDP_RANGE Range;
        [FieldOffset(56)] public HIDP_NOT_RANGE NotRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_RANGE
    {
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_NOT_RANGE
    {
        public ushort Usage;
        public ushort Reserved1;
        public ushort StringIndex;
        public ushort Reserved2;
        public ushort DesignatorIndex;
        public ushort Reserved3;
        public ushort DataIndex;
        public ushort Reserved4;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(
        HIDP_REPORT_TYPE reportType,
        [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(
        HIDP_REPORT_TYPE reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        out uint usageValue,
        IntPtr preparsedData,
        byte[] report,
        uint reportLength);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
