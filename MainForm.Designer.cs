using System;
using System.Drawing;
using System.Windows.Forms;

namespace CfdTcpAccess
{
    /// <summary>
    /// 界面搭建（与 MainForm.cs 逻辑分离，风格对齐参考项目的 Designer 拆分）。
    /// 全部控件由代码构建，没有使用 WinForms 设计器。
    /// </summary>
    public sealed partial class MainForm : Form
    {
        // ---- 1. cloudflared 程序 ----
        private readonly TextBox _txCloudflared = new TextBox();
        private readonly Button _btnBrowse = new Button();
        private readonly Label _lblVersion = new Label();

    // ---- 2. 映射表 ----
    private readonly DataGridView _dgv = new();
    private readonly DataGridViewCheckBoxColumn _colEnabled = new();
    private readonly DataGridViewTextBoxColumn _colHost = new();
    private readonly DataGridViewTextBoxColumn _colPort = new();
    private readonly DataGridViewTextBoxColumn _colStatus = new();
    private readonly ContextMenuStrip _gridMenu = new();

    // ---- 操作 ----
    private readonly Button _btnStartAll = new();
    private readonly Button _btnStopAll = new();

    // ---- 状态 / 日志 ----
    private readonly Label _lblState = new();
    private readonly Label _lblHint = new();
    private readonly ProgressBar _progress = new();
    private readonly RichTextBox _rtbLog = new();
    private readonly Button _btnClearLog = new();
    private readonly Button _btnSaveLog = new();

    // ================== UI 构建 ==================

    private void InitializeComponent()
    {
        Text = "Cloudflare Tunnel TCP Access 助手 — cloudflared access tcp（批量）";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 760);
        Size = new Size(1100, 900);
        Font = new Font("Microsoft YaHei UI", 9F);

        var menu = new MenuStrip();
        var mFile = new ToolStripMenuItem("文件(&F)");
        mFile.DropDownItems.Add("保存配置到默认位置", null, (_, _) =>
        {
            CaptureSettings();
            _settings.Save();
            AppendLog(null, "配置已保存到 " + AppSettings.PrimarySettingsPath, false);
        });
        mFile.DropDownItems.Add("导出配置到文件…", null, (_, _) => ExportSettings());
        mFile.DropDownItems.Add("从文件导入配置…", null, (_, _) => ImportSettings());
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("退出", null, (_, _) => Close());

