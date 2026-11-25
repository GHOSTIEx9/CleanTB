using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CleanTB;

/// <summary>
/// Descriptor for a single enumerated HID collection belonging to a USB/BT device.
/// </summary>
internal sealed class HidChannelInfo
{
    public string Path { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort InputReportByteLength { get; init; }
    public ushort OutputReportByteLength { get; init; }
    public ushort FeatureReportByteLength { get; init; }
    public string? ProductName { get; init; }
}

/// <summary>
/// Verified HID battery protocols for ASUS ROG/TUF, Turtle Beach, GameSir, FlyDigi,
/// Nintendo Switch Pro/Joy-Con, 8BitDo, BEITONG, SteelSeries mice, Razer/Logitech/HyperX/Corsair
/// extra models, EPOS, Sony Inzone, Audeze, JBL, Skullcandy, Keychron, Rapoo, CompX/Redragon,
/// Beken/Attack Shark, SinoWealth/Glorious/Lamzu, MCHOSE, AULA, VAXEE, Finalmouse, and Dareu/Endgame.
/// </summary>
internal static class DongleBrandProtocols
{
    internal const ushort VidGameSir = 0x3537;
    internal const ushort VidFlyDigi = 0x37D7;
    internal const ushort VidBeitong = 0x20BC;
    internal const ushort VidNintendo = 0x057E;
    internal const ushort Vid8BitDo = 0x2DC8;
    internal const ushort VidAsusRog = 0x0B05;
    internal const ushort VidTurtleBeach = 0x10F5;
    internal const ushort VidSteelSeries = 0x1038;
    internal const ushort VidRazer = 0x1532;
    internal const ushort VidLogitech = 0x046D;
    internal const ushort VidAstro = 0x9886;
    internal const ushort VidCorsair = 0x1B1C;
    internal const ushort VidHyperXHp = 0x03F0;
    internal const ushort VidHyperXKingston = 0x0951;
    internal const ushort VidSony = 0x054C;
    internal const ushort VidEpos = 0x1395;
    internal const ushort VidAudeze = 0x3329;
    internal const ushort VidJbl = 0x0ECB;
    internal const ushort VidSkullcandy = 0x34F0;
    internal const ushort VidSinoWealthGlorious = 0x258A;
    internal const ushort VidLamzu = 0x373E;
    internal const ushort VidMchoseAlt = 0x5253;
    internal const ushort VidMchose = 0x3837;
    internal const ushort VidMchoseHeadset = 0x291D;
    internal const ushort VidBeken = 0x1D57;
    internal const ushort VidCompxVxe = 0x3554;
    internal const ushort VidCompxPulsarRedragon = 0x25A7;
    internal const ushort VidCompxAtk = 0x373B;
    internal const ushort VidCompxPulsar2 = 0x3710;
    internal const ushort VidCompxHitscan = 0x3770;
    internal const ushort VidCompxDareu = 0x260D;
    internal const ushort VidCompxGwolves = 0x33E4;
    internal const ushort VidEndgame = 0x3367;
    internal const ushort VidAjazz = 0x249A;
    internal const ushort VidAjazzAlt = 0x3151;
    internal const ushort VidAula = 0x0C45;
    internal const ushort VidKeychron = 0x3434;
    internal const ushort VidSkn = 0x362D;
    internal const ushort VidRapoo = 0x24AE;
    internal const ushort VidVaxee = 0x3057;
    internal const ushort VidFinalmouse = 0x361D;
    internal const ushort VidWlmouse = 0x36A7;
    internal const ushort VidMchoseSinowealth = 0x2023;

    // Cache push-only / sleeping readings briefly while dongle stays plugged in
    private static readonly Dictionary<string, (BatteryDeviceInfo Info, long ExpiresAtMs)> _stickyCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache()
    {
        lock (_stickyCache) _stickyCache.Clear();
    }

    private static void SaveSticky(string key, BatteryDeviceInfo info, long ttlMs = 120_000)
    {
        lock (_stickyCache) _stickyCache[key] = (info, Environment.TickCount64 + ttlMs);
    }

    private static bool TryGetSticky(string key, out BatteryDeviceInfo info)
    {
        lock (_stickyCache)
        {
            if (_stickyCache.TryGetValue(key, out var entry) && Environment.TickCount64 < entry.ExpiresAtMs)
            {
                info = entry.Info;
                return true;
            }
            _stickyCache.Remove(key);
        }
        info = default;
        return false;
    }

    // =========================================================================
    //  Grouped Multi-Channel Dispatch (called once per logical VID+PID+Instance)
    // =========================================================================

    public static void PollLogicalGroup(
        List<HidChannelInfo> group,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        if (group.Count == 0) return;

        ushort vid = group[0].VendorId;
        ushort pid = group[0].ProductId;
        string? nameHint = null;
        foreach (var c in group)
        {
            if (!string.IsNullOrWhiteSpace(c.ProductName))
            {
                nameHint = c.ProductName;
                break;
            }
        }

        // 1. GameSir Controllers (VID 0x3537)
        if (vid == VidGameSir)
        {
            if (TryPollGameSir(group, pid, nameHint, out var gsDev) && seenIds.Add(gsDev.Id))
                results.Add(gsDev);
            // Check also if VID 0x3537 hosts a VXE R1 CompX PID 0x2106
            if (pid == 0x2106 && TryPollCompxKysonaGroup(group, vid, pid, nameHint, PeripheralType.Mouse, out var vxeDev) && seenIds.Add(vxeDev.Id))
                results.Add(vxeDev);
            return;
        }

        // 2. FlyDigi Controllers (VID 0x37D7)
        if (vid == VidFlyDigi)
        {
            if (TryPollFlyDigi(group, pid, nameHint, out var fdDev) && seenIds.Add(fdDev.Id))
                results.Add(fdDev);
            return;
        }

        // 3. BEITONG KP40D (VID 0x20BC, PID 0x515C)
        if (vid == VidBeitong && pid == 0x515C)
        {
            if (TryPollBeitong(group, pid, nameHint, out var btDev) && seenIds.Add(btDev.Id))
                results.Add(btDev);
            return;
        }

        // 4. Nintendo Switch Pro / Joy-Con (VID 0x057E)
        if (vid == VidNintendo && pid is 0x2006 or 0x2007 or 0x2009)
        {
            if (TryPollNintendoSwitch(group, pid, nameHint, out var nsDev) && seenIds.Add(nsDev.Id))
                results.Add(nsDev);
            return;
        }

        // 5. 8BitDo Controllers in D-Input mode (VID 0x2DC8)
        if (vid == Vid8BitDo && pid is 0x6000 or 0x6100 or 0x6001 or 0x6101 or 0x6003 or 0x6006 or 0x6009)
        {
            if (TryPoll8BitDo(group, pid, nameHint, out var bdDev) && seenIds.Add(bdDev.Id))
                results.Add(bdDev);
            return;
        }

        // 6. ASUS ROG / TUF Mice, Keyboards, OMNI Receiver, SpeedNova, Strix Go 2.4 / Headsets
        if (vid == VidAsusRog)
        {
            PollAsusRogGroup(group, pid, nameHint, results, seenIds);
            return;
        }

        // 7. Turtle Beach Stealth Pro II, Stealth 700 Gen 3, Atlas Air (VID 0x10F5)
        if (vid == VidTurtleBeach)
        {
            if (TryPollTurtleBeach(group, pid, nameHint, out var tbDev) && seenIds.Add(tbDev.Id))
                results.Add(tbDev);
            return;
        }

        // 8. SteelSeries Extra Mice (Rival 3 Wireless, Aerox, Prime Wireless)
        if (vid == VidSteelSeries && (IsSteelSeriesMousePid(pid)))
        {
            if (TryPollSteelSeriesMouse(group, pid, nameHint, out var ssMouse) && seenIds.Add(ssMouse.Id))
                results.Add(ssMouse);
            return;
        }

        // 9. Razer Extra Wireless Headsets (BlackShark V2 Pro 2023, BlackShark V3, Barracuda Pro, Barracuda X)
        if (vid == VidRazer && IsRazerHeadsetExtendedPid(pid))
        {
            if (TryPollRazerExtendedHeadset(group, pid, nameHint, out var rzHs) && seenIds.Add(rzHs.Id))
                results.Add(rzHs);
            return;
        }

        // 10. Logitech G Headsets (HID++ Voltage) + Centurion (PRO X 2/3) + Astro A50
        if (vid == VidLogitech || vid == VidAstro)
        {
            if (TryPollLogitechOrAstroHeadset(group, vid, pid, nameHint, out var lgHs) && seenIds.Add(lgHs.Id))
                results.Add(lgHs);
            // Fall through allowed for Logitech 0xFF00 mice (handled in DongleBatteryMonitor directly)
            if (vid == VidAstro) return;
        }

        // 11. Corsair Slipstream / Virtuoso SE & XT
        if (vid == VidCorsair && (pid == 0x1BDC || pid is 0x0A3E or 0x0A3D or 0x0A40 or 0x0A64))
        {
            if (TryPollCorsairBragi(group, pid, nameHint, out var corDev) && seenIds.Add(corDev.Id))
                results.Add(corDev);
            return;
        }

        // 12. HyperX Extra Headsets (Cloud Flight / S, Cloud III / Core / III S, Cloud Stinger 2)
        if ((vid == VidHyperXHp || vid == VidHyperXKingston) && IsHyperXExtendedPid(vid, pid))
        {
            if (TryPollHyperXExtended(group, vid, pid, nameHint, out var hxDev) && seenIds.Add(hxDev.Id))
                results.Add(hxDev);
            return;
        }

        // 13. Sony INZONE Buds (054C:0EC2)
        if (vid == VidSony && pid == 0x0EC2)
        {
            if (TryPollSonyInzone(group, pid, nameHint, out var izDev) && seenIds.Add(izDev.Id))
                results.Add(izDev);
            return;
        }

        // 14. EPOS H3PRO Hybrid (1395:0303 / 0101), Audeze Maxwell (3329), JBL Quantum 910 (0ECB:2088), Skullcandy PLYR (34F0)
        if (vid == VidEpos && pid is 0x0303 or 0x0101)
        {
            if (TryPollEposH3Pro(group, pid, nameHint, out var epDev) && seenIds.Add(epDev.Id))
                results.Add(epDev);
            return;
        }
        if (vid == VidAudeze && pid is 0x4B18 or 0x4B19 or 0x4B28 or 0x4B29)
        {
            if (TryPollAudezeMaxwell(group, pid, nameHint, out var auDev) && seenIds.Add(auDev.Id))
                results.Add(auDev);
            return;
        }
        if (vid == VidJbl && pid == 0x2088)
        {
            if (TryPollJblQuantum(group, pid, nameHint, out var jblDev) && seenIds.Add(jblDev.Id))
                results.Add(jblDev);
            return;
        }
        if (vid == VidSkullcandy && pid is 0x3210 or 0x5310)
        {
            if (TryPollSkullcandy(group, pid, nameHint, out var scDev) && seenIds.Add(scDev.Id))
                results.Add(scDev);
            return;
        }

        // 15. AULA Keyboards (0C45:FEFC) & CompX Keyboards (3554:FA09)
        if (vid == VidAula && pid == 0xFEFC)
        {
            if (TryPollAulaKeyboard(group, pid, nameHint, out var aulaDev) && seenIds.Add(aulaDev.Id))
                results.Add(aulaDev);
            return;
        }
        if (vid == VidCompxVxe && pid == 0xFA09)
        {
            if (TryPollCompxKeyboard(group, pid, nameHint, out var kbDev) && seenIds.Add(kbDev.Id))
                results.Add(kbDev);
            return;
        }

        // 16. Keychron / SKN 8K (3434 / 362D), Rapoo (24AE), VAXEE (3057), Finalmouse (361D), Endgame (3367)
        if (vid == VidKeychron || vid == VidSkn)
        {
            if (TryPollKeychron(group, vid, pid, nameHint, out var kcDev) && seenIds.Add(kcDev.Id))
                results.Add(kcDev);
            return;
        }
        if (vid == VidRapoo)
        {
            if (TryPollRapoo(group, pid, nameHint, out var rpDev) && seenIds.Add(rpDev.Id))
                results.Add(rpDev);
            return;
        }
        if (vid == VidVaxee)
        {
            if (TryPollVaxee(group, pid, nameHint, out var vxDev) && seenIds.Add(vxDev.Id))
                results.Add(vxDev);
            return;
        }
        if (vid == VidFinalmouse && pid == 0x0100)
        {
            if (TryPollFinalmouseUlx(group, pid, nameHint, out var fmDev) && seenIds.Add(fmDev.Id))
                results.Add(fmDev);
            return;
        }
        if (vid == VidEndgame && pid is 0x1961 or 0x1970)
        {
            if (TryPollEndgameGear(group, pid, nameHint, out var egDev) && seenIds.Add(egDev.Id))
                results.Add(egDev);
            return;
        }

        // 17. SinoWealth / Glorious / LAMZU / WLMouse / MCHOSE A5
        if (vid == VidSinoWealthGlorious || vid == VidLamzu || vid == VidMchoseSinowealth || vid == VidWlmouse)
        {
            if (TryPollSinoWealthOrWlmouse(group, vid, pid, nameHint, out var swDev) && seenIds.Add(swDev.Id))
                results.Add(swDev);
            return;
        }

        // 18. MCHOSE RealTek Mice (3837 / 5253) & MCHOSE Keyboards (3837:3033) & MCHOSE V9 Pro/X9 (3837:6045 / 291D:385D)
        if (vid == VidMchose || vid == VidMchoseAlt || vid == VidMchoseHeadset)
        {
            if (TryPollMchoseGroup(group, vid, pid, nameHint, out var mcDev) && seenIds.Add(mcDev.Id))
                results.Add(mcDev);
            return;
        }

        // 19. ATK NearLink (373B:108B/108C) & ATK Holtek (373B:10F0/10F1/1178)
        if (vid == VidCompxAtk && pid is 0x108B or 0x108C or 0x10F0 or 0x10F1 or 0x1178)
        {
            if (TryPollAtkSpecial(group, pid, nameHint, out var atkDev) && seenIds.Add(atkDev.Id))
                results.Add(atkDev);
            return;
        }

        // 20. Beken / Inphic / Attack Shark Passive Broadcast (1D57:FA60/FA55/FA62/FA65) & AJAZZ (249A/3151)
        if (vid == VidBeken && pid is 0xFA60 or 0xFA55 or 0xFA62 or 0xFA65)
        {
            if (TryPollBekenBroadcast(group, pid, nameHint, out var bkDev) && seenIds.Add(bkDev.Id))
                results.Add(bkDev);
            return;
        }
        if (vid == VidAjazz || vid == VidAjazzAlt)
        {
            if (TryPollAjazz(group, vid, pid, nameHint, out var ajDev) && seenIds.Add(ajDev.Id))
                results.Add(ajDev);
            return;
        }

        // 21. Shared CompX 17-byte Report 0x08 (Redragon M901P-KS 25A7:FA07, VXE 3554, Pulsar 25A7/3710, Hitscan 3770, Dareu 260D, G-Wolves 33E4, ATK 373B)
        if (vid is VidCompxVxe or VidCompxPulsarRedragon or VidCompxAtk or VidCompxPulsar2 or VidCompxHitscan or VidCompxDareu or VidCompxGwolves)
        {
            if (TryPollCompxKysonaGroup(group, vid, pid, nameHint, PeripheralType.Mouse, out var cxDev) && seenIds.Add(cxDev.Id))
                results.Add(cxDev);
        }
    }

