using System.Runtime.InteropServices;
using System.Text;
using static DialDump.NativeMethods;

namespace DialDump;

/// <summary>Raw Input で見える HID デバイス 1 件分の情報と、レポート解析</summary>
internal sealed class HidDevice : IDisposable
{
    public const ushort UsagePageGenericDesktop = 0x01;
    public const ushort UsageSystemMultiAxis = 0x0E;
    public const ushort UsageDial = 0x37;
    public const ushort UsagePageButton = 0x09;

    public IntPtr Handle { get; }
    public string Path { get; }
    public uint VendorId { get; }
    public uint ProductId { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }

    /// <summary>preparsed data(取得できなかった場合は IntPtr.Zero)</summary>
    public IntPtr Preparsed { get; private set; }
    public HIDP_CAPS Caps { get; }

    /// <summary>入力レポート内の Dial(0x01/0x37)の値定義。無ければ null</summary>
    public HIDP_VALUE_CAPS? DialCap { get; }

    public bool IsRadialController => UsagePage == UsagePageGenericDesktop && Usage == UsageSystemMultiAxis;

    private HidDevice(IntPtr handle, string path, RID_DEVICE_INFO_HID info)
    {
        Handle = handle;
        Path = path;
        VendorId = info.dwVendorId;
        ProductId = info.dwProductId;
        UsagePage = info.usUsagePage;
        Usage = info.usUsage;

        Preparsed = GetPreparsed(handle);
        if (Preparsed != IntPtr.Zero && HidP_GetCaps(Preparsed, out var caps) == HIDP_STATUS_SUCCESS)
        {
            Caps = caps;
            DialCap = GetValueCaps(HIDP_REPORT_TYPE.Input)
                .Where(v => v.UsagePage == UsagePageGenericDesktop && ContainsUsage(v.IsRange, v.UsageMin, v.UsageMax, UsageDial))
                .Select(v => (HIDP_VALUE_CAPS?)v)
                .FirstOrDefault();
        }
    }

    /// <summary>HID 型の Raw Input デバイスをすべて列挙する</summary>
    public static List<HidDevice> Enumerate()
    {
        var result = new List<HidDevice>();
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(IntPtr.Zero, ref count, itemSize);
        if (count == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)(count * itemSize));
        try
        {
            uint got = GetRawInputDeviceList(buffer, ref count, itemSize);
            if (got == uint.MaxValue) return result;
            for (int i = 0; i < got; i++)
            {
                var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buffer + (int)(i * itemSize));
                if (item.dwType != RIM_TYPEHID) continue;
                var dev = TryCreate(item.hDevice);
                if (dev != null) result.Add(dev);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>Raw Input のデバイスハンドルから情報を取得する(HID 以外や失敗時は null)</summary>
    public static HidDevice? TryCreate(IntPtr hDevice)
    {
        var info = new RID_DEVICE_INFO_HID { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO_HID>() };
        uint size = info.cbSize;
        IntPtr p = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, p, ref size) == uint.MaxValue) return null;
            info = Marshal.PtrToStructure<RID_DEVICE_INFO_HID>(p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
        if (info.dwType != RIM_TYPEHID) return null;
        return new HidDevice(hDevice, GetName(hDevice), info);
    }

