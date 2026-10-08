using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Launcher.Native;

/// <summary>
/// Result data for HDR display queries across Windows and Linux.
/// </summary>
public readonly record struct HdrDisplayInfo(
    bool Success,
    bool IsHdrActive,
    float PeakNits,
    float MinNits,
    string StatusMessage
)
{
    public void Deconstruct(out bool isHdrActive, out float peakNits, out float minNits)
    {
        isHdrActive = IsHdrActive;
        peakNits = PeakNits;
        minNits = MinNits;
    }
}

/// <summary>
/// Cross-platform display query utility for detecting HDR state and hardware peak luminance.
/// - On Windows: queries DXGI 1.6 (<c>IDXGIOutput6::GetDesc1</c>).
/// - On Linux: inspects DRM sysfs connectors (/sys/class/drm/*/edid) for CTA-861 HDR metadata,
///   Gamescope HDR environment flags, and KDE Plasma 6 display configurations.
/// </summary>
public static class HdrDisplayDetector
{
    [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        int GetWindowAssociation(out IntPtr pWindowHandle);
        int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        int EnumAdapters1(uint adapter, out IntPtr ppAdapter);
        int IsCurrent();
    }

    [Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumOutputs(uint output, out IntPtr ppOutput);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public float RedPrimary0, RedPrimary1;
        public float GreenPrimary0, GreenPrimary1;
        public float BluePrimary0, BluePrimary1;
        public float WhitePoint0, WhitePoint1;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    [Guid("068346e8-aaec-4b84-add7-137f513f77a1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput6
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int GetDesc(IntPtr pDesc);
        int GetDisplayModeList(int enumFormat, uint flags, ref uint pNumModes, IntPtr pDesc);
        int FindClosestMatchingMode(IntPtr pModeToMatch, IntPtr pClosestMatch, IntPtr pConcernedDevice);
        int WaitForVBlank();
        int TakeOwnership(IntPtr pDevice, int exclusive);
        void ReleaseOwnership();
        int GetGammaControlCapabilities(IntPtr pGammaCaps);
        int SetGammaControl(IntPtr pArray);
        int GetGammaControl(out IntPtr pArray);
        int SetDisplaySurface(IntPtr pScanoutSurface);
        int GetDisplaySurfaceData(IntPtr pDestination);
        int GetFrameStatistics(out IntPtr pStats);
        int GetDisplayModeList1(int enumFormat, uint flags, ref uint pNumModes, IntPtr pDesc);
        int FindClosestMatchingMode1(IntPtr pModeToMatch, IntPtr pClosestMatch, IntPtr pConcernedDevice);
        int GetDisplaySurfaceData1(IntPtr pDestination);
        int DuplicateOutput(IntPtr pDevice, out IntPtr ppOutputDuplication);
        int SupportsOverlays();
        int CheckOverlaySupport(int enumFormat, IntPtr pConcernedDevice, out uint pFlags);
        int CheckOverlayColorSpaceSupport(int enumFormat, int colorSpace, IntPtr pConcernedDevice, out uint pFlags);
        int DuplicateOutput1(IntPtr pDevice, uint flags, uint supportedFormatsCount, IntPtr pSupportedFormats, out IntPtr ppOutputDuplication);
        int GetDesc1(out DXGI_OUTPUT_DESC1 pDesc);
        int CheckHardwareCompositionSupport(out uint pFlags);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1([In] ref Guid riid, out IDXGIFactory1 ppFactory);

    // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12
    private const int DxgiColorSpaceRgbFullG2084NoneP2020 = 12;

    /// <summary>
    /// Queries the primary display for HDR capabilities and peak luminance.
    /// Supports Windows (DXGI 1.6) and Linux (DRM sysfs CTA-861 EDID + compositor detection).
    /// </summary>
    public static HdrDisplayInfo QueryPrimaryDisplay()
    {
        if (OperatingSystem.IsWindows())
        {
            return QueryWindowsDisplay();
        }

        if (OperatingSystem.IsLinux())
        {
            return QueryLinuxDisplay();
        }

        return new HdrDisplayInfo(false, false, 0f, 0f, "HDR display query not supported on this operating system");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static HdrDisplayInfo QueryWindowsDisplay()
    {
        try
        {
            Guid factoryGuid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref factoryGuid, out IDXGIFactory1 factory) != 0)
            {
                return new HdrDisplayInfo(false, false, 0f, 0f, "Failed to initialize DXGI factory");
            }

            uint adapterIndex = 0;
            while (factory.EnumAdapters1(adapterIndex++, out IntPtr pAdapter) == 0)
            {
                var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(pAdapter);
                uint outputIndex = 0;
                while (adapter.EnumOutputs(outputIndex++, out IntPtr pOutput) == 0)
                {
                    Guid output6Guid = typeof(IDXGIOutput6).GUID;
                    if (Marshal.QueryInterface(pOutput, in output6Guid, out IntPtr pOutput6) == 0)
                    {
                        var output6 = (IDXGIOutput6)Marshal.GetObjectForIUnknown(pOutput6);
                        if (output6.GetDesc1(out DXGI_OUTPUT_DESC1 desc) == 0)
                        {
                            bool isHdr = desc.ColorSpace == DxgiColorSpaceRgbFullG2084NoneP2020;
                            float peak = desc.MaxLuminance;
                            float min = desc.MinLuminance;
                            Marshal.Release(pOutput6);
                            Marshal.Release(pOutput);
                            Marshal.Release(pAdapter);

                            if (isHdr)
                            {
                                float reportedPeak = peak > 0f ? peak : 1000f;
                                return new HdrDisplayInfo(
                                    true,
                                    true,
                                    reportedPeak,
                                    min,
                                    $"Detected {Math.Round(reportedPeak)} nits (Windows HDR Active)");
                            }

                            if (peak > 120f)
                            {
                                return new HdrDisplayInfo(
                                    true,
                                    false,
                                    peak,
                                    min,
                                    $"Detected {Math.Round(peak)} nits (HDR capable, but Windows HDR is disabled in Windows Settings)");
                            }

                            return new HdrDisplayInfo(
                                false,
                                false,
                                0f,
                                0f,
                                "Display is currently in SDR mode (Enable 'Use HDR' in Windows Settings)");
                        }
                        Marshal.Release(pOutput6);
                    }
                    Marshal.Release(pOutput);
                }
                Marshal.Release(pAdapter);
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[display] DXGI query exception: {ex.Message}");
            return new HdrDisplayInfo(false, false, 0f, 0f, $"DXGI query failed: {ex.Message}");
        }

        return new HdrDisplayInfo(false, false, 0f, 0f, "No connected HDR displays found via DXGI");
    }

    private static HdrDisplayInfo QueryLinuxDisplay()
    {
        try
        {
            // 1. Check Gamescope / Steam Deck environment variables
            string? gamescopePeak = System.Environment.GetEnvironmentVariable("GAMESCOPE_HDR_MAX_OUTPUT_NITS");
            if (!string.IsNullOrEmpty(gamescopePeak) &&
                float.TryParse(gamescopePeak, NumberStyles.Float, CultureInfo.InvariantCulture, out float gsPeak) &&
                gsPeak > 0f)
            {
                return new HdrDisplayInfo(true, true, gsPeak, 0f, $"Detected {Math.Round(gsPeak)} nits (Gamescope HDR)");
            }

            bool envHdrActive =
                string.Equals(System.Environment.GetEnvironmentVariable("HDR_ENABLED"), "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.Environment.GetEnvironmentVariable("ENABLE_HDR"), "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.Environment.GetEnvironmentVariable("DXVK_HDR"), "1", StringComparison.OrdinalIgnoreCase);

            // 2. Check KDE Plasma 6 display configuration (kwinoutputconfig.json)
            bool kdeHdrActive = false;
            float kdePeak = 0f;
            string homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string kwinConfig = Path.Combine(homeDir, ".config", "kwinoutputconfig.json");
            if (File.Exists(kwinConfig))
            {
                try
                {
                    string json = File.ReadAllText(kwinConfig);
                    if (json.Contains("\"highDynamicRange\": true") || json.Contains("\"highDynamicRange\":true"))
                    {
                        kdeHdrActive = true;
                    }
                    var match = Regex.Match(json, @"""maxPeakBrightness""\s*:\s*([0-9]+)");
                    if (match.Success &&
                        float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedKdePeak) &&
                        parsedKdePeak > 0f)
                    {
                        kdePeak = parsedKdePeak;
                    }
                }
                catch
                {
                    // Ignore JSON read errors
                }
            }

            // 3. Scan DRM sysfs connectors for hardware EDID CTA-861 blocks
            const string drmPath = "/sys/class/drm";
            if (Directory.Exists(drmPath))
            {
                string[] connectorDirs = Directory.GetDirectories(drmPath, "card*-*");
                float bestPeak = 0f;
                float bestMin = 0f;
                bool foundHdrCapable = false;
                string? bestConnector = null;

                foreach (string dir in connectorDirs)
                {
                    string statusFile = Path.Combine(dir, "status");
                    if (!File.Exists(statusFile)) continue;

                    string status = File.ReadAllText(statusFile).Trim();
                    if (!status.Equals("connected", StringComparison.OrdinalIgnoreCase)) continue;

                    string edidFile = Path.Combine(dir, "edid");
                    if (!File.Exists(edidFile)) continue;

                    try
                    {
                        byte[] edidBytes = File.ReadAllBytes(edidFile);
                        if (TryParseEdid(edidBytes, out bool isHdr, out float peak, out float min))
                        {
                            if (isHdr || peak > 0f)
                            {
                                bestPeak = peak;
                                bestMin = min;
                                foundHdrCapable = true;
                                bestConnector = Path.GetFileName(dir);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore individual connector read errors
                    }
                }

                if (foundHdrCapable || kdePeak > 0f || bestPeak > 0f)
                {
                    float finalPeak = bestPeak > 0f ? bestPeak : (kdePeak > 0f ? kdePeak : 1000f);
                    bool isHdr = envHdrActive || kdeHdrActive;
                    string detail = isHdr ? "HDR Mode Active" : "HDR Capable (Desktop currently in SDR mode)";
                    string connStr = bestConnector != null ? $" on {bestConnector}" : string.Empty;
                    return new HdrDisplayInfo(
                        true,
                        isHdr,
                        finalPeak,
                        bestMin,
                        $"Detected {Math.Round(finalPeak)} nits{connStr} ({detail})");
                }
            }

            if (kdeHdrActive && kdePeak > 0f)
            {
                return new HdrDisplayInfo(
                    true,
                    true,
                    kdePeak,
                    0f,
                    $"Detected {Math.Round(kdePeak)} nits (KDE Plasma HDR Active)");
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[display] Linux display query exception: {ex.Message}");
            return new HdrDisplayInfo(false, false, 0f, 0f, $"Linux display query failed: {ex.Message}");
        }

        return new HdrDisplayInfo(
            false,
            false,
            0f,
            0f,
            "No HDR display metadata found in Linux DRM connectors or compositor");
    }

    /// <summary>
    /// Parses raw EDID bytes and CTA-861 Extension Blocks for HDR Static Metadata (Extended Tag 0x06).
    /// Extracts EOTF support and Desired Content Max Luminance (converted to nits via standard CTA-861.3 formula).
    /// </summary>
    public static bool TryParseEdid(byte[] edid, out bool isHdrCapable, out float peakNits, out float minNits)
    {
        isHdrCapable = false;
        peakNits = 0f;
        minNits = 0f;

        if (edid == null || edid.Length < 128)
            return false;

        // Verify standard EDID header: 00 FF FF FF FF FF FF 00
        if (edid[0] != 0x00 || edid[1] != 0xFF || edid[2] != 0xFF || edid[3] != 0xFF ||
            edid[4] != 0xFF || edid[5] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
        {
            return false;
        }

        int extensionBlocks = edid[126];
        for (int ext = 1; ext <= extensionBlocks; ext++)
        {
            int offset = ext * 128;
            if (offset + 128 > edid.Length)
                break;

            byte tag = edid[offset];
            if (tag == 0x02) // CTA-861 (CEA-EDID Extension Block)
            {
                byte d = edid[offset + 2];
                if (d < 4) continue;

                int end = offset + Math.Min((int)d, 127);
                int pos = offset + 4;

                while (pos < end)
                {
                    byte header = edid[pos++];
                    int tagCode = (header >> 5) & 0x07;
                    int length = header & 0x1F;

                    if (pos + length > offset + 128)
                        break;

                    if (tagCode == 7 && length >= 2) // Extended Tag Data Block
                    {
                        byte extTag = edid[pos];
                        if (extTag == 0x06) // HDR Static Metadata Data Block
                        {
                            byte eotf = edid[pos + 1];
                            // Bit 2: SMPTE ST 2084 (PQ / HDR10), Bit 3: Hybrid Log-Gamma (HLG)
                            bool supportsPq = (eotf & (1 << 2)) != 0;
                            bool supportsHlg = (eotf & (1 << 3)) != 0;
                            if (supportsPq || supportsHlg)
                            {
                                isHdrCapable = true;
                            }

                            if (length >= 4)
                            {
                                byte cvMax = edid[pos + 3];
                                if (cvMax > 0)
                                {
                                    // CTA-861.3 formula: Max Luminance (cd/m^2) = 50 * 2^(CV / 32)
                                    peakNits = (float)(50.0 * Math.Pow(2.0, cvMax / 32.0));
                                }
                            }

                            if (length >= 6)
                            {
                                byte cvMin = edid[pos + 5];
                                if (cvMin > 0 && peakNits > 0f)
                                {
                                    // CTA-861.3 formula: Min Luminance = Max Luminance * (CV_min / 255)^2 / 100
                                    minNits = peakNits * (float)Math.Pow(cvMin / 255.0, 2.0) / 100.0f;
                                }
                            }

                            if (isHdrCapable || peakNits > 0f)
                            {
                                return true;
                            }
                        }
                    }

                    pos += length;
                }
            }
        }

        return isHdrCapable;
    }
}