        menu.Items.Add(mFile);
        MainMenuStrip = menu;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(10, 8, 10, 10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 1 cloudflared
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));   // 2 映射表
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 操作
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 状态
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));   // 日志

        root.Controls.Add(BuildCloudflaredGroup(), 0, 0);
        root.Controls.Add(BuildMappingGroup(), 0, 1);
        root.Controls.Add(BuildActionPanel(), 0, 2);
        root.Controls.Add(BuildStatusPanel(), 0, 3);
        root.Controls.Add(BuildLogGroup(), 0, 4);

        Controls.Add(root);
        Controls.Add(menu);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 7, 3, 0),
    };

    private GroupBox BuildCloudflaredGroup()
    {
        var grp = new GroupBox
        {
            Text = "1. 选择 cloudflared 可执行文件",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 8),
            Margin = new Padding(0, 0, 0, 6),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));

        _txCloudflared.Dock = DockStyle.Fill;
        _txCloudflared.Margin = new Padding(3, 4, 6, 4);
        _txCloudflared.AllowDrop = true;
        _txCloudflared.PlaceholderText = @"例如 C:\cloudflared\cloudflared.exe（可把 exe 直接拖进来）";

        _btnBrowse.Text = "浏览…";
        _btnBrowse.AutoSize = true;
        _btnBrowse.Margin = new Padding(0, 3, 0, 3);

        _lblVersion.Text = "版本：未检测";
        _lblVersion.AutoSize = true;
        _lblVersion.ForeColor = Color.DimGray;
        _lblVersion.Margin = new Padding(3, 6, 3, 0);

        grid.Controls.Add(MakeLabel("程序路径"), 0, 0);
        grid.Controls.Add(_txCloudflared, 1, 0);
        grid.Controls.Add(_btnBrowse, 2, 0);
        grid.Controls.Add(_lblVersion, 1, 1);
        grid.SetColumnSpan(_lblVersion, 2);

        grp.Controls.Add(grid);
        return grp;
    }

    private GroupBox BuildMappingGroup()
    {
        var grp = new GroupBox
        {
            Text = "2. 域名 → 本地 TCP 端口 映射表",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 4, 10, 6),
            Margin = new Padding(0, 4, 0, 6),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // --- 工具条 ---
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };

        Button MakeButton(string text, EventHandler handler)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            b.Click += handler;
            return b;
        }

        bar.Controls.Add(MakeButton("＋ 添加映射", (_, _) => AddRow()));
        bar.Controls.Add(MakeButton("－ 删除选中", (_, _) => RemoveSelectedRows()));
        bar.Controls.Add(MakeButton("清空列表", (_, _) => ClearRows()));

        // --- 表格 ---
        _dgv.Dock = DockStyle.Fill;
        _dgv.AutoGenerateColumns = false;
        _dgv.AllowUserToAddRows = false;
        _dgv.AllowUserToDeleteRows = false;
        _dgv.AllowUserToResizeRows = false;
        _dgv.RowHeadersVisible = false;
        _dgv.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _dgv.MultiSelect = true;
        _dgv.BackgroundColor = Color.White;
        _dgv.BorderStyle = BorderStyle.FixedSingle;
        _dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _dgv.RowTemplate.Height = 24;

        _colEnabled.HeaderText = "启用";
        _colEnabled.DataPropertyName = nameof(MappingRow.Enabled);
        _colEnabled.Width = 50;
        _colEnabled.SortMode = DataGridViewColumnSortMode.NotSortable;

        _colHost.HeaderText = "① 源域名（--hostname）";
        _colHost.DataPropertyName = nameof(MappingRow.Hostname);
        _colHost.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _colHost.FillWeight = 52;
        _colHost.SortMode = DataGridViewColumnSortMode.NotSortable;

        _colPort.HeaderText = "② 转换后的本地 TCP 端口（--url localhost:端口）";
        _colPort.DataPropertyName = nameof(MappingRow.Port);
        _colPort.Width = 250;
        _colPort.SortMode = DataGridViewColumnSortMode.NotSortable;

        _colStatus.HeaderText = "状态 / 本地地址";
        _colStatus.DataPropertyName = nameof(MappingRow.Status);
        _colStatus.ReadOnly = true;
        _colStatus.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _colStatus.FillWeight = 48;
        _colStatus.SortMode = DataGridViewColumnSortMode.NotSortable;

        _dgv.Columns.AddRange(_colEnabled, _colHost, _colPort, _colStatus);
        _dgv.DataSource = _rows;

        grid.Controls.Add(bar, 0, 0);
        grid.Controls.Add(_dgv, 0, 1);

        grp.Controls.Add(grid);
        return grp;
    }

    private Control BuildActionPanel()
    {
        _btnStartAll.Text = "▶ 启动全部";
        _btnStartAll.Font = new Font(Font, FontStyle.Bold);
        _btnStartAll.AutoSize = true;
        _btnStartAll.Padding = new Padding(10, 4, 10, 4);
        _btnStartAll.Margin = new Padding(0, 0, 6, 0);
        _btnStartAll.BackColor = Color.FromArgb(232, 245, 233);

        _btnStopAll.Text = "■ 停止全部";
        _btnStopAll.AutoSize = true;
        _btnStopAll.Padding = new Padding(10, 4, 10, 4);
        _btnStopAll.Margin = new Padding(0, 0, 12, 0);
        _btnStopAll.BackColor = Color.FromArgb(253, 237, 237);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4),
        };
        bar.Controls.Add(_btnStartAll);
        bar.Controls.Add(_btnStopAll);
        return bar;
    }

    private Control BuildStatusPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 4),
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblState.Text = "状态：未启动";
        _lblState.AutoSize = true;
        _lblState.Font = new Font(Font, FontStyle.Bold);
        _lblState.Margin = new Padding(3, 6, 12, 0);

        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Height = 14;
        _progress.Margin = new Padding(0, 7, 3, 0);

        _lblHint.Text = "";
        _lblHint.AutoSize = true;
        _lblHint.ForeColor = Color.FromArgb(192, 0, 0);
        _lblHint.Visible = false;

        panel.Controls.Add(_lblState, 0, 0);
        panel.Controls.Add(_progress, 1, 0);
        panel.Controls.Add(_lblHint, 0, 1);
        panel.SetColumnSpan(_lblHint, 2);
        return panel;
    }

    private GroupBox BuildLogGroup()
    {
        var grp = new GroupBox
        {
            Text = "3. 运行日志（每个域名前缀标注）",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 4, 10, 8),
            Margin = new Padding(0, 0, 0, 0),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _btnClearLog.Text = "清空日志";
        _btnClearLog.AutoSize = true;
        _btnSaveLog.Text = "保存日志…";
        _btnSaveLog.AutoSize = true;

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        flow.Controls.Add(_btnClearLog);
        flow.Controls.Add(_btnSaveLog);

        _rtbLog.Dock = DockStyle.Fill;
        _rtbLog.ReadOnly = true;
        _rtbLog.BackColor = Color.FromArgb(250, 250, 250);
        _rtbLog.Font = new Font("Consolas", 9F);
        _rtbLog.WordWrap = false;
        _rtbLog.ScrollBars = RichTextBoxScrollBars.Both;
        _rtbLog.DetectUrls = false;
        _rtbLog.HideSelection = false;

        grid.Controls.Add(flow, 0, 0);
        grid.Controls.Add(_rtbLog, 0, 1);

        grp.Controls.Add(grid);
        return grp;
    }

    }
}