    // =========================================================================
    //  1. GameSir (VID 0x3537, UsagePage 0xFF7A)
    // =========================================================================

    private static bool TryPollGameSir(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF7A, null);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = new byte[Math.Max((int)ch.OutputReportByteLength, 65)];
        req[0] = 0x00;
        req[1] = 0x01;
        req[2] = 0x01;

        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 75))
            return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 65)];
        int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 85);
        if (read > 0 && TryParseGameSirBattery(resp, read, out int pct))
        {
            string name = DongleBatteryMonitor.CleanDongleName(nameHint, "GameSir Wireless Controller");
            dev = new BatteryDeviceInfo($"GAMESIR_{pid:X4}", name, PeripheralType.Controller, pct, true);
            return true;
        }
        return false;
    }

    internal static bool TryParseGameSirBattery(byte[] resp, int len, out int percent)
    {
        percent = -1;
        // Handles both with leading 0x00 report ID ([1]=0x01) or without ([0]=0x01)
        if (len >= 20 && resp[0] == 0x00 && resp[1] == 0x01)
        {
            byte v = resp[19];
            if (v == 0xFF) { percent = 100; return true; }
            if (v <= 100) { percent = v; return true; }
        }
        if (len >= 20 && resp[0] == 0x01)
        {
            byte v = resp[19];
            if (v == 0xFF) { percent = 100; return true; }
            if (v <= 100) { percent = v; return true; }
        }
        return false;
    }

    // =========================================================================
    //  2. FlyDigi (VID 0x37D7, UsagePage 0xFFA0)
    // =========================================================================

    private static bool TryPollFlyDigi(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFFA0, null);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        int wLen = Math.Max((int)ch.OutputReportByteLength, 32);
        byte[] arch = Pad(wLen, 0x00, 0x5A, 0xA5, 0x07, 0x00, 0x00);
        DongleBatteryMonitor.WriteHidOverlapped(h, arch, 40);

        byte[][] shapes =
        {
            Pad(wLen, 0x00, 0x5A, 0xA5, 0x01, 0x02, 0x00),
            Pad(wLen, 0x03, 0x5A, 0xA5, 0x01, 0x02, 0x00),
        };

        foreach (var req in shapes)
        {
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 50)) continue;
            byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 32)];
            for (int i = 0; i < 6; i++)
            {
                int read = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 40);
                if (read <= 0) break;
                if (TryParseFlyDigiBattery(buf, read, out int pct))
                {
                    string fallback = pid == 0x2401 ? "FlyDigi Vader 5 Pro" : "FlyDigi Controller";
                    string name = DongleBatteryMonitor.CleanDongleName(nameHint, fallback);
                    dev = new BatteryDeviceInfo($"FLYDIGI_{pid:X4}", name, PeripheralType.Controller, pct, true);
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool TryParseFlyDigiBattery(byte[] buf, int len, out int percent)
    {
        percent = -1;
        // Accepts numbered ([0]=reportId, [1..3]=5A A5 01) or unnumbered ([0..2]=5A A5 01)
        int off = -1;
        if (len >= 12 && buf[0] == 0x5A && buf[1] == 0xA5 && buf[2] == 0x01) off = 0;
        else if (len >= 13 && buf[1] == 0x5A && buf[2] == 0xA5 && buf[3] == 0x01) off = 1;
        if (off < 0) return false;

        byte b = buf[off + 11];
        int state = (b >> 4) & 0x0F;
        int step = b & 0x0F;
        if (state > 2 || step > 5) return false;
        percent = Math.Clamp(step * 20, 0, 100);
        return true;
    }

    // =========================================================================
    //  3. BEITONG KP40D (20BC:515C, 0x00FF:0x0003)
    // =========================================================================

    private static bool TryPollBeitong(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0x00FF, 0x0003);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = Pad(Math.Max((int)ch.OutputReportByteLength, 64), 0x02, 0x15);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        for (int i = 0; i < 6; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 35);
            if (read <= 0) break;
            if (read >= 64 && resp[0] == 0x02 && resp[1] == 0x15 && resp[2] <= 100 && resp[4] is 0x50 or 0x51)
            {
                string name = DongleBatteryMonitor.CleanDongleName(nameHint, "BEITONG KP40D");
                dev = new BatteryDeviceInfo($"BEITONG_{pid:X4}", name, PeripheralType.Controller, resp[2], true);
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    //  4. Nintendo Switch Pro & Joy-Con (057E:2006 / 2007 / 2009)
    // =========================================================================

    private static bool TryPollNintendoSwitch(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0x01, 0x05) ?? FindByUsage(group, 0x01, 0x04) ?? group[0];

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        for (int i = 0; i < 3; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 25);
            if (read <= 0) break;
            if (TryParseNintendoBattery(buf, read, out int pct))
            {
                string fallback = pid switch
                {
                    0x2006 => "Nintendo Joy-Con (L)",
                    0x2007 => "Nintendo Joy-Con (R)",
                    _ => "Switch Pro Controller"
                };
                string name = fallback;
                if (!string.IsNullOrWhiteSpace(nameHint) &&
                    !nameHint.Equals("Wireless Gamepad", StringComparison.OrdinalIgnoreCase))
                {
                    name = DongleBatteryMonitor.CleanDongleName(nameHint, fallback);
                }
                dev = new BatteryDeviceInfo($"NINTENDO_{pid:X4}", name, PeripheralType.Controller, pct, true);
                SaveSticky($"NS_{ch.InstanceKey}", dev);
                return true;
            }
        }

        if (TryGetSticky($"NS_{ch.InstanceKey}", out dev))
            return true;

        return false;
    }

    internal static bool TryParseNintendoBattery(byte[] report, int len, out int percent)
    {
        percent = -1;
        if (len < 3) return false;
        if (report[0] is not (0x21 or 0x30 or 0x31)) return false;
        int tier = (report[2] & 0xE0) >> 4; // 0, 2, 4, 6, 8
        if (tier > 8) return false;
        percent = tier switch
        {
            8 => 100,
            6 => 75,
            4 => 50,
            2 => 25,
            0 => 5,
            _ => -1
        };
        return percent >= 0;
    }

    // =========================================================================
    //  5. 8BitDo D-Input Mode (2DC8) - passive listen-only
    // =========================================================================

    private static bool TryPoll8BitDo(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0x01, 0x05) ?? FindByUsage(group, 0x01, 0x04) ?? group[0];

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        for (int i = 0; i < 3; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 25);
            if (read <= 0) break;
            if (TryParse8BitDoBattery(buf, read, out int pct))
            {
                string name = DongleBatteryMonitor.CleanDongleName(nameHint, "8BitDo Controller");
                dev = new BatteryDeviceInfo($"8BITDO_{pid:X4}", name, PeripheralType.Controller, pct, true);
                SaveSticky($"8B_{ch.InstanceKey}", dev);
                return true;
            }
        }

        return TryGetSticky($"8B_{ch.InstanceKey}", out dev);
    }

    internal static bool TryParse8BitDoBattery(byte[] buf, int len, out int percent)
    {
        percent = -1;
        if (len <= 14 || buf[0] is not (0x01 or 0x04)) return false;
        int lvl = buf[14] & 0x7F;
        if (lvl < 1 || lvl > 100) return false;
        percent = lvl;
        return true;
    }

    // =========================================================================
    //  6. ASUS ROG & TUF (VID 0x0B05)
    // =========================================================================

    private static void PollAsusRogGroup(
        List<HidChannelInfo> group,
        ushort pid,
        string? nameHint,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        // A. ROG Strix Go 2.4 Headset (0x18D6 dongle / 0x18D7 cable)
        if (pid is 0x18D6 or 0x18D7)
        {
            if (TryPollStrixGo24(group, out var goDev) && seenIds.Add(goDev.Id))
                results.Add(goDev);
            return;
        }

        // B. ROG Cetra SpeedNova (0x1AD3) / ROG Delta II (0x1AFA) / Pelta / AsusHeadset
        if (pid is 0x1AD3 or 0x1AFA or 0x1A62)
        {
            if (TryPollAsusHeadset(group, pid, nameHint, out var hsDev) && seenIds.Add(hsDev.Id))
                results.Add(hsDev);
            return;
        }

        // C. ROG OMNI Receiver (0x1ACE) - can host BOTH a paired mouse AND a paired keyboard
        if (pid == 0x1ACE)
        {
            PollAsusOmniReceiver(group, results, seenIds);
            return;
        }

        // D. Harpe II Receiver (0x1AD0, mi_02&col03, report 0x03) / SpeedNova
        if (pid == 0x1AD0)
        {
            var ch = FindByUsage(group, 0xFF01, null);
            if (ch != null && TryQueryAsusMouse(ch, reportId: 0x03, scale: 1, out int pct))
            {
                string name = DongleBatteryMonitor.CleanDongleName(nameHint, "ROG Harpe II Ace");
                if (seenIds.Add($"ASUS_{pid:X4}"))
                    results.Add(new BatteryDeviceInfo($"ASUS_{pid:X4}", name, PeripheralType.Mouse, pct, true));
            }
            return;
        }

        // E. Standalone ROG / TUF Wireless Mice (reportId 0x00, 12 07)
        var mouseCh = FindByUsage(group, 0xFF00, null) ?? FindByUsage(group, 0xFF01, null);
        if (mouseCh != null)
        {
            int scale = pid is 0x18E5 or 0x18E3 or 0x1960 or 0x195E or 0x1A59 or 0x1A57 or 0x1908 or 0x1906 or 0x1949 or 0x1947 ? 25 : 1;
            if (TryQueryAsusMouse(mouseCh, reportId: 0x00, scale, out int pct))
            {
                string name = ResolveAsusMouseName(pid, nameHint);
                string id = $"ASUS_{pid:X4}";
                if (seenIds.Add(id))
                    results.Add(new BatteryDeviceInfo(id, name, PeripheralType.Mouse, pct, true));
            }
        }
    }

    private static void PollAsusOmniReceiver(
        List<HidChannelInfo> group,
        List<BatteryDeviceInfo> results,
        HashSet<string> seenIds)
    {
        var ctrlCh = FindByUsage(group, 0xFF02, null);
        var pairedPids = new List<ushort>();

        if (ctrlCh != null)
        {
            using var hCtrl = DongleBatteryMonitor.OpenHidHandle(ctrlCh.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (hCtrl != null && !hCtrl.IsInvalid)
            {
                DongleBatteryMonitor.DrainPendingReads(hCtrl, ctrlCh.InputReportByteLength);
                byte[] pairReq = Pad(Math.Max((int)ctrlCh.OutputReportByteLength, 64), 0x01, 0xA0, 0x00, 0x00);
                if (DongleBatteryMonitor.WriteHidOverlapped(hCtrl, pairReq, 70))
                {
                    byte[] resp = new byte[Math.Max((int)ctrlCh.InputReportByteLength, 64)];
                    for (int i = 0; i < 4; i++)
                    {
                        int read = DongleBatteryMonitor.ReadHidOverlapped(hCtrl, resp, 45);
                        if (read >= 7 && resp[0] == 0x01 && resp[1] == 0xA0 && resp[2] == 0x00 && resp[3] == 0x00)
                        {
                            for (int slot = 5; slot + 1 < read; slot += 4)
                            {
                                ushort p = (ushort)(resp[slot] | (resp[slot + 1] << 8));
                                if (p == 0) break;
                                pairedPids.Add(p);
                            }
                            break;
                        }
                    }
                }
            }
        }

        // Mouse on col03 / 0xFF01 with report 03 12 07
        var mouseCh = FindByUsage(group, 0xFF01, null);
        if (mouseCh != null && TryQueryAsusMouse(mouseCh, reportId: 0x03, scale: 1, out int mPct))
        {
            string mName = "ROG OMNI Wireless Mouse";
            foreach (ushort p in pairedPids)
            {
                string cand = ResolveAsusMouseName(p, null);
                if (!cand.StartsWith("ASUS ROG", StringComparison.OrdinalIgnoreCase) || cand != "ASUS ROG Wireless Mouse")
                {
                    mName = cand;
                    break;
                }
            }
            if (seenIds.Add("ASUS_OMNI_MOUSE"))
                results.Add(new BatteryDeviceInfo("ASUS_OMNI_MOUSE", mName, PeripheralType.Mouse, mPct, true));
        }

        // Keyboard on col02 / 0xFF00 with report 02 12 01
        var kbCh = FindByUsage(group, 0xFF00, null);
        if (kbCh != null)
        {
            int batOffset = pairedPids.Contains(0x1B06) ? 11 : 6; // Falchion RX uses byte 11, Azoth/Scope II use byte 6
            if (TryQueryAsusOmniKeyboard(kbCh, batOffset, out int kPct))
            {
                string kName = ResolveAsusKeyboardName(pairedPids);
                if (seenIds.Add("ASUS_OMNI_KB"))
                    results.Add(new BatteryDeviceInfo("ASUS_OMNI_KB", kName, PeripheralType.Keyboard, kPct, true));
            }
        }
    }

    private static bool TryQueryAsusMouse(HidChannelInfo ch, byte reportId, int scale, out int percent)
    {
        percent = -1;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = Pad(Math.Max((int)ch.OutputReportByteLength, 64), reportId, 0x12, 0x07);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 70)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 65)];
        for (int i = 0; i < 4; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 45);
            if (read <= 0) break;
            if (TryParseAsusMouseBattery(resp, read, reportId, scale, out percent))
                return true;
        }
        return false;
    }

    internal static bool TryParseAsusMouseBattery(byte[] resp, int len, byte expectedReportId, int scale, out int percent)
    {
        percent = -1;
        int off = -1;
        if (expectedReportId != 0 && len >= 11 && resp[0] == expectedReportId && resp[1] == 0x12 && resp[2] == 0x07)
            off = 1;
        else if (expectedReportId == 0 && len >= 11 && resp[0] == 0x00 && resp[1] == 0x12 && resp[2] == 0x07)
            off = 1;
        else if (expectedReportId == 0 && len >= 10 && resp[0] == 0x12 && resp[1] == 0x07)
            off = 0;

        if (off < 0) return false;

        int raw = resp[off + 4];
        bool charging = resp[off + 9] != 0;
        if (raw == 0 && !charging) return false; // mouse in standby/offline

        if (scale > 1)
        {
            if (raw > 4) return false;
            percent = Math.Clamp(raw * scale, 0, 100);
            return true;
        }

        if (raw > 100) return false;
        percent = raw;
        return true;
    }

    private static bool TryQueryAsusOmniKeyboard(HidChannelInfo ch, int batteryByteOffset, out int percent)
    {
        percent = -1;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = Pad(Math.Max((int)ch.OutputReportByteLength, 64), 0x02, 0x12, 0x01);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 70)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        for (int i = 0; i < 4; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 45);
            if (read <= 0) break;
            if (TryParseAsusOmniKeyboard(resp, read, batteryByteOffset, out percent))
                return true;
        }
        return false;
    }

    internal static bool TryParseAsusOmniKeyboard(byte[] resp, int len, int batteryByteOffset, out int percent)
    {
        percent = -1;
        if (len <= Math.Max(batteryByteOffset, 9) || resp[0] != 0x02 || resp[1] != 0x12 || resp[2] != 0x01)
            return false;

        int lvl = resp[batteryByteOffset];
        bool charging = resp[9] != 0;
        if (lvl > 100 || (lvl == 0 && !charging)) return false;
        percent = lvl;
        return true;
    }

    private static bool TryPollStrixGo24(List<HidChannelInfo> group, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF00, null) ?? group[0];
        if (ch.FeatureReportByteLength < 64) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] req = Pad(Math.Max((int)ch.FeatureReportByteLength, 64), 0xFF, 0x08, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x03, 0x52, 0x01);
        if (!DongleBatteryMonitor.HidD_SetFeature(h, req, (uint)req.Length)) return false;

        Thread.Sleep(35);
        byte[] resp = new byte[req.Length];
        resp[0] = 0xFF;
        if (!DongleBatteryMonitor.HidD_GetFeature(h, resp, (uint)resp.Length)) return false;

        if (TryParseStrixGo24Battery(resp, resp.Length, out int pct))
        {
            dev = new BatteryDeviceInfo("ASUS_STRIX_GO24", "ROG Strix Go 2.4", PeripheralType.Headphones, pct, true);
            return true;
        }
        return false;
    }

    internal static bool TryParseStrixGo24Battery(byte[] resp, int len, out int percent)
    {
        percent = -1;
        if (len < 13) return false;
        int skip = resp[0] == 0xFF ? 0 : 1;
        int At(int n) { int i = n - skip; return i >= 0 && i < len ? resp[i] : -1; }

        if (At(1) == 0xFF && At(2) == 0xAA) return false;
        if (At(1) != 0x1B) return false;

        int lsb = At(11), msb = At(12);
        if (lsb < 0 || msb < 0) return false;
        int mv = (msb << 8) | lsb;
        bool charging = At(9) == 0x0A;
        if (charging && mv < 4100)
        {
            mv = Math.Max(0, mv - 160);
            msb = mv >> 8;
        }
        if (msb >= 0x10 || mv >= 4100) percent = 100;
        else if (msb >= 0x0F || mv >= 3800) percent = 75;
        else if (mv >= 3700) percent = 50;
        else if (msb >= 0x0E || mv >= 3500) percent = 25;
        else percent = 10;
        return true;
    }

    private static bool TryPollAsusHeadset(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF00, null) ?? group[0];
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = Pad(Math.Max((int)ch.OutputReportByteLength, 64), 0x00, 0x12, 0x07);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 75)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        for (int i = 0; i < 4; i++)
        {
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 60);
            if (read <= 0) break;
            int off = (resp[0] == 0x00 && read >= 9 && resp[1] == 0x12 && resp[2] == 0x07) ? 0
                    : (read >= 8 && resp[0] == 0x12 && resp[1] == 0x07) ? -1 : -99;
            if (off < -1) continue;
            if (resp[off + 1] == 0xFF && resp[off + 2] == 0xAA) return false;

            int pct;
            if (pid == 0x1AD3 && off + 7 < read) // ROG Cetra SpeedNova: min(L, R)
            {
                int l = resp[off + 6], r = resp[off + 7];
                if (l > 100 && r > 100) return false;
                pct = (l <= 100 && r <= 100) ? Math.Min(l, r) : Math.Min(l, r);
                if (pct > 100) pct = Math.Min(l, 100);
            }
            else if (off + 6 < read && resp[off + 6] <= 100)
            {
                pct = resp[off + 6];
            }
            else continue;

            string fallback = pid == 0x1AD3 ? "ROG Cetra SpeedNova" : pid == 0x1AFA ? "ROG Delta II" : "ASUS ROG Headset";
            string name = DongleBatteryMonitor.CleanDongleName(nameHint, fallback);
            dev = new BatteryDeviceInfo($"ASUS_HS_{pid:X4}", name, PeripheralType.Headphones, pct, true);
            return true;
        }
        return false;
    }

    private static string ResolveAsusMouseName(ushort pid, string? fallbackName) => pid switch
    {
        0x1B65 => "ROG Harpe Ace Mini",
        0x1C0E or 0x1D4E => "ROG Keris II Origin",
        0x1A94 or 0x1A92 => "ROG Harpe Ace Aim Lab Edition",
        0x1AD7 => "ROG Strix Impact III Wireless",
        0x1A72 or 0x1A70 or 0x1B0C or 0x1B0A => "ROG Gladius III Aimpoint",
        0x197F or 0x197D => "ROG Gladius III Wireless",
        0x1A68 or 0x1A66 or 0x1A6A => "ROG Keris Wireless Aimpoint",
        0x1B1A or 0x1B18 => "ROG Keris II Ace",
        0x1B68 or 0x1B69 => "ROG Harpe Ace Extreme",
        0x1A1A or 0x1A18 => "ROG Chakram X",
        0x18E5 or 0x18E3 => "ROG Chakram",
        0x1960 or 0x195E => "ROG Keris Wireless",
        0x1979 or 0x1977 => "ROG Spatha X",
        0x19F4 => "TUF Gaming M4 Wireless",
        0x1908 or 0x1906 => "ROG Pugio II",
        0x1949 or 0x1947 => "ROG Strix Impact II Wireless",
        _ => DongleBatteryMonitor.CleanDongleName(fallbackName, "ASUS ROG Wireless Mouse")
    };

    private static string ResolveAsusKeyboardName(List<ushort> pids)
    {
        foreach (ushort p in pids)
        {
            string? n = p switch
            {
                0x1A85 => "ROG Azoth",
                0x1B42 => "ROG Azoth Extreme",
                0x1CF1 => "ROG Azoth Extreme SE",
                0x1AB0 => "ROG Strix Scope II 96 Wireless",
                0x1B7A => "ROG Strix Scope II 96 RX Wireless",
                0x1B06 => "ROG Falchion RX Low Profile",
                0x1CEA => "ROG Strix Morph 96 Wireless",
                _ => null
            };
            if (n != null) return n;
        }
        return "ROG OMNI Wireless Keyboard";
    }

    // =========================================================================
    //  7. Turtle Beach (10F5: Stealth Pro II, Stealth 700 Gen 3, Atlas Air)
    // =========================================================================

    private static bool TryPollTurtleBeach(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        if (pid is not (0x229B or 0x229E or 0x2250 or 0x2251 or 0x2253 or 0x225E))
            return false;

        var ch = FindByUsage(group, 0xFF13, 0x0001) ?? FindByUsage(group, 0xFF13, null);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);

        bool isCoap = pid is 0x2250 or 0x2251 or 0x2253;
        var frames = new List<byte[]>();

        if (!isCoap)
        {
            // SInf opener + GSI ask
            byte[] askSinf = Pad(62, 0x06, 0x18, 0x00, 0x05, 0x5A, 0x14, 0x00, 0x02, 0x99, 0x48, 0x01, 0xC0, 0x4A, 0x01,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x61, 0x00, 0x53, 0x49, 0x6E, 0x66);
            DongleBatteryMonitor.WriteHidOverlapped(h, askSinf, 60);
            ReadFramedBurst(h, ch.InputReportByteLength, frames, 6, 30);

            byte[] askGsi = Pad(62, 0x06, 0x18, 0x00, 0x05, 0x5A, 0x14, 0x00, 0x02, 0x99, 0x48, 0x01, 0x44, 0x11, 0x01,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x61, 0x00, 0x53, 0x47, 0x53, 0x49);
            if (DongleBatteryMonitor.WriteHidOverlapped(h, askGsi, 60))
                ReadFramedBurst(h, ch.InputReportByteLength, frames, 8, 35);
        }

        if (isCoap || (pid == 0x225E && frames.Count == 0))
        {
            // RACE 0x9942 enable CoAP client: 06 <len> 00 05 5A 02 00 42 99
            byte[] coapEnable = Pad(62, 0x06, 0x06, 0x00, 0x05, 0x5A, 0x02, 0x00, 0x42, 0x99);
            if (DongleBatteryMonitor.WriteHidOverlapped(h, coapEnable, 60))
            {
                Thread.Sleep(50);
                // CoAP GET GSI: 05 5A 0A 00 02 99 40 01 00 01 B3 47 53 49
                byte[] coapGsi = Pad(62, 0x06, 0x0E, 0x00, 0x05, 0x5A, 0x0A, 0x00, 0x02, 0x99,
                    0x40, 0x01, 0x00, 0x01, 0xB3, 0x47, 0x53, 0x49);
                if (DongleBatteryMonitor.WriteHidOverlapped(h, coapGsi, 60))
                    ReadFramedBurst(h, ch.InputReportByteLength, frames, 12, 30);
            }
        }

        if (TryParseTurtleBeachGsiFrames(frames, out int pct, out string? gsiName))
        {
            string fallback = pid switch
            {
                0x229B or 0x229E => "Turtle Beach Stealth Pro II",
                0x225E => "Turtle Beach Atlas Air",
                _ => "Turtle Beach Stealth 700 Gen 3"
            };
            string name = (pid is 0x229B or 0x229E) && !string.IsNullOrWhiteSpace(gsiName) &&
                          !gsiName.Equals("My Headset", StringComparison.OrdinalIgnoreCase)
                ? gsiName
                : DongleBatteryMonitor.CleanDongleName(nameHint, fallback);

            dev = new BatteryDeviceInfo($"TURTLEBEACH_{pid:X4}", name, PeripheralType.Headphones, pct, true);
            SaveSticky($"TB_{pid:X4}", dev);
            return true;
        }

        return TryGetSticky($"TB_{pid:X4}", out dev);
    }

    private static void ReadFramedBurst(SafeFileHandle h, ushort inLen, List<byte[]> outFrames, int maxReads, int timeoutMs)
    {
        int len = Math.Max((int)inLen, 62);
        for (int i = 0; i < maxReads; i++)
        {
            byte[] buf = new byte[len];
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, buf, timeoutMs);
            if (n < 3) break;
            if (n < buf.Length) Array.Resize(ref buf, n);
            outFrames.Add(buf);
        }
    }

    internal static bool TryParseTurtleBeachGsiFrames(List<byte[]> frames, out int percent, out string? deviceName)
    {
        percent = -1;
        deviceName = null;
        if (frames.Count == 0) return false;

        var blob = new List<byte>(512);
        foreach (var f in frames)
        {
            if (f.Length < 3 || f[0] != 0x07) continue;
            int n = Math.Min(f[1], f.Length - 3);
            for (int i = 0; i < n; i++) blob.Add(f[3 + i]);
        }

        if (blob.Count == 0) return false;
        byte[] data = blob.ToArray();

        int idx = 0;
        while (idx < data.Length - 2)
        {
            if (data[idx] != '{' || data[idx + 1] != '"') { idx++; continue; }

            int depth = 0;
            bool inStr = false, esc = false;
            int end = -1;
            for (int j = idx; j < data.Length; j++)
            {
                byte c = data[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { if (--depth == 0) { end = j + 1; break; } }
                else if (c < 0x20) break;
            }

            if (end <= idx) { idx++; continue; }

            try
            {
                using var doc = JsonDocument.Parse(Encoding.ASCII.GetString(data, idx, end - idx));
                if (doc.RootElement.TryGetProperty("KVP", out var kvp) && kvp.ValueKind == JsonValueKind.Object)
                {
                    if (kvp.TryGetProperty("240", out var p240) &&
                        int.TryParse(p240.ValueKind == JsonValueKind.String ? p240.GetString() : p240.ToString(), out int parsed) &&
                        parsed is >= 0 and <= 100)
                    {
                        percent = parsed;
                    }
                    if (kvp.TryGetProperty("220", out var p220) && p220.ValueKind == JsonValueKind.String)
                    {
                        deviceName = p220.GetString();
                    }
                }
            }
            catch { }

            idx = end;
        }

        return percent >= 0;
    }

    // =========================================================================
    //  8. SteelSeries Extra Mice (Rival 3, Aerox, Prime Wireless)
    // =========================================================================

    private static bool IsSteelSeriesMousePid(ushort pid) => pid is 0x1830 or 0x1872
        or 0x1838 or 0x1878 or 0x1852 or 0x185C or 0x1860 or 0x1858 or 0x1874 or 0x1840 or 0x1848
        or 0x183A or 0x187A or 0x1854 or 0x185E or 0x1862 or 0x185A or 0x1876 or 0x1842 or 0x184A;

    private static bool TryPollSteelSeriesMouse(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindVendorChannel(group);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        bool isRival3 = pid is 0x1830 or 0x1872;
        bool isWiredCable = pid is 0x183A or 0x187A or 0x1854 or 0x185E or 0x1862 or 0x185A or 0x1876 or 0x1842 or 0x184A;
        byte[] req = isRival3 ? Pad(Math.Max((int)ch.OutputReportByteLength, 3), 0x00, 0xAA, 0x01)
                              : Pad(Math.Max((int)ch.OutputReportByteLength, 2), 0x00, (byte)(isWiredCable ? 0x92 : 0xD2));

        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 32)];
        int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 60);
        if (read < 3) return false;

        int pct;
        if (isRival3)
        {
            int raw = resp[0] == 0x00 && read >= 4 ? resp[1] : resp[0];
            if (raw > 100) return false;
            pct = raw;
        }
        else
        {
            int rawByte = resp[0] == 0x00 && read >= 3 ? resp[2] : resp[1];
            int lvl = ((rawByte & 0x7F) - 1) * 5;
            if (lvl < 0 || lvl > 100) return false;
            pct = lvl;
        }

        string name = DongleBatteryMonitor.CleanDongleName(nameHint, isRival3 ? "SteelSeries Rival 3 Wireless" : "SteelSeries Wireless Mouse");
        dev = new BatteryDeviceInfo($"STEELSERIES_M_{pid:X4}", name, PeripheralType.Mouse, pct, true);
        return true;
    }

    // =========================================================================
    //  9. Razer Extended Headsets (BlackShark V2 Pro 2023 / V3 / Barracuda Pro / X)
    // =========================================================================

    private static bool IsRazerHeadsetExtendedPid(ushort pid) =>
        pid is 0x0555 or 0x0556 or 0x0583 or 0x0536 or 0x053A or 0x0576 or 0x0577 or 0x0579 or 0x057A or 0x0552;

    private static bool TryPollRazerExtendedHeadset(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;

        // BlackShark V3 (0576/0577/0579/057A): UsagePage 0xFF14, 64-byte XOR query
        if (pid is 0x0576 or 0x0577 or 0x0579 or 0x057A)
        {
            var ch = FindByUsage(group, 0xFF14, 0x0001) ?? FindByUsage(group, 0xFF14, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            byte[] req = Pad(64, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, 0x21);
            byte xor = 0; for (int i = 0; i < 62; i++) xor ^= req[i];
            req[62] = xor;
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 70)) return false;

            byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
            for (int t = 0; t < 4; t++)
            {
                int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 50);
                if (read >= 14 && resp[0] == 0x02 && resp[1] == 0x02 && resp[2] == 0x60 &&
                    resp[9] == 0x80 && resp[10] == 0x21 && resp[11] is 0x01 or 0x02 && resp[12] == 0x01 &&
                    resp[13] is >= 1 and <= 100)
                {
                    string name = DongleBatteryMonitor.CleanDongleName(nameHint, "Razer BlackShark V3");
                    dev = new BatteryDeviceInfo($"RAZER_HS_{pid:X4}", name, PeripheralType.Headphones, resp[13], true);
                    return true;
                }
            }
            return false;
        }

        // BlackShark V2 Pro (2023) & Barracuda Pro ('PA'/'PI' 64-byte)
        if (pid is 0x0555 or 0x0556 or 0x0583 or 0x0536 or 0x053A)
        {
            var ch = FindByUsage(group, 0xFF00, null) ?? FindByUsage(group, 0xFF14, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            bool isBarracuda = pid == 0x053A;
            byte[] remoteOn = isBarracuda
                ? Pad(64, 0x01, 0x80, 0x07, 0x50, 0x41, 0x0E, 0x08, 0x02, 0xE1, 0x01)
                : Pad(64, 0x02, 0x80, 0x07, 0x00, 0x00, 0x50, 0x41, 0x0E, 0x00, 0x02, 0xE1, 0x01);
            byte[] batQuery = isBarracuda
                ? Pad(64, 0x01, 0x80, 0x08, 0x50, 0x41, 0x08, 0x08, 0x03, 0x21)
                : Pad(64, 0x02, 0x80, 0x08, 0x00, 0x00, 0x50, 0x41, 0x08, 0x00, 0x03, 0x21);

            DongleBatteryMonitor.WriteHidOverlapped(h, remoteOn, 50);
            Thread.Sleep(20);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, batQuery, 60)) return false;

            byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
            int cmdOff = isBarracuda ? 13 : 12;
            byte expectId = (byte)(isBarracuda ? 0x01 : 0x02);
            for (int t = 0; t < 4; t++)
            {
                int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 45);
                if (read <= 0) break;
                if (resp[0] == expectId && read > cmdOff + 3 && resp[cmdOff] == 0x21 && resp[cmdOff + 1] is 0x01 or 0x02)
                {
                    int pct = resp[cmdOff + 3];
                    if (pct <= 100)
                    {
                        string name = DongleBatteryMonitor.CleanDongleName(nameHint, isBarracuda ? "Razer Barracuda Pro" : "Razer BlackShark V2 Pro");
                        dev = new BatteryDeviceInfo($"RAZER_HS_{pid:X4}", name, PeripheralType.Headphones, pct, true);
                        return true;
                    }
                }
            }
            return false;
        }

        return false;
    }

    // =========================================================================
    //  10. Logitech G Headsets (HID++ Voltage + Centurion PRO X 2/3) & Astro A50
    // =========================================================================

    private static bool TryPollLogitechOrAstroHeadset(
        List<HidChannelInfo> group,
        ushort vid,
        ushort pid,
        string? nameHint,
        out BatteryDeviceInfo dev)
    {
        dev = default;

        // Astro A50 Gen 4 (9886:002C)
        if (vid == VidAstro && pid == 0x002C)
        {
            var ch = FindByUsage(group, 0xFF32, 0x74) ?? FindVendorChannel(group);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(64, 0x02, 0x54, 0x00), 50)) return false;
            byte[] rLink = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, rLink, 50) < 4 || rLink[0] != 0x02 || rLink[1] != 0x02 || (rLink[3] & 0x02) == 0)
                return false;

            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(64, 0x02, 0x7C, 0x00), 50)) return false;
            byte[] rBat = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, rBat, 50) >= 4 && rBat[0] == 0x02 && rBat[1] == 0x02)
            {
                int pct = Math.Clamp(rBat[3] & 0x7F, 0, 100);
                dev = new BatteryDeviceInfo("ASTRO_A50_G4", DongleBatteryMonitor.CleanDongleName(nameHint, "Astro A50 Gen 4"), PeripheralType.Headphones, pct, true);
                return true;
            }
            return false;
        }

        // Astro A50 Gen 5 (046D:0B1C)
        if (vid == VidLogitech && pid == 0x0B1C)
        {
            var ch = FindByUsage(group, 0xFF32, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(64, 0x02, 0x0C, 0x03, 0x00, 0x06, 0x0C), 50)) return false;
            byte[] r = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 60) >= 9 && r[0] == 0x02 && r[1] == 0x0C && r[4] == 0x06 && r[6] <= 100)
            {
                dev = new BatteryDeviceInfo("LOGI_A50_G5", DongleBatteryMonitor.CleanDongleName(nameHint, "Astro A50 Gen 5"), PeripheralType.Headphones, r[6], true);
                return true;
            }
            return false;
        }

        // Logitech G PRO X 2 / PRO X 3 Lightspeed Centurion (046D:0AF7 / 0B45, 0xFFA0:0x0001)
        if (vid == VidLogitech && pid is 0x0AF7 or 0x0B45)
        {
            var ch = FindByUsage(group, 0xFFA0, 0x0001) ?? FindByUsage(group, 0xFFA0, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            byte[] legacyReq = Pad(64, 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, legacyReq, 60)) return false;

            byte[] resp = new byte[64];
            for (int t = 0; t < 4; t++)
            {
                int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 50);
                if (read < 7 || resp[0] != 0x51) continue;
                if (resp[1] == 0x05 && resp[6] == 0x00) return false; // headset off
                if (resp[1] == 0x0B && read >= 11 && resp[8] == 0x04 && resp[10] <= 100)
                {
                    dev = new BatteryDeviceInfo($"LOGI_CENTURION_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Logitech G PRO X 2"), PeripheralType.Headphones, resp[10], true);
                    return true;
                }
            }
            return false;
        }

        // Logitech G633/G733/G933, G533, G535, G PRO Headsets (HID++ 0xFF43:0x0202)
        byte[]? cmd = null;
        int[]? mvCurve = null;
        int[]? pctCurve = null;

        if (pid is 0x0A5C or 0x0A89 or 0x0A5B or 0x0A87 or 0x0AB5 or 0x0AFE or 0x0B1F)
        {
            cmd = new byte[] { 0x08, 0x0A };
            mvCurve = new[] { 4100, 3950, 3850, 3750, 3650, 3500, 3450, 3300 };
            pctCurve = new[] { 100, 80, 60, 40, 20, 10, 5, 0 };
        }
        else if (pid == 0x0A66) // G533
        {
            cmd = new byte[] { 0x07, 0x01 };
            mvCurve = new[] { 4200, 3850, 3790, 3750, 3680, 3330 };
            pctCurve = new[] { 100, 50, 30, 20, 5, 0 };
        }
        else if (pid == 0x0AC4) // G535
        {
            cmd = new byte[] { 0x05, 0x0D };
            mvCurve = new[] { 4175, 3817, 3766, 3730, 3664, 3310 };
            pctCurve = new[] { 100, 50, 30, 20, 5, 0 };
        }
        else if (pid is 0x0AA7 or 0x0AAA or 0x0ABA or 0x0AFB or 0x0AFC) // G PRO
        {
            cmd = new byte[] { 0x06, 0x0D };
            mvCurve = new[] { 4150, 3830, 3780, 3740, 3670, 3320 };
            pctCurve = new[] { 100, 50, 30, 20, 5, 0 };
        }

        if (cmd != null && mvCurve != null && pctCurve != null)
        {
            var ch = FindByUsage(group, 0xFF43, 0x0202) ?? FindByUsage(group, 0xFF43, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(20, 0x11, 0xFF, cmd[0], cmd[1]), 60)) return false;

            byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
            int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 65);
            if (read >= 7 && resp[0] == 0x11 && resp[2] == cmd[0] && resp[3] == cmd[1])
            {
                int mv = (resp[4] << 8) | resp[5];
                if (mv >= mvCurve[^1])
                {
                    int pct = InterpolateDescending(mv, mvCurve, pctCurve);
                    dev = new BatteryDeviceInfo($"LOGI_HS_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Logitech Wireless Headset"), PeripheralType.Headphones, pct, true);
                    return true;
                }
            }
        }

        return false;
    }

    // =========================================================================
    //  11. Corsair Slipstream / Virtuoso SE / XT (0x1B1C)
    // =========================================================================

    private static bool TryPollCorsairBragi(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF42, null) ?? FindVendorChannel(group);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        bool unnumbered = ch.OutputReportByteLength >= 65 || pid == 0x1BDC;
        int wLen = unnumbered ? 65 : 64;

        if (pid == 0x1BDC)
        {
            DongleBatteryMonitor.WriteHidOverlapped(h, Pad(wLen, 0x00, 0x02, 0x08, 0x02, 0x12, 0x00), 40);
            DongleBatteryMonitor.ReadHidOverlapped(h, new byte[64], 30);
        }

        byte[] req = unnumbered
            ? Pad(wLen, 0x00, 0x02, 0x09, 0x02, 0x0F, 0x00)
            : Pad(wLen, 0x02, 0x09, 0x02, 0x0F, 0x00);

        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;

        byte[] resp = new byte[Math.Max((int)ch.InputReportByteLength, 64)];
        int read = DongleBatteryMonitor.ReadHidOverlapped(h, resp, 65);
        if (read >= 6 && resp[0] == 0x01 && resp[1] == 0x01 && resp[2] == 0x02 && resp[3] == 0x00)
        {
            int raw = resp[4] | (resp[5] << 8);
            if (raw is >= 1 and <= 1000)
            {
                int pct = Math.Clamp((int)Math.Round(raw / 10.0), 0, 100);
                var kind = pid == 0x1BDC ? PeripheralType.Mouse : PeripheralType.Headphones;
                string name = DongleBatteryMonitor.CleanDongleName(nameHint, pid == 0x1BDC ? "Corsair Slipstream" : "Corsair Virtuoso");
                dev = new BatteryDeviceInfo($"CORSAIR_B_{pid:X4}", name, kind, pct, true);
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    //  12. HyperX Extra Headsets (Cloud Flight / S / Cloud III / Core / III S / Stinger 2)
    // =========================================================================

    private static bool IsHyperXExtendedPid(ushort vid, ushort pid) =>
        (vid == VidHyperXKingston && pid is 0x16C4 or 0x1723 or 0x16EA or 0x16EB) ||
        (vid == VidHyperXHp && pid is 0x05B7 or 0x0C9D or 0x0995 or 0x06BE or 0x0D93);

    private static bool TryPollHyperXExtended(List<HidChannelInfo> group, ushort vid, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        if (vid == VidHyperXHp && pid is 0x05B7 or 0x0C9D or 0x0995) // Cloud III Wireless / Cloud II Core (66 89)
        {
            var ch = FindByUsage(group, 0xFF13, 0x01) ?? FindVendorChannel(group);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(Math.Max((int)ch.OutputReportByteLength, 52), 0x66, 0x89), 60)) return false;
            byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 65) >= 5 && r[0] == 0x66 && r[1] == 0x89 && r[4] <= 100)
            {
                dev = new BatteryDeviceInfo($"HYPERX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "HyperX Cloud III Wireless"), PeripheralType.Headphones, r[4], true);
                return true;
            }
        }
        else if (vid == VidHyperXHp && pid == 0x06BE) // Cloud III S Wireless (0x01C0:0x0001)
        {
            var ch = FindByUsage(group, 0x01C0, 0x01) ?? group[0];
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(Math.Max((int)ch.OutputReportByteLength, 52), 0x0C, 0x02, 0x03, 0x01, 0x00, 0x06), 60)) return false;
            byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 65) >= 7 && r[0] == 0x0C && r[5] == 0x06 && r[6] <= 100)
            {
                dev = new BatteryDeviceInfo($"HYPERX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "HyperX Cloud III S Wireless"), PeripheralType.Headphones, r[6], true);
                return true;
            }
        }
        else if (vid == VidHyperXHp && pid == 0x0D93) // Cloud Stinger 2 Wireless
        {
            var ch = FindByUsage(group, 0xFF90, 0x0303) ?? FindVendorChannel(group);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(20, 0x06, 0xFF, 0xBB, 0x02, 0x00), 60)) return false;
            byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 65) >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x02 && r[7] <= 100)
            {
                dev = new BatteryDeviceInfo($"HYPERX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "HyperX Cloud Stinger 2 Wireless"), PeripheralType.Headphones, r[7], true);
                return true;
            }
        }
        else if (vid == VidHyperXKingston && pid is 0x16EA or 0x16EB) // Cloud Flight S
        {
            var ch = FindByUsage(group, 0xFF13, 0x01) ?? FindVendorChannel(group);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            byte[] req = Pad(62, 0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0x02);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;
            byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 65) >= 8 && r[0] != 0x0A && r[2] == 0xBB && r[3] == 0x02 && r[7] is >= 1 and <= 100)
            {
                dev = new BatteryDeviceInfo($"HYPERX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "HyperX Cloud Flight S"), PeripheralType.Headphones, r[7], true);
                return true;
            }
        }
        else if (vid == VidHyperXKingston && pid is 0x16C4 or 0x1723) // Cloud Flight
        {
            var ch = FindVendorChannel(group) ?? group[0];
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(20, 0x21, 0xFF, 0x05), 60)) return false;
            byte[] r = new byte[20];
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 65);
            if (n is 15 or 20)
            {
                int mv = (r[3] << 8) | r[4];
                int pct = InterpolateDescending(mv, new[] { 3975, 3900, 3800, 3700, 3648, 0 }, new[] { 100, 80, 50, 25, 8, 0 });
                dev = new BatteryDeviceInfo($"HYPERX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "HyperX Cloud Flight"), PeripheralType.Headphones, pct, true);
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    //  13. Sony INZONE Buds (054C:0EC2)
    // =========================================================================

    private static bool TryPollSonyInzone(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF04, 0x0001) ?? FindByUsage(group, 0xFF04, null);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        const ushort tid = 0x4242;
        byte[] req = Pad(64, 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, (byte)(tid & 0xFF), (byte)(tid >> 8));
        int chk = 0; for (int i = 6; i <= 12; i++) chk += req[i];
        req[13] = (byte)(chk & 0xFF);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 70)) return false;

        byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 20)];
        for (int t = 0; t < 4; t++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 60);
            if (n >= 20 && r[0] == 0x02 && r[9] == 0x04 && r[10] == 0x10 && r[11] == (tid & 0xFF) && r[12] == (tid >> 8))
            {
                int sum = 0; for (int i = 5; i < 19; i++) sum += r[i];
                if ((sum & 0xFF) != r[19]) continue;
                int left = r[14] <= 100 ? r[14] : 999;
                int right = r[16] <= 100 ? r[16] : 999;
                int pct = Math.Min(left, right);
                if (pct <= 100)
                {
                    dev = new BatteryDeviceInfo("SONY_INZONE", DongleBatteryMonitor.CleanDongleName(nameHint, "Sony INZONE Buds"), PeripheralType.Headphones, pct, true);
                    return true;
                }
            }
        }
        return false;
    }

    // =========================================================================
    //  14. EPOS H3PRO Hybrid / Audeze Maxwell / JBL Quantum / Skullcandy PLYR
    // =========================================================================

    private static byte EposCrc8(byte[] data, int offset, int len)
    {
        byte crc = 0xFF;
        for (int k = 0; k < len; k++)
        {
            crc ^= data[offset + k];
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    private static bool TryPollEposH3Pro(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFFFF, 0x0001);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        ushort target = pid == 0x0101 ? (ushort)0x0001 : (ushort)0x0005;

        byte[] payload = { 0x12, 0x00, 0x03, 0x00 }; // group 0x12, cmd 0x03
        byte[] req = new byte[107];
        req[0] = 0x01; req[1] = 0xEB; req[2] = 0xFE;
        req[3] = 0x01; req[4] = 0x00;
        req[5] = 0x04; req[6] = 0x00;
        req[7] = (byte)(target & 0xFF); req[8] = (byte)(target >> 8);
        req[9] = 0x04; req[10] = 0x00;
        req[11] = EposCrc8(payload, 0, payload.Length); req[12] = 0x00;
        Buffer.BlockCopy(payload, 0, req, 13, 4);

        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 70)) return false;

        byte[] r = new byte[107];
        for (int t = 0; t < 4; t++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 60);
            if (n < 19 || r[0] != 0x01 || r[1] != 0xEB || r[2] != 0xFE) continue;
            int plen = r[9] | (r[10] << 8);
            if (plen < 6 || 13 + plen > n || (r[11] | (r[12] << 8)) != EposCrc8(r, 13, plen)) continue;
            ushort rTarget = (ushort)(r[7] | (r[8] << 8));
            ushort rGrp = (ushort)(r[13] | (r[14] << 8));
            ushort rCmd = (ushort)(r[15] | (r[16] << 8));
            ushort rVal = (ushort)(r[17] | (r[18] << 8));
            if (rGrp == 0x12 && rCmd == 0x03 && rTarget == target - 1 && rVal <= 100)
            {
                dev = new BatteryDeviceInfo("EPOS_H3PRO", DongleBatteryMonitor.CleanDongleName(nameHint, "EPOS H3PRO Hybrid"), PeripheralType.Headphones, rVal, true);
                return true;
            }
        }
        return false;
    }

    private static bool TryPollAudezeMaxwell(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF13, 0x01) ?? FindVendorChannel(group);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] req = Pad(62, 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;

        byte[] r = new byte[62];
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(25);
            Array.Clear(r, 0, r.Length);
            r[0] = 0x07;
            if (!DongleBatteryMonitor.HidD_GetInputReport(h, r, (uint)r.Length)) break;
            if (TryParseAudezeBatteryReport(r, r.Length, out int pct))
            {
                dev = new BatteryDeviceInfo("AUDEZE_MAXWELL", DongleBatteryMonitor.CleanDongleName(nameHint, "Audeze Maxwell"), PeripheralType.Headphones, pct, true);
                return true;
            }
        }
        return false;
    }

    internal static bool TryParseAudezeBatteryReport(byte[] r, int len, out int percent)
    {
        percent = -1;
        if (len < 12 || r[1] == 0) return false;
        int end = Math.Min(3 + r[1], len);
        for (int i = 3; i + 8 < end; i++)
        {
            if (r[i] == 0x05 && r[i + 1] == 0x5D && r[i + 4] == 0xD6 && r[i + 5] == 0x0C && r[i + 8] <= 100)
            {
                percent = r[i + 8];
                return true;
            }
        }
        return false;
    }

    private static bool TryPollJblQuantum(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF13, 0x01) ?? FindVendorChannel(group);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
        if (h != null && !h.IsInvalid)
        {
            byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 32)];
            for (int i = 0; i < 3; i++)
            {
                int n = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 20);
                if (n <= 0) break;
                if (n >= 2 && buf[0] == 0x08 && buf[1] <= 100)
                {
                    dev = new BatteryDeviceInfo("JBL_Q910", DongleBatteryMonitor.CleanDongleName(nameHint, "JBL Quantum 910 Wireless"), PeripheralType.Headphones, buf[1], true);
                    SaveSticky("JBL_Q910", dev);
                    return true;
                }
            }
        }
        return TryGetSticky("JBL_Q910", out dev);
    }

    private static bool TryPollSkullcandy(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF13, null);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] req = Pad(62, 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) return false;

        byte[] r = new byte[62];
        for (int t = 0; t < 6; t++)
        {
            Thread.Sleep(25);
            Array.Clear(r, 0, r.Length);
            r[0] = 0x07;
            if (!DongleBatteryMonitor.HidD_GetInputReport(h, r, (uint)r.Length)) break;
            if (TryParseAudezeBatteryReport(r, r.Length, out int pct))
            {
                dev = new BatteryDeviceInfo($"SKULLCANDY_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Skullcandy PLYR"), PeripheralType.Headphones, pct, true);
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    //  15..21 ODM & Famous Gaming Mice / Keyboards
    // =========================================================================

    private static bool TryPollAulaKeyboard(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF80, 0x0001) ?? FindVendorChannel(group);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(33, 0x00, 0xAA, 0x10), 60)) return false;
        byte[] r = new byte[33];
        for (int i = 0; i < 3; i++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 55);
            if (n <= 0) break;
            foreach (var (lead, at) in new[] { (0, 25), (1, 26) })
            {
                if (n > at && r[lead] == 0x55 && r[lead + 1] == 0x10 && r[at] is >= 1 and <= 100)
                {
                    dev = new BatteryDeviceInfo("AULA_FEFC", DongleBatteryMonitor.CleanDongleName(nameHint, "AULA Wireless Keyboard"), PeripheralType.Keyboard, r[at], true);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryPollCompxKeyboard(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF02, 0x0002) ?? FindByUsage(group, 0xFF02, null);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] Frame(byte cmd, byte arg)
        {
            byte[] f = new byte[20];
            f[0] = 0x13; f[1] = cmd; f[2] = arg;
            int sum = 0; for (int i = 0; i < 19; i++) sum += f[i];
            f[19] = (byte)(sum & 0xFF);
            return f;
        }

        DongleBatteryMonitor.WriteHidOverlapped(h, Frame(0x05, 0x01), 40);
        DongleBatteryMonitor.WriteHidOverlapped(h, Frame(0x4A, 0x01), 40);
        byte[] r = new byte[20];
        for (int t = 0; t < 5; t++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 45);
            if (n < 20 || r[0] != 0x13) continue;
            int sum = 0; for (int i = 0; i < 19; i++) sum += r[i];
            if ((sum & 0xFF) != r[19]) continue;
            int lvl = -1;
            if ((r[1] & 0x7F) == 0x4A && r[2] == 0x01 && r[3] == 0x00 && r[4] == 0x02) lvl = r[5];
            else if (r[1] == 0x0A && r[2] == 0x01 && r[4] == 0x04 && r[5] == 0x05) lvl = r[6];
            if (lvl is >= 1 and <= 100)
            {
                dev = new BatteryDeviceInfo("COMPX_KB_FA09", DongleBatteryMonitor.CleanDongleName(nameHint, "Wireless Mechanical Keyboard"), PeripheralType.Keyboard, lvl, true);
                return true;
            }
        }
        return false;
    }

    private static bool TryPollKeychron(List<HidChannelInfo> group, ushort vid, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFFC1, null) ?? FindVendorChannel(group);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        byte[] req = Pad(64, 0xB3, 0x06);
        DongleBatteryMonitor.HidD_SetFeature(h, req, (uint)req.Length);
        DongleBatteryMonitor.WriteHidOverlapped(h, req, 50);

        byte[] r = new byte[64];
        for (int t = 0; t < 4; t++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 55);
            if (n >= 21 && r[0] == 0xB4 && r[1] == 0x06)
            {
                int lvl = r[20] & 0x7F;
                if (lvl is >= 1 and <= 100)
                {
                    dev = new BatteryDeviceInfo($"KEYCHRON_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Keychron Wireless Mouse"), PeripheralType.Mouse, lvl, true);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryPollRapoo(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        foreach (var ch in group)
        {
            if (ch.UsagePage < 0xFF00) continue;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) continue;

            byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 32)];
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 20);
            if (n >= 8 && buf[0] == 0x07 && buf[6] is 1 or 2 && buf[7] <= 100)
            {
                dev = new BatteryDeviceInfo($"RAPOO_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Rapoo Wireless Mouse"), PeripheralType.Mouse, buf[7], true);
                SaveSticky($"RAPOO_{pid:X4}", dev);
                return true;
            }
            if (n >= 9 && buf[0] == 0xBD && buf[1] == 0x01 && buf[7] is 1 or 2 && buf[8] <= 100)
            {
                dev = new BatteryDeviceInfo($"RAPOO_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Rapoo Wireless Keyboard"), PeripheralType.Keyboard, buf[8], true);
                SaveSticky($"RAPOO_{pid:X4}", dev);
                return true;
            }
        }
        return TryGetSticky($"RAPOO_{pid:X4}", out dev);
    }

    private static bool TryPollVaxee(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF05, null);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] req = Pad(64, 0x0E, 0xA5, 0x0B, 0x01, 0x01);
        if (!DongleBatteryMonitor.HidD_SetFeature(h, req, (uint)req.Length)) return false;
        Thread.Sleep(35);
        byte[] resp = new byte[64]; resp[0] = 0x0E;
        if (!DongleBatteryMonitor.HidD_GetFeature(h, resp, (uint)resp.Length)) return false;

        if (resp[0] == 0x0E && resp[1] == 0xA5 && resp[2] == 0x0B && resp[5] <= 20)
        {
            dev = new BatteryDeviceInfo("VAXEE_MOUSE", DongleBatteryMonitor.CleanDongleName(nameHint, "VAXEE Wireless Mouse"), PeripheralType.Mouse, resp[5] * 5, true);
            return true;
        }
        return false;
    }

    private static bool TryPollFinalmouseUlx(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFF00, null);
        if (ch == null) return false;
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        DongleBatteryMonitor.DrainPendingReads(h, ch.InputReportByteLength);
        foreach (byte cmd in new byte[] { 38, 5 }) // CMD_BATTERY_STATUS then CMD_VBAT
        {
            byte[] req = Pad(64, 0x04, 0x02, (byte)(0x80 | cmd), 0x00);
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, req, 60)) continue;
            byte[] r = new byte[64];
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 70);
            if (n >= 6 && r[0] == 0x05 && (r[2] & 0x7F) == cmd)
            {
                int pct = -1;
                if (cmd == 38 && r[3] >= 1 && r[4] <= 100) pct = r[4];
                else if (cmd == 5 && r[3] >= 2)
                {
                    int mv = r[4] | (r[5] << 8);
                    if (mv is > 2500 and < 5000)
                        pct = InterpolateAscending(mv, new[] { 3000, 3620, 3660, 3740, 3880, 4170, 4380 }, new[] { 0, 5, 10, 25, 50, 75, 100 });
                }
                if (pct >= 0)
                {
                    dev = new BatteryDeviceInfo("FINALMOUSE_ULX", DongleBatteryMonitor.CleanDongleName(nameHint, "Finalmouse UltralightX"), PeripheralType.Mouse, pct, true);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryPollEndgameGear(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var feat = FindByUsage(group, 0xFF02, 0x0002) ?? FindByUsage(group, 0xFF02, null);
        var reply = FindByUsage(group, 0xFF01, 0x0000) ?? FindByUsage(group, 0xFF01, null);
        if (feat == null || reply == null) return false;

        using var hReply = DongleBatteryMonitor.OpenHidHandle(reply.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
        using var hFeat = DongleBatteryMonitor.OpenHidHandle(feat.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
        if (hReply == null || hFeat == null) return false;

        byte[] req = CompxBuildFrame(0x08, 0x04);
        if (!DongleBatteryMonitor.HidD_SetFeature(hFeat, req, (uint)req.Length)) return false;

        byte[] buf = new byte[Math.Max((int)reply.InputReportByteLength, 17)];
        for (int t = 0; t < 4; t++)
        {
            int n = DongleBatteryMonitor.ReadHidOverlapped(hReply, buf, 50);
            if (n >= 17 && buf[0] == 0x09 && buf[1] == 0x04 && CompxChecksumValid(buf, 17) && buf[6] <= 100)
            {
                dev = new BatteryDeviceInfo($"ENDGAME_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "Endgame Gear Wireless Mouse"), PeripheralType.Mouse, buf[6], true);
                return true;
            }
        }
        return false;
    }

    private static bool TryPollSinoWealthOrWlmouse(List<HidChannelInfo> group, ushort vid, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0xFFFF, null) ?? FindVendorChannel(group);
        if (ch == null || ch.FeatureReportByteLength < 9) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        int fLen = Math.Max((int)ch.FeatureReportByteLength, 65);
        byte[] req = Pad(fLen, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83);
        if (!DongleBatteryMonitor.HidD_SetFeature(h, req, (uint)fLen)) return false;

        Thread.Sleep(35);
        byte[] resp = new byte[fLen];
        if (!DongleBatteryMonitor.HidD_GetFeature(h, resp, (uint)fLen)) return false;

        if (resp.Length >= 9 && resp[6] == 0x83 && resp[1] is 0xA1 or 0xA2 && resp[8] is >= 1 and <= 100)
        {
            string fallback = vid == VidWlmouse ? "WLMouse Wireless" : vid == VidLamzu ? "LAMZU Wireless Mouse" : vid == VidSinoWealthGlorious ? "Glorious Wireless Mouse" : "MCHOSE Wireless Mouse";
            dev = new BatteryDeviceInfo($"SINOWEALTH_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, fallback), PeripheralType.Mouse, resp[8], true);
            return true;
        }
        return false;
    }

    private static bool TryPollMchoseGroup(List<HidChannelInfo> group, ushort vid, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        // MCHOSE Keyboard 3837:3033
        if (vid == VidMchose && pid == 0x3033)
        {
            var ch = FindByUsage(group, 0x01, null) ?? group[0];
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(65, 0x00, 0x55, 0x04, 0x00, 0x38, 0x38, 0x00, 0x00, 0x00), 60)) return false;
            byte[] r = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 60) >= 41 && r[0] == 0xAA && r[1] == 0x04 && (r[40] & 0x7F) is >= 1 and <= 100)
            {
                dev = new BatteryDeviceInfo("MCHOSE_KB_3033", DongleBatteryMonitor.CleanDongleName(nameHint, "MCHOSE Wireless Keyboard"), PeripheralType.Keyboard, r[40] & 0x7F, true);
                return true;
            }
            return false;
        }

        // MCHOSE X9 / V9 Pro Headset (3837:6045 / 291D:385D)
        if ((vid == VidMchose && pid == 0x6045) || (vid == VidMchoseHeadset && pid == 0x385D))
        {
            var ch = FindByUsage(group, 0xFF90, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(64, 0x55, 0x65, 0x01), 60)) return false;
            byte[] r = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 60) >= 4 && r[0] == 0x55 && r[1] == 0x65 && r[3] == 0x02 && r[2] <= 100)
            {
                dev = new BatteryDeviceInfo($"MCHOSE_HS_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "MCHOSE Wireless Headset"), PeripheralType.Headphones, r[2], true);
                return true;
            }
            return false;
        }

        // MCHOSE RealTek mice (inverted 0xFF feature report 0x11)
        if (vid == VidMchoseAlt || (vid == VidMchose && pid is 0x1001 or 0x4150 or 0x4022 or 0x100B or 0x100C))
        {
            var ch = FindByUsage(group, 0xFF01, null) ?? FindByUsage(group, 0xFF0B, null);
            if (ch == null || ch.FeatureReportByteLength < 21) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;

            byte[] req = new byte[21];
            req[0] = 0x11;
            req[1] = 0x06 ^ 0xFF;
            for (int i = 2; i < 21; i++) req[i] = 0xFF;
            if (!DongleBatteryMonitor.HidD_SetFeature(h, req, 21)) return false;

            Thread.Sleep(25);
            byte[] raw = new byte[21]; raw[0] = 0x11;
            if (!DongleBatteryMonitor.HidD_GetFeature(h, raw, 21)) return false;
            if (raw[0] == 0x11 && (raw[1] ^ 0xFF) == 0x06)
            {
                int lvl = raw[2 + 9] ^ 0xFF;
                if (lvl is >= 1 and <= 100)
                {
                    dev = new BatteryDeviceInfo($"MCHOSE_M_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "MCHOSE Wireless Mouse"), PeripheralType.Mouse, lvl, true);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryPollAtkSpecial(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        if (pid is 0x108B or 0x108C) // NearLink
        {
            var ch = FindByUsage(group, 0xFFB1, null) ?? FindByUsage(group, 0xFFB2, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            if (!DongleBatteryMonitor.WriteHidOverlapped(h, Pad(64, 0x05, 0x01, 0x00, 0x1A), 60)) return false;
            byte[] r = new byte[64];
            if (DongleBatteryMonitor.ReadHidOverlapped(h, r, 65) >= 10 && r[1] == 0x01 && r[2] == 0x00 && r[8] is >= 1 and <= 100)
            {
                dev = new BatteryDeviceInfo($"ATK_NL_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "ATK X1 Wireless Mouse"), PeripheralType.Mouse, r[8], true);
                return true;
            }
        }
        else // Holtek 10F0 / 10F1 / 1178
        {
            var ch = FindByUsage(group, 0xFF10, 0x0001) ?? FindByUsage(group, 0xFF10, null);
            if (ch == null) return false;
            using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
            if (h == null || h.IsInvalid) return false;
            DongleBatteryMonitor.HidD_SetFeature(h, Pad(9, 0x00, 0x82), 9);
            byte[] r = new byte[Math.Max((int)ch.InputReportByteLength, 16)];
            int n = DongleBatteryMonitor.ReadHidOverlapped(h, r, 60);
            if (n >= 9 && r[0] == 0x82)
            {
                int lvl = r[8] & 0x7F;
                if (lvl is >= 1 and <= 100)
                {
                    dev = new BatteryDeviceInfo($"ATK_HT_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "ATK Wireless Mouse"), PeripheralType.Mouse, lvl, true);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryPollBekenBroadcast(List<HidChannelInfo> group, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindByUsage(group, 0x000A, null) ?? FindVendorChannel(group);
        if (ch == null) return false;

        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
        if (h != null && !h.IsInvalid)
        {
            byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 32)];
            for (int i = 0; i < 3; i++)
            {
                int n = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 20);
                if (n <= 0) break;
                if (TryParseBekenFrame(buf, n, out int pct))
                {
                    string fallback = pid == 0xFA65 ? "Inphic Wireless Mouse" : "Attack Shark Wireless Mouse";
                    dev = new BatteryDeviceInfo($"BEKEN_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, fallback), PeripheralType.Mouse, pct, true);
                    SaveSticky($"BK_{pid:X4}", dev);
                    return true;
                }
            }
        }
        return TryGetSticky($"BK_{pid:X4}", out dev);
    }

    internal static bool TryParseBekenFrame(byte[] buf, int len, out int percent)
    {
        percent = -1;
        if (len < 5 || buf[0] != 0x03 || buf[2] != 0x40) return false;
        byte model = buf[1];
        if (model == 0x10) // X6 / R1 1..10 steps
        {
            int pct = buf[4] * 10;
            if (pct is >= 10 and <= 100) { percent = pct; return true; }
            return false;
        }
        if (model is 0xE5 or 0x55 or 0x4E or 0x95 or 0x90 or 0x93 or 0x99 or 0x85 or 0x4D or 0xBE or 0x07)
        {
            if (buf[3] == 0x02) { percent = 100; return true; }
            if (buf[3] is 0x01 or 0x03 && buf[4] is >= 1 and <= 100)
            {
                percent = buf[4];
                return true;
            }
        }
        return false;
    }

    private static bool TryPollAjazz(List<HidChannelInfo> group, ushort vid, ushort pid, string? nameHint, out BatteryDeviceInfo dev)
    {
        dev = default;
        var ch = FindVendorChannel(group) ?? (group.Count > 1 ? group[^1] : group[0]);
        using var h = DongleBatteryMonitor.OpenHidHandle(ch.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h != null && !h.IsInvalid)
        {
            byte[] buf = new byte[Math.Max((int)ch.InputReportByteLength, 33)];
            for (int i = 0; i < 3; i++)
            {
                int n = DongleBatteryMonitor.ReadHidOverlapped(h, buf, 25);
                if (n <= 0) break;
                if (n >= 3 && buf[0] == 0xC0 && buf[2] is >= 1 and <= 100)
                {
                    dev = new BatteryDeviceInfo($"AJAZZ_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "AJAZZ Wireless Mouse"), PeripheralType.Mouse, buf[2], true);
                    SaveSticky($"AJ_{vid:X4}_{pid:X4}", dev);
                    return true;
                }
            }
        }
        return TryGetSticky($"AJ_{vid:X4}_{pid:X4}", out dev);
    }

    private static bool TryPollCompxKysonaGroup(
        List<HidChannelInfo> group,
        ushort vid,
        ushort pid,
        string? nameHint,
        PeripheralType kind,
        out BatteryDeviceInfo dev)
    {
        dev = default;
        var cmdCh = FindByUsage(group, 0xFF02, 0x0002) ?? FindByUsage(group, 0xFF02, null);
        if (cmdCh == null) return false;

        // Dareu A950 (260D:1074 / 1084): Feature 0x08 -> Input 0x09 on FF01
        if (vid == VidCompxDareu)
        {
            var replyCh = FindByUsage(group, 0xFF01, 0x0000) ?? FindByUsage(group, 0xFF01, null);
            if (replyCh != null)
            {
                using var hr = DongleBatteryMonitor.OpenHidHandle(replyCh.Path, readWrite: false, overlapped: true, desiredAccessZero: false);
                using var hf = DongleBatteryMonitor.OpenHidHandle(cmdCh.Path, readWrite: true, overlapped: false, desiredAccessZero: false);
                if (hr != null && hf != null)
                {
                    DongleBatteryMonitor.HidD_SetFeature(hf, CompxBuildFrame(0x08, 0x04), 17);
                    byte[] rb = new byte[Math.Max((int)replyCh.InputReportByteLength, 17)];
                    int n = DongleBatteryMonitor.ReadHidOverlapped(hr, rb, 70);
                    if (n >= 17 && rb[0] == 0x09 && rb[1] == 0x04 && CompxChecksumValid(rb, 17) && rb[6] <= 100)
                    {
                        dev = new BatteryDeviceInfo($"DAREU_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, "DAREU Wireless Mouse"), kind, rb[6], true);
                        return true;
                    }
                }
            }
        }

        // Standard CompX 17-byte Input/Output OR Feature (Redragon M901P-KS 25A7:FA07, Pulsar, VXE, ATK, Hitscan)
        using var h = DongleBatteryMonitor.OpenHidHandle(cmdCh.Path, readWrite: true, overlapped: true, desiredAccessZero: false);
        if (h == null || h.IsInvalid) return false;

        byte[] batReq = CompxBuildFrame(0x08, 0x04);
        if (cmdCh.InputReportByteLength >= 17 && DongleBatteryMonitor.WriteHidOverlapped(h, batReq, 55))
        {
            byte[] rb = new byte[Math.Max((int)cmdCh.InputReportByteLength, 17)];
            for (int t = 0; t < 4; t++)
            {
                int n = DongleBatteryMonitor.ReadHidOverlapped(h, rb, 50);
                if (n <= 0) break;
                if (TryParseCompx17Battery(rb, n, out int pct))
                {
                    string fallback = vid == VidCompxPulsarRedragon ? "Redragon / Pulsar Wireless Mouse" : "Wireless Gaming Mouse";
                    dev = new BatteryDeviceInfo($"COMPX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, fallback), kind, pct, true);
                    SaveSticky($"CX_{vid:X4}_{pid:X4}", dev);
                    return true;
                }
            }
        }

        // Feature fallback (Redragon M901P-KS 25A7:FA07 / Ajazz AJ199 25A7:FA7C)
        if (cmdCh.FeatureReportByteLength >= 17 && DongleBatteryMonitor.HidD_SetFeature(h, batReq, 17))
        {
            Thread.Sleep(35);
            byte[] fb = new byte[cmdCh.FeatureReportByteLength];
            fb[0] = 0x08;
            if (DongleBatteryMonitor.HidD_GetFeature(h, fb, (uint)fb.Length) &&
                TryParseCompx17Battery(fb, fb.Length, out int pct))
            {
                string fallback = vid == VidCompxPulsarRedragon ? "Redragon Wireless Mouse" : "Wireless Gaming Mouse";
                dev = new BatteryDeviceInfo($"COMPX_{vid:X4}_{pid:X4}", DongleBatteryMonitor.CleanDongleName(nameHint, fallback), kind, pct, true);
                SaveSticky($"CX_{vid:X4}_{pid:X4}", dev);
                return true;
            }
        }

        return TryGetSticky($"CX_{vid:X4}_{pid:X4}", out dev);
    }

    internal static byte[] CompxBuildFrame(byte reportId, byte cmd)
    {
        byte[] f = new byte[17];
        f[0] = reportId;
        f[1] = cmd;
        int sum = 0; for (int i = 0; i < 16; i++) sum += f[i];
        f[16] = (byte)((0x55 - sum) & 0xFF);
        return f;
    }

    internal static bool CompxChecksumValid(byte[] f, int len)
    {
        if (len < 17) return false;
        int sum = 0; for (int i = 0; i < 16; i++) sum += f[i];
        return ((0x55 - sum) & 0xFF) == f[16];
    }

    internal static bool TryParseCompx17Battery(byte[] r, int len, out int percent)
    {
        percent = -1;
        if (len < 10 || r[0] != 0x08 || r[1] != 0x04) return false;
        if (r[6] < 1 || r[6] > 100) return false;
        percent = r[6];
        return true;
    }

    // =========================================================================
    //  Shared Helpers
    // =========================================================================

    private static HidChannelInfo? FindByUsage(List<HidChannelInfo> group, ushort usagePage, ushort? usage)
    {
        if (usage.HasValue)
        {
            foreach (var c in group)
                if (c.UsagePage == usagePage && c.Usage == usage.Value) return c;
        }
        foreach (var c in group)
            if (c.UsagePage == usagePage) return c;
        return null;
    }

    private static HidChannelInfo? FindVendorChannel(List<HidChannelInfo> group)
    {
        foreach (var c in group)
            if (c.UsagePage >= 0xFF00) return c;
        return null;
    }

    private static byte[] Pad(int length, params byte[] prefix)
    {
        byte[] arr = new byte[Math.Max(length, prefix.Length)];
        Buffer.BlockCopy(prefix, 0, arr, 0, prefix.Length);
        return arr;
    }

    private static int InterpolateDescending(int mv, int[] voltsDesc, int[] pctsDesc)
    {
        if (mv >= voltsDesc[0]) return pctsDesc[0];
        if (mv <= voltsDesc[^1]) return pctsDesc[^1];
        for (int i = 0; i < voltsDesc.Length - 1; i++)
        {
            if (mv <= voltsDesc[i] && mv >= voltsDesc[i + 1])
            {
                double t = (double)(mv - voltsDesc[i + 1]) / (voltsDesc[i] - voltsDesc[i + 1]);
                return Math.Clamp((int)Math.Round(pctsDesc[i + 1] + t * (pctsDesc[i] - pctsDesc[i + 1])), 0, 100);
            }
        }
        return pctsDesc[^1];
    }

    private static int InterpolateAscending(int mv, int[] voltsAsc, int[] pctsAsc)
    {
        if (mv <= voltsAsc[0]) return pctsAsc[0];
        if (mv >= voltsAsc[^1]) return pctsAsc[^1];
        for (int i = 0; i < voltsAsc.Length - 1; i++)
        {
            if (mv >= voltsAsc[i] && mv <= voltsAsc[i + 1])
            {
                double t = (double)(mv - voltsAsc[i]) / (voltsAsc[i + 1] - voltsAsc[i]);
                return Math.Clamp((int)Math.Round(pctsAsc[i] + t * (pctsAsc[i + 1] - pctsAsc[i])), 0, 100);
            }
        }
        return pctsAsc[^1];
    }
}