    private static string GetName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "";
        IntPtr p = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, p, ref chars) == uint.MaxValue
                ? ""
                : Marshal.PtrToStringUni(p) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static IntPtr GetPreparsed(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return IntPtr.Zero;
        IntPtr p = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, p, ref size) == uint.MaxValue)
        {
            Marshal.FreeHGlobal(p);
            return IntPtr.Zero;
        }
        return p;
    }

    private static bool ContainsUsage(byte isRange, ushort min, ushort max, ushort usage)
        => isRange != 0 ? usage >= min && usage <= max : usage == min;

    public HIDP_VALUE_CAPS[] GetValueCaps(HIDP_REPORT_TYPE type)
    {
        ushort n = type switch
        {
            HIDP_REPORT_TYPE.Input => Caps.NumberInputValueCaps,
            HIDP_REPORT_TYPE.Output => Caps.NumberOutputValueCaps,
            _ => Caps.NumberFeatureValueCaps,
        };
        if (n == 0 || Preparsed == IntPtr.Zero) return [];
        var arr = new HIDP_VALUE_CAPS[n];
        return HidP_GetValueCaps(type, arr, ref n, Preparsed) == HIDP_STATUS_SUCCESS ? arr[..n] : [];
    }

    public HIDP_BUTTON_CAPS[] GetButtonCaps(HIDP_REPORT_TYPE type)
    {
        ushort n = type switch
        {
            HIDP_REPORT_TYPE.Input => Caps.NumberInputButtonCaps,
            HIDP_REPORT_TYPE.Output => Caps.NumberOutputButtonCaps,
            _ => Caps.NumberFeatureButtonCaps,
        };
        if (n == 0 || Preparsed == IntPtr.Zero) return [];
        var arr = new HIDP_BUTTON_CAPS[n];
        return HidP_GetButtonCaps(type, arr, ref n, Preparsed) == HIDP_STATUS_SUCCESS ? arr[..n] : [];
    }

    /// <summary>入力レポートから Dial の相対値を取り出す(符号拡張込み)。取れなければ null</summary>
    public int? ReadDial(byte[] report)
    {
        if (DialCap is not { } cap || Preparsed == IntPtr.Zero) return null;
        if (report.Length == 0 || report[0] != cap.ReportID) return null;
        int st = HidP_GetUsageValue(HIDP_REPORT_TYPE.Input, cap.UsagePage, cap.LinkCollection, UsageDial,
            out uint raw, Preparsed, report, (uint)report.Length);
        if (st != HIDP_STATUS_SUCCESS) return null;
        if (cap.LogicalMin < 0 && cap.BitSize is > 0 and < 32)
        {
            uint signBit = 1u << (cap.BitSize - 1);
            if ((raw & signBit) != 0) return (int)(raw | ~((1u << cap.BitSize) - 1));
        }
        return (int)raw;
    }

    /// <summary>入力レポート中で ON になっている Button ページの usage 一覧</summary>
    public ushort[] ReadButtons(byte[] report)
    {
        if (Preparsed == IntPtr.Zero) return [];
        uint max = HidP_MaxUsageListLength(HIDP_REPORT_TYPE.Input, UsagePageButton, Preparsed);
        if (max == 0) return [];
        var list = new ushort[max];
        uint len = max;
        int st = HidP_GetUsages(HIDP_REPORT_TYPE.Input, UsagePageButton, 0, list, ref len, Preparsed, report, (uint)report.Length);
        return st == HIDP_STATUS_SUCCESS ? list[..(int)len] : [];
    }

    /// <summary>Caps(レポート構造)を人が読める形で出力する</summary>
    public string DescribeCaps()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {this}");
        sb.AppendLine($"Path: {Path}");
        if (Preparsed == IntPtr.Zero)
        {
            sb.AppendLine("preparsed data を取得できませんでした");
            return sb.ToString();
        }
        sb.AppendLine($"TopLevel UsagePage=0x{Caps.UsagePage:X2} Usage=0x{Caps.Usage:X2}");
        sb.AppendLine($"ReportByteLength Input={Caps.InputReportByteLength} Output={Caps.OutputReportByteLength} Feature={Caps.FeatureReportByteLength}");
        foreach (var type in new[] { HIDP_REPORT_TYPE.Input, HIDP_REPORT_TYPE.Output, HIDP_REPORT_TYPE.Feature })
        {
            foreach (var b in GetButtonCaps(type))
            {
                sb.AppendLine($"[{type}] Button ReportID={b.ReportID} Page=0x{b.UsagePage:X2} " +
                    (b.IsRange != 0 ? $"Usage=0x{b.UsageMin:X2}-0x{b.UsageMax:X2}" : $"Usage=0x{b.UsageMin:X2}") +
                    $" Link={b.LinkCollection}(0x{b.LinkUsagePage:X2}/0x{b.LinkUsage:X2})");
            }
            foreach (var v in GetValueCaps(type))
            {
                sb.AppendLine($"[{type}] Value  ReportID={v.ReportID} Page=0x{v.UsagePage:X2} " +
                    (v.IsRange != 0 ? $"Usage=0x{v.UsageMin:X2}-0x{v.UsageMax:X2}" : $"Usage=0x{v.UsageMin:X2}") +
                    $" Bits={v.BitSize}x{v.ReportCount} Abs={v.IsAbsolute} Logical={v.LogicalMin}..{v.LogicalMax}" +
                    $" Physical={v.PhysicalMin}..{v.PhysicalMax} Units=0x{v.Units:X} Exp=0x{v.UnitsExp:X}" +
                    $" Link={v.LinkCollection}(0x{v.LinkUsagePage:X2}/0x{v.LinkUsage:X2})");
            }
        }
        return sb.ToString();
    }

    /// <summary>Feature レポートを読み取り専用で取得する(書き込みは行わない)</summary>
    public string ReadFeatureReports()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Feature 読取: {this}");
        if (Caps.FeatureReportByteLength == 0)
        {
            sb.AppendLine("Feature レポートはありません");
            return sb.ToString();
        }

        var ids = GetValueCaps(HIDP_REPORT_TYPE.Feature).Select(v => v.ReportID)
            .Concat(GetButtonCaps(HIDP_REPORT_TYPE.Feature).Select(b => b.ReportID))
            .Distinct().OrderBy(x => x).ToArray();

        // まず読み書き権限で開き、拒否されたらアクセス権 0 で開き直す
        using var h = OpenDevice(GENERIC_READ | GENERIC_WRITE, sb) ?? OpenDevice(0, sb);
        if (h == null) return sb.ToString();

        foreach (byte id in ids)
        {
            var buf = new byte[Caps.FeatureReportByteLength];
            buf[0] = id;
            if (HidD_GetFeature(h, buf, buf.Length))
                sb.AppendLine($"ReportID={id}: {Convert.ToHexString(buf)}");
            else
                sb.AppendLine($"ReportID={id}: 取得失敗 (Win32Error={Marshal.GetLastWin32Error()})");
        }
        return sb.ToString();
    }

    private Microsoft.Win32.SafeHandles.SafeFileHandle? OpenDevice(uint access, StringBuilder log)
    {
        var h = CreateFile(Path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid)
        {
            log.AppendLine($"CreateFile 成功 (access=0x{access:X8})");
            return h;
        }
        log.AppendLine($"CreateFile 失敗 (access=0x{access:X8}, Win32Error={Marshal.GetLastWin32Error()})");
        h.Dispose();
        return null;
    }

    public override string ToString()
        => $"VID={VendorId:X4} PID={ProductId:X4} Page=0x{UsagePage:X2} Usage=0x{Usage:X2}";

    public void Dispose()
    {
        if (Preparsed != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Preparsed);
            Preparsed = IntPtr.Zero;
        }
    }
}
