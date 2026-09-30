using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static DialDump.NativeMethods;

namespace DialDump;

/// <summary>
/// Raw Input で HID 入力レポートをダンプし、Surface Dial の回転量・押し込みを集計する検証用フォーム。
/// RIDEV_INPUTSINK で登録するため、最小化・非フォアグラウンドでも受信を続ける。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ListView _deviceList = new()
    {
        Dock = DockStyle.Top,
        Height = 170,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9f),
    };

    private readonly Label _stats = new()
    {
        Dock = DockStyle.Top,
        Height = 64,
        Font = new Font("Consolas", 10f),
        Padding = new Padding(4),
    };

    private readonly CheckBox _rawHex = new() { Text = "生データ表示", Checked = true, AutoSize = true };
    private readonly CheckBox _dialOnly = new() { Text = "Dial変化のみ記録", AutoSize = true };
    private readonly CheckBox _csv = new() { Text = "CSV保存", AutoSize = true };

    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 100 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _pending = [];
    private readonly Dictionary<IntPtr, HidDevice> _devices = [];
    private readonly List<RAWINPUTDEVICE> _registered = [];

    private StreamWriter? _csvWriter;

    // 集計値
    private long _reportCount;
    private long _cumulative;
    private long _positiveSum;
    private long _negativeSum;
    private int _minAbsDelta = int.MaxValue;
    private int _maxAbsDelta;
    private long _unitDeltaCount;
    private double _lastReportMs = -1;
    private double _minIntervalMs = double.MaxValue;
    private string _buttonState = "-";

    public MainForm()
    {
        Text = "DialDump - Raw Input HID ダンプ";
        Width = 1100;
        Height = 780;

        _deviceList.Columns.Add("VID", 60);
        _deviceList.Columns.Add("PID", 60);
        _deviceList.Columns.Add("UsagePage", 80);
        _deviceList.Columns.Add("Usage", 60);
        _deviceList.Columns.Add("Dial", 50);
        _deviceList.Columns.Add("Path", 780);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(2) };
        buttons.Controls.Add(MakeButton("再列挙", (_, _) => EnumerateDevices()));
        buttons.Controls.Add(MakeButton("チェックした種類を登録", (_, _) => RegisterChecked()));
        buttons.Controls.Add(MakeButton("登録解除", (_, _) => Unregister()));
        buttons.Controls.Add(MakeButton("Caps表示", (_, _) => ShowCaps()));
        buttons.Controls.Add(MakeButton("Feature読取", (_, _) => ReadFeature()));
        buttons.Controls.Add(MakeButton("集計リセット", (_, _) => ResetStats()));
        buttons.Controls.Add(MakeButton("ログクリア", (_, _) => _log.Clear()));
        buttons.Controls.Add(_rawHex);
        buttons.Controls.Add(_dialOnly);
        buttons.Controls.Add(_csv);
        _csv.CheckedChanged += (_, _) => ToggleCsv();

        // Dock は後から追加したものが先に配置されるため、下から順に追加する
        Controls.Add(_log);
        Controls.Add(_stats);
        Controls.Add(buttons);
        Controls.Add(_deviceList);

        _flushTimer.Tick += (_, _) => FlushLog();
        _flushTimer.Start();

        Load += (_, _) => EnumerateDevices();
        FormClosed += (_, _) =>
        {
            _csvWriter?.Dispose();
            foreach (var d in _devices.Values) d.Dispose();
        };

        UpdateStats();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    // ---------------- デバイス列挙・登録 ----------------

    private void EnumerateDevices()
    {
        foreach (var d in _devices.Values) d.Dispose();
        _devices.Clear();
        _deviceList.Items.Clear();

        foreach (var dev in HidDevice.Enumerate())
        {
            _devices[dev.Handle] = dev;
            var item = new ListViewItem(
            new[]
            {
                dev.VendorId.ToString("X4"),
                dev.ProductId.ToString("X4"),
                $"0x{dev.UsagePage:X2}",
                $"0x{dev.Usage:X2}",
                dev.DialCap.HasValue ? "あり" : "",
                dev.Path,
            })
            {
                Tag = dev,
                // ラジアルコントローラー(0x01/0x0E)は最初からチェックしておく
                Checked = dev.IsRadialController,
            };
            if (dev.IsRadialController) item.BackColor = Color.LightYellow;
            _deviceList.Items.Add(item);
        }
        Log($"HID デバイス {_devices.Count} 件を列挙 (ラジアルコントローラー {_devices.Values.Count(d => d.IsRadialController)} 件)");
    }

    private void RegisterChecked()
    {
        var targets = _deviceList.CheckedItems.Cast<ListViewItem>()
            .Select(i => (HidDevice)i.Tag!)
            .Select(d => (d.UsagePage, d.Usage))
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            Log("登録対象がありません。一覧でチェックしてください。");
            return;
        }

        Unregister();
        var rids = targets.Select(t => new RAWINPUTDEVICE
        {
            usUsagePage = t.UsagePage,
            usUsage = t.Usage,
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            hwndTarget = Handle,
        }).ToArray();

        if (RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _registered.AddRange(rids);
            Log("登録: " + string.Join(", ", targets.Select(t => $"0x{t.UsagePage:X2}/0x{t.Usage:X2}")) + " (INPUTSINK: バックグラウンドでも受信)");
        }
        else
        {
            Log($"RegisterRawInputDevices 失敗 Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private void Unregister()
    {
        if (_registered.Count == 0) return;
        var rids = _registered.Select(r => new RAWINPUTDEVICE
        {
            usUsagePage = r.usUsagePage,
            usUsage = r.usUsage,
            dwFlags = RIDEV_REMOVE,
            hwndTarget = IntPtr.Zero,
        }).ToArray();
        if (!RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log($"登録解除失敗 Win32Error={Marshal.GetLastWin32Error()}");
        else
            Log("登録を解除しました");
        _registered.Clear();
    }

    private HidDevice? SelectedDevice()
    {
        if (_deviceList.SelectedItems.Count > 0) return (HidDevice)_deviceList.SelectedItems[0].Tag!;
        Log("一覧で対象デバイスを選択(行をクリック)してください。");
        return null;
    }

    private void ShowCaps()
    {
        if (SelectedDevice() is { } dev) Log(dev.DescribeCaps());
    }

    private void ReadFeature()
    {
        if (SelectedDevice() is { } dev) Log(dev.ReadFeatureReports());
    }

    // ---------------- WM_INPUT 処理 ----------------

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_INPUT:
                HandleRawInput(m.LParam);
                break;
            case WM_INPUT_DEVICE_CHANGE:
                // wParam: 1=GIDC_ARRIVAL, 2=GIDC_REMOVAL
                Log($"デバイス{(m.WParam == 1 ? "接続" : "切断")} handle=0x{m.LParam:X}");
                break;
        }
        base.WndProc(ref m);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return;

            // RAWHID: DWORD dwSizeHid, DWORD dwCount, BYTE bRawData[dwSizeHid * dwCount]
            int sizeHid = Marshal.ReadInt32(buffer, (int)headerSize);
            int count = Marshal.ReadInt32(buffer, (int)headerSize + 4);
            IntPtr data = buffer + (int)headerSize + 8;

            var dev = GetOrCreateDevice(header.hDevice);
            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(data + i * sizeHid, report, 0, sizeHid);
                ProcessReport(dev, report);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private HidDevice? GetOrCreateDevice(IntPtr hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var dev)) return dev;
        dev = HidDevice.TryCreate(hDevice);
        if (dev != null)
        {
            _devices[hDevice] = dev;
            Log($"未列挙のデバイスから受信: {dev}");
        }
        return dev;
    }

    private void ProcessReport(HidDevice? dev, byte[] report)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double interval = _lastReportMs < 0 ? 0 : now - _lastReportMs;
        _lastReportMs = now;

        int? dial = dev?.ReadDial(report);
        ushort[] pressed = dev?.ReadButtons(report) ?? Array.Empty<ushort>();
        if (dev?.DialCap is { } dialCap && report.Length > 0 && report[0] == dialCap.ReportID)
            _buttonState = pressed.Length > 0 ? "押下" : "離す";

        _reportCount++;
        if (interval > 0 && interval < _minIntervalMs) _minIntervalMs = interval;
        if (dial is int d && d != 0)
        {
            _cumulative += d;
            if (d > 0) _positiveSum += d; else _negativeSum += d;
            int abs = Math.Abs(d);
            if (abs < _minAbsDelta) _minAbsDelta = abs;
            if (abs > _maxAbsDelta) _maxAbsDelta = abs;
            if (abs == 1) _unitDeltaCount++;
        }

        if (_dialOnly.Checked && (dial ?? 0) == 0) return;

        string buttons = pressed.Length > 0 ? string.Join("|", pressed.Select(u => $"B{u}")) : "-";
        string hex = Convert.ToHexString(report);
        var line = new StringBuilder();
        line.Append($"{now,10:F1}ms +{interval,6:F1}ms  dial={(dial?.ToString() ?? "n/a"),6}  btn={buttons,-6} sum={_cumulative,8}");
        if (_rawHex.Checked) line.Append($"  raw={hex}");
        if (dev == null) line.Append("  (デバイス情報なし)");
        Log(line.ToString());

        _csvWriter?.WriteLine(string.Join(",",
            now.ToString("F3"), interval.ToString("F3"),
            dev != null ? $"{dev.VendorId:X4}:{dev.ProductId:X4}" : "",
            dial?.ToString() ?? "", buttons, _cumulative, hex));
    }

    // ---------------- 集計・ログ ----------------

    private void ResetStats()
    {
        _reportCount = 0;
        _cumulative = 0;
        _positiveSum = 0;
        _negativeSum = 0;
        _minAbsDelta = int.MaxValue;
        _maxAbsDelta = 0;
        _unitDeltaCount = 0;
        _minIntervalMs = double.MaxValue;
        _lastReportMs = -1;
        Log("---- 集計リセット ----");
        UpdateStats();
    }

    private void UpdateStats()
    {
        string minAbs = _minAbsDelta == int.MaxValue ? "-" : _minAbsDelta.ToString();
        string minInt = _minIntervalMs == double.MaxValue ? "-" : $"{_minIntervalMs:F1}ms";
        _stats.Text =
            $"レポート数={_reportCount}  累積={_cumulative} (正 {_positiveSum} / 負 {_negativeSum})  " +
            $"3600カウント/回転と仮定すると {_cumulative / 3600.0:F3} 回転\r\n" +
            $"|Δ|最小={minAbs}  |Δ|最大={_maxAbsDelta}  Δ=±1の回数={_unitDeltaCount}  最短レポート間隔={minInt}  ボタン={_buttonState}";
    }

    private void Log(string text)
    {
        _pending.Add(text);
    }

    private void FlushLog()
    {
        UpdateStats();
        if (_pending.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var s in _pending) sb.Append(s).Append("\r\n");
        _pending.Clear();

        // ログが大きくなりすぎたら古い側を捨てる
        if (_log.TextLength > 2_000_000) _log.Text = _log.Text[^500_000..];
        _log.AppendText(sb.ToString());
        _csvWriter?.Flush();
    }

    private void ToggleCsv()
    {
        if (_csv.Checked)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = Path.Combine(dir, $"DialDump_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _csvWriter = new StreamWriter(path, false, new UTF8Encoding(true));
            _csvWriter.WriteLine("time_ms,interval_ms,device,dial,buttons,cumulative,raw_hex");
            Log($"CSV 保存開始: {path}");
        }
        else if (_csvWriter != null)
        {
            _csvWriter.Dispose();
            _csvWriter = null;
            Log("CSV 保存終了");
        }
    }
}
