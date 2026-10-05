using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace CfdTcpAccess
{
    public sealed partial class MainForm
    {
        private const string DefaultLocalHost = "localhost";

        private AppSettings _settings = new AppSettings();
        private readonly BindingList<MappingRow> _rows = new BindingList<MappingRow>();
        private bool _closing;

        public MainForm()
        {
            _settings = AppSettings.Load(out _);
            InitializeComponent();
            WireEvents();
            ApplySettings();

            AppendLog(null, "就绪。①选择 cloudflared 程序 → ②在映射表里填「源域名 / 本地端口」→ ③点“启动全部”。", false);

            AutoLocateCloudflared();
        }

    // ================== 事件绑定 ==================

    private void WireEvents()
    {
        _btnBrowse.Click += (_, _) => BrowseCloudflared();
        _txCloudflared.Leave += (_, _) => RefreshVersion();
        _txCloudflared.TextChanged += (_, _) => _lblVersion.Text = "版本：未检测";
        _txCloudflared.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                e.Effect = DragDropEffects.Copy;
            }
        };
        _txCloudflared.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _txCloudflared.Text = files[0];
                RefreshVersion();
            }
        };

        // --- 表格 ---
        _dgv.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_dgv.IsCurrentCellDirty && _dgv.CurrentCell is DataGridViewCheckBoxCell)
            {
                _dgv.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _dgv.CellEndEdit += OnCellEndEdit;
        _dgv.CellValidating += OnCellValidating;
        _dgv.CellFormatting += OnCellFormatting;
        _dgv.DataError += (_, e) =>
        {
            e.ThrowException = false;
            e.Cancel = true;
            if (e.RowIndex >= 0 && e.ColumnIndex == _colPort.Index)
            {
                var fallback = SuggestFreePort();
                _rows[e.RowIndex].Port = fallback;
                AppendLog(_rows[e.RowIndex], $"端口值无效，已自动改为 {fallback}。", true);
            }
        };
        _dgv.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedRows();
                e.Handled = true;
            }
        };

        // --- 右键菜单 ---
        _gridMenu.Items.Add("启动该行", null, (_, _) => StartRow(CurrentRow()));
        _gridMenu.Items.Add("停止该行", null, (_, _) => StopRow(CurrentRow()));
        _gridMenu.Items.Add(new ToolStripSeparator());
        _gridMenu.Items.Add("复制本地地址", null, (_, _) => CopyRowAddress(CurrentRow()));
        _gridMenu.Items.Add(new ToolStripSeparator());
        _gridMenu.Items.Add("删除该行", null, (_, _) => RemoveSelectedRows());
        _dgv.ContextMenuStrip = _gridMenu;
        _gridMenu.Opening += (_, e) => e.Cancel = _dgv.CurrentRow is null;

        // --- 操作 ---
        _btnStartAll.Click += (_, _) => StartAll();
        _btnStopAll.Click += (_, _) => StopAll();

        _btnClearLog.Click += (_, _) => _rtbLog.Clear();
        _btnSaveLog.Click += (_, _) => SaveLog();

        FormClosing += OnFormClosing;
    }

    private void ApplySettings()
    {
        _txCloudflared.Text = _settings.CloudflaredPath;

        _rows.Clear();
        var configs = _settings.Mappings.Count > 0
            ? _settings.Mappings
            : new List<MappingConfig> { new() };

        foreach (var config in configs)
        {
            _rows.Add(MappingRow.FromConfig(config));
        }

        if (_rows.Count > 0)
        {
            SelectRow(_rows[0]);
        }
    }

    /// <summary>
    /// 选中某个映射行。DataGridView 的行是绑定后才生成的，构造函数阶段可能还没有行，
    /// 所以这里必须按 DataBoundItem 查找而不是直接索引 Rows[0]。
    /// </summary>
    private void SelectRow(MappingRow? row)
    {
        if (row is null || _dgv.Rows.Count == 0)
        {
            return;
        }

        foreach (DataGridViewRow gridRow in _dgv.Rows)
        {
            if (ReferenceEquals(gridRow.DataBoundItem, row))
            {
                _dgv.CurrentCell = gridRow.Cells[_colHost.Index];
                return;
            }
        }
    }

    private void CaptureSettings()
    {
        _settings.CloudflaredPath = _txCloudflared.Text.Trim();
        _settings.Mappings = _rows.Select(r => r.ToConfig()).ToList();
    }

    /// <summary>把当前配置导出成一个独立的 json 文件（默认 settings.json 只是本程序自己用的状态文件）。</summary>
    private void ExportSettings()
    {
        CaptureSettings();

        using var dlg = new SaveFileDialog
        {
            Title = "导出配置到文件",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "cfd-tcp-access-config.json",
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, _settings.ToJson());
            AppendLog(null, $"已导出 {_settings.Mappings.Count} 条映射配置到 {dlg.FileName}", false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出配置失败：\n" + ex.Message, "导出失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>从 json 文件导入配置并立即应用到界面与磁盘。</summary>
    private void ImportSettings()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "从文件导入配置",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        AppSettings loaded;
        try
        {
            loaded = AppSettings.FromJson(File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入配置失败（不是有效的配置文件？）：\n" + ex.Message, "导入失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var running = _rows.Count(r => r.IsRunning);
        if (running > 0 && MessageBox.Show(this,
                $"当前有 {running} 个隧道在运行，导入配置会先停止它们。继续吗？",
                "确认导入", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        StopAll();

        _settings = loaded;
        ApplySettings();
        UpdateOverallStatus();

        try
        {
            _settings.Save();
        }
        catch
        {
            // 落盘失败不影响导入结果。
        }

        AppendLog(null, $"已导入配置：{dlg.FileName}（{_settings.Mappings.Count} 条映射，" +
                        $"cloudflared：{(string.IsNullOrWhiteSpace(_settings.CloudflaredPath) ? "未设置" : _settings.CloudflaredPath)}）", false);
    }

    // ================== 表格辅助 ==================

    private MappingRow? RowAt(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index] : null;

    private MappingRow? CurrentRow() => _dgv.CurrentRow?.DataBoundItem as MappingRow;

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        var row = _rows[e.RowIndex];

        if (e.ColumnIndex == _colHost.Index)
        {
            var normalized = HostValidator.Normalize(row.Hostname);
            if (normalized != row.Hostname)
            {
                row.Hostname = normalized;
            }

            if (normalized.Length > 0 && !HostValidator.IsValidHostname(normalized))
            {
                AppendLog(row, $"“{normalized}”不是标准域名格式，请确认域名拼写与 DNS 记录。", true);
            }
        }
        else if (e.ColumnIndex == _colPort.Index && (row.Port < 1 || row.Port > 65535))
        {
            var fallback = SuggestFreePort();
            row.Port = fallback;
            AppendLog(row, $"端口超出范围，已自动改为 {fallback}。", true);
        }
    }

    private void OnCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _colPort.Index)
        {
            return;
        }

        var text = Convert.ToString(e.FormattedValue)?.Trim() ?? "";
        if (int.TryParse(text, out var port) && port is >= 1 and <= 65535)
        {
            _dgv.Rows[e.RowIndex].ErrorText = "";
            return;
        }

        var fallback = SuggestFreePort();
        _dgv.Rows[e.RowIndex].ErrorText = "";
        e.Cancel = true;
        BeginInvoke(() =>
        {
            if (e.RowIndex < _rows.Count)
            {
                _rows[e.RowIndex].Port = fallback;
                AppendLog(_rows[e.RowIndex], $"端口“{text}”无效，已自动改为 {fallback}。", true);
            }
        });
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != _colStatus.Index || e.Value is not string text)
        {
            return;
        }

        if (text.StartsWith("已就绪", StringComparison.Ordinal))
        {
            e.CellStyle!.ForeColor = Color.FromArgb(0, 130, 0);
            e.CellStyle.Font = new Font(_dgv.Font, FontStyle.Bold);
        }
        else if (text.StartsWith("失败", StringComparison.Ordinal))
        {
            e.CellStyle!.ForeColor = Color.FromArgb(192, 0, 0);
        }
        else if (text.StartsWith("启动中", StringComparison.Ordinal))
        {
            e.CellStyle!.ForeColor = Color.FromArgb(200, 120, 0);
        }
        else
        {
            e.CellStyle!.ForeColor = Color.DimGray;
        }
    }

    private MappingRow AddRow(int? port = null)
    {
        var row = new MappingRow { Port = port ?? SuggestFreePort() };
        _rows.Add(row);
        SelectRow(row);
        return row;
    }

    private void RemoveSelectedRows()
    {
        var targets = _dgv.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as MappingRow)
            .Where(r => r is not null)
            .Cast<MappingRow>()
            .ToList();

        if (targets.Count == 0 && CurrentRow() is { } current)
        {
            targets.Add(current);
        }

        foreach (var row in targets)
        {
            StopRow(row);
            _rows.Remove(row);
        }

        UpdateOverallStatus();
    }

    private void ClearRows()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "确定要清空映射表吗？", "清空列表",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        StopAll();
        _rows.Clear();
        UpdateOverallStatus();
    }

    private int SuggestFreePort()
    {
        var used = _rows.Select(r => r.Port).ToHashSet();
        var port = PortPlanner.DefaultStartPort;
        while (port <= 65535)
        {
            if (!used.Contains(port) && IsPortAvailable(port))
            {
                return port;
            }

            port++;
        }

        return PortPlanner.DefaultStartPort;
    }

    private bool IsPortAvailable(int port) => IsPortAvailable(DefaultLocalHost, port);

    private static bool IsPortAvailable(string host, int port)
    {
        var effective = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host;
        if (!IPAddress.TryParse(effective, out var address))
        {
            return true;
        }

        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================== 1. cloudflared ==================

    private void BrowseCloudflared()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 cloudflared 可执行文件",
            Filter = "cloudflared (cloudflared*.exe)|cloudflared*.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        var current = _txCloudflared.Text.Trim();
        if (File.Exists(current))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(current);
            dlg.FileName = Path.GetFileName(current);
        }

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txCloudflared.Text = dlg.FileName;
            RefreshVersion();
        }
    }

    /// <summary>
    /// 开箱即用：启动时静默定位 cloudflared（配置里记录的路径 → 程序同目录 → 当前目录 → PATH → 常见安装位置），
    /// 找到就自动填好，找不到就留空等用户点“浏览…”。没有单独的“自动检测”按钮。
    /// </summary>
    private void AutoLocateCloudflared()
    {
        var found = CloudflaredLocator.Find(_txCloudflared.Text.Trim());
        if (found is null)
        {
            _lblVersion.Text = "版本：未找到 cloudflared，请点“浏览…”选择";
            _lblVersion.ForeColor = Color.FromArgb(192, 0, 0);
            return;
        }

        if (!string.Equals(found, _txCloudflared.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _txCloudflared.Text = found;
        }

        RefreshVersion(verbose: true);
    }

    private void RefreshVersion(bool verbose = false)
    {
        var path = _txCloudflared.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            _lblVersion.Text = "版本：未选择程序";
            _lblVersion.ForeColor = Color.DimGray;
            return;
        }

        if (!File.Exists(path))
        {
            _lblVersion.Text = "版本：路径不存在";
            _lblVersion.ForeColor = Color.FromArgb(192, 0, 0);
            return;
        }

        var version = CloudflaredLocator.GetVersion(path);
        var ok = version.StartsWith("cloudflared version", StringComparison.OrdinalIgnoreCase);
        _lblVersion.Text = (ok ? "版本：" : "版本读取异常：") + version;
        _lblVersion.ForeColor = ok ? Color.FromArgb(0, 120, 0) : Color.FromArgb(192, 0, 0);

        if (verbose)
        {
            AppendLog(null, version, !ok);
        }
    }

    // ================== 启动 / 停止 ==================

    private TunnelOptions BuildOptions(MappingRow row)
    {
        var exe = _txCloudflared.Text.Trim().Trim('"');
        if (exe.Length == 0)
        {
            exe = "cloudflared.exe";
        }

        return new TunnelOptions
        {
            CloudflaredPath = exe,
            Hostname = HostValidator.Normalize(row.Hostname),
            LocalHost = DefaultLocalHost,
            LocalPort = row.Port,
        };
    }

    private void StartAll()
    {
        var exe = _txCloudflared.Text.Trim().Trim('"');
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, "请先选择有效的 cloudflared 程序（第 1 步）。", "缺少 cloudflared",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var started = 0;
        var skipped = 0;
        var duplicates = new HashSet<int>();

        foreach (var row in _rows)
        {
            if (!row.Enabled || row.IsRunning)
            {
                continue;
            }

            var hostname = HostValidator.Normalize(row.Hostname);
            if (hostname.Length == 0)
            {
                row.Status = "跳过：未填写源域名";
                skipped++;
                continue;
            }

            if (!duplicates.Add(row.Port))
            {
                row.Status = $"跳过：端口 {row.Port} 与上面的行重复";
                AppendLog(row, $"本地端口 {row.Port} 重复，已跳过。请修改上面一行的端口。", true);
                skipped++;
                continue;
            }

            if (!IsPortAvailable(row.Port))
            {
                row.Status = $"跳过：端口 {row.Port} 已被占用";
                AppendLog(row, $"本地端口 {row.Port} 已被其它程序占用，已跳过。", true);
                skipped++;
                continue;
            }

            StartRow(row);
            started++;
        }

        if (started == 0)
        {
            AppendLog(null, skipped > 0 ? "没有可启动的映射行（请看各行状态）。" : "没有需要启动的映射行。", skipped > 0);
        }

        UpdateOverallStatus();
    }

    private void StartRow(MappingRow? row)
    {
        if (row is null || row.IsRunning)
        {
            return;
        }

        var exe = _txCloudflared.Text.Trim().Trim('"');
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, "请先选择有效的 cloudflared 程序（第 1 步）。", "缺少 cloudflared",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var hostname = HostValidator.Normalize(row.Hostname);
        if (hostname.Length == 0)
        {
            row.Status = "跳过：未填写源域名";
            AppendLog(row, "未填写源域名，无法启动。", true);
            return;
        }

        row.Hostname = hostname;
        var options = BuildOptions(row);

        if (!HostValidator.IsValidHostname(hostname))
        {
            AppendLog(row, $"“{hostname}”不是标准域名格式，仍尝试启动。", true);
        }

        var runner = new CloudflaredRunner();
        row.Runner = runner;
        row.Status = "启动中…";

        runner.LogReceived += (_, e) => Ui(() => AppendLog(row, e.Text, e.IsError));
        runner.StateChanged += (_, e) => Ui(() =>
        {
            if (!ReferenceEquals(row.Runner, runner))
            {
                return;
            }

            switch (e.State)
            {
                case TunnelState.Starting:
                    row.Status = "启动中…";
                    break;

                case TunnelState.Listening:
                    row.Status = $"已就绪  {e.Endpoint ?? row.LocalAddress}";
                    break;

                case TunnelState.Stopped:
                    row.Status = "已停止";
                    break;

                case TunnelState.Failed:
                    row.Status = "失败：" + e.Message;
                    break;
            }

            if (!string.IsNullOrWhiteSpace(e.Hint))
            {
                _lblHint.Text = $"[{hostname}] {e.Hint}";
                _lblHint.Visible = true;
                AppendLog(row, "排错提示：" + e.Hint, true);
            }

            UpdateOverallStatus();
        });

        try
        {
            runner.Start(options);
        }
        catch (Exception ex)
        {
            row.Status = "启动失败：" + ex.Message;
            row.Runner = null;
            try
            {
                runner.Dispose();
            }
            catch
            {
                // ignore
            }

            AppendLog(row, "启动失败：" + ex.Message, true);
        }
    }

    private void StopRow(MappingRow? row)
    {
        if (row is null)
        {
            return;
        }

        var runner = row.Runner;
        if (runner is null)
        {
            if (row.Status.StartsWith("已就绪", StringComparison.Ordinal))
            {
                row.Status = "未启动";
            }

            return;
        }

        row.Runner = null;
        try
        {
            runner.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            runner.Dispose();
        }
        catch
        {
            // ignore
        }

        row.Status = "已停止";
    }

    private void StopAll()
    {
        foreach (var row in _rows)
        {
            StopRow(row);
        }

        UpdateOverallStatus();
    }

    private void UpdateOverallStatus()
    {
        var enabled = _rows.Count(r => r.Enabled);
        var ready = _rows.Count(r => r.Status.StartsWith("已就绪", StringComparison.Ordinal));
        var starting = _rows.Count(r => r.Status.StartsWith("启动中", StringComparison.Ordinal));
        var failed = _rows.Count(r => r.Status.StartsWith("失败", StringComparison.Ordinal));

        _lblState.Text = $"状态：映射 {_rows.Count} 条（启用 {enabled}） · 已就绪 {ready} · 启动中 {starting} · 失败 {failed}";
        _lblState.ForeColor = failed > 0
            ? Color.FromArgb(192, 0, 0)
            : ready > 0 ? Color.FromArgb(0, 130, 0) : Color.DimGray;

        _progress.Visible = starting > 0;

        if (failed == 0)
        {
            _lblHint.Visible = false;
        }
    }

    // ================== 本地地址 ==================

    private void CopyRowAddress(MappingRow? row)
    {
        if (row is null)
        {
            return;
        }

        var text = row.LocalAddress;
        try
        {
            Clipboard.SetText(text);
            AppendLog(row, "已复制本地地址：" + text, false);
        }
        catch (Exception ex)
        {
            AppendLog(row, "复制失败：" + ex.Message, true);
        }
    }

    // ================== 日志 ==================

    private void AppendLog(MappingRow? row, string text, bool isError)
    {
        if (_closing)
        {
            return;
        }

        if (_rtbLog.TextLength > 400_000)
        {
            _rtbLog.Clear();
            _rtbLog.SelectionColor = Color.DimGray;
            _rtbLog.AppendText("（日志过长，已自动清空旧内容）" + Environment.NewLine);
        }

        var prefix = row is null
            ? "[全局]"
            : "[" + (string.IsNullOrWhiteSpace(row.Hostname) ? row.LocalAddress : row.Hostname) + "]";

        _rtbLog.SelectionStart = _rtbLog.TextLength;
        _rtbLog.SelectionLength = 0;
        _rtbLog.SelectionColor = Color.Silver;
        _rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
        _rtbLog.SelectionColor = Color.FromArgb(90, 90, 160);
        _rtbLog.AppendText(prefix + " ");
        _rtbLog.SelectionColor = LineColor(text, isError);
        _rtbLog.AppendText(text + Environment.NewLine);
        _rtbLog.SelectionColor = _rtbLog.ForeColor;
        _rtbLog.ScrollToCaret();
    }

    private static Color LineColor(string text, bool isError)
    {
        if (text.StartsWith(">", StringComparison.Ordinal))
        {
            return Color.FromArgb(0, 80, 200);
        }

        if (text.Contains("Start Websocket listener", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(0, 130, 0);
        }

        if (text.Contains("ERR", StringComparison.Ordinal) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("失败", StringComparison.Ordinal) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(192, 0, 0);
        }

        if (text.Contains("WRN", StringComparison.Ordinal) ||
            text.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(200, 120, 0);
        }

        return isError ? Color.FromArgb(70, 70, 70) : Color.FromArgb(40, 40, 40);
    }

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存运行日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"cloudflared-access-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, _rtbLog.Text);
            AppendLog(null, "日志已保存到 " + dlg.FileName, false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存日志失败：\n" + ex.Message, "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Ui(Action action)
    {
        if (_closing || IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // 窗口已关闭。
        }
        catch (InvalidOperationException)
        {
            // 句柄已销毁。
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var running = _rows.Count(r => r.IsRunning);
        if (running > 0)
        {
            var r = MessageBox.Show(this,
                $"还有 {running} 个 cloudflared 进程在运行，退出会同时断开这些隧道。确定要退出吗？",
                "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (!_suppressSave)
        {
            try
            {
                CaptureSettings();
                _settings.Save();
            }
            catch
            {
                // 保存失败不影响退出。
            }
        }

        _closing = true;
        StopAll();
    }

    // ================== 布局自检（供 --uicheck 使用） ==================

    private bool _suppressSave;

    internal void SeedForUiCheck()
    {
        _suppressSave = true;
        _rows.Clear();
        _rows.Add(new MappingRow { Hostname = "tcp.example.com", Port = 5555, Status = "已就绪  localhost:5555" });
        _rows.Add(new MappingRow { Hostname = "ssh.example.com", Port = 5556, Status = "启动中…" });
        _rows.Add(new MappingRow { Hostname = "rdp.example.com", Port = 5557, Status = "失败：cloudflared 已退出（退出码 1）。" });
        _rows.Add(new MappingRow { Hostname = "", Port = 5558, Status = "未启动" });
        SelectRow(_rows[0]);
        UpdateOverallStatus();
    }

    internal string BuildUiDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"form.size={Width}x{Height} client={ClientSize.Width}x{ClientSize.Height}");

        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root is not null)
        {
            var heights = root.GetRowHeights();
            sb.AppendLine($"root.rowHeights={string.Join(",", heights)}");
            sb.AppendLine("root.rowStyles=" + string.Join(",", root.RowStyles.Cast<RowStyle>().Select(s => $"{s.SizeType}:{s.Height}")));
            for (var i = 0; i < heights.Length; i++)
            {
                var child = root.GetControlFromPosition(0, i);
                sb.AppendLine($"  root.row{i}: {child?.GetType().Name} '{Trim(child?.Text ?? "")}' bounds={child?.Bounds} minHeight={child?.MinimumSize.Height}");
            }
        }

        sb.AppendLine($"grid.bounds={_dgv.Bounds} rows={_dgv.Rows.Count} columns={_dgv.Columns.Count}");
        sb.AppendLine($"grid.colWidths=enabled:{_colEnabled.Width} host:{_colHost.Width} port:{_colPort.Width} status:{_colStatus.Width} rowHeader:{_dgv.RowHeadersWidth}");
        sb.AppendLine($"log.bounds={_rtbLog.Bounds}");
        sb.AppendLine($"status.text={_lblState.Text}");
        sb.AppendLine($"status.bounds={_lblState.Bounds} hintVisible={_lblHint.Visible}");
        sb.AppendLine($"startAll.bounds={_btnStartAll.Bounds} stopAll.bounds={_btnStopAll.Bounds}");

        var broken = 0;
        foreach (var control in EnumerateControls(this))
        {
            if (control is Form || control is MenuStrip || control is ToolStrip)
            {
                continue;
            }

            if (control.Visible && control.Width <= 0 && control.Text.Length > 0)
            {
                sb.AppendLine($"ZERO-WIDTH: {control.GetType().Name} '{Trim(control.Text)}'");
                broken++;
            }

            var parent = control.Parent;
            if (parent is not null && control.Visible &&
                control.Right > parent.ClientSize.Width + 2 &&
                parent is not DataGridView)
            {
                sb.AppendLine($"OVERFLOW: {control.GetType().Name} '{Trim(control.Text)}' right={control.Right} parentWidth={parent.ClientSize.Width}");
                broken++;
            }
        }

        sb.AppendLine($"zeroWidthOrOverflowControls={broken}");
        return sb.ToString();
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grand in EnumerateControls(child))
            {
                yield return grand;
            }
        }
    }

    private static string Trim(string text) =>
        text.Length > 30 ? text[..30] + "…" : text;
    }
}
