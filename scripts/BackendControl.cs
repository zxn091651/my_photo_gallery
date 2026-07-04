using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BackendControlProgram
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BackendControlForm());
    }
}

internal sealed class BackendControlForm : Form
{
    private const int CornerRadius = 28;
    private readonly string projectRoot;
    private readonly string localStatusUrl = "http://127.0.0.1:8787/api/status";
    private readonly string publicBaseUrl;
    private readonly StatusPill overallPill;
    private readonly StatusRow localRow;
    private readonly StatusRow publicApiRow;
    private readonly StatusRow frontendRow;
    private readonly StatusRow driveRow;
    private readonly TextBox detailBox;
    private readonly GlassButton startButton;
    private readonly GlassButton stopButton;
    private readonly GlassButton checkButton;
    private readonly GlassButton openFrontendButton;
    private bool isBusy;

    public BackendControlForm()
    {
        projectRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        publicBaseUrl = ReadPublicBaseUrl();

        Text = "zxn's Photo Gallery";
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 620);
        Size = new Size(640, 700);
        BackColor = Color.FromArgb(8, 10, 16);
        ForeColor = Color.White;
        DoubleBuffered = true;

        ApplyRoundRegion();

        GlassPanel root = new GlassPanel();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(24);
        Controls.Add(root);

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.ColumnCount = 1;
        layout.RowCount = 5;
        layout.BackColor = Color.Transparent;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.Controls.Add(layout);

        Panel titleBar = new Panel();
        titleBar.Dock = DockStyle.Fill;
        titleBar.BackColor = Color.Transparent;
        titleBar.MouseDown += delegate(object sender, MouseEventArgs e) { BeginDrag(e); };
        layout.Controls.Add(titleBar, 0, 0);

        Label title = new Label();
        title.AutoSize = true;
        title.Text = "zxn's Photo Gallery";
        title.Font = new Font(Font.FontFamily, 21F, FontStyle.Bold);
        title.ForeColor = Color.White;
        title.Location = new Point(2, 0);
        titleBar.Controls.Add(title);

        Label subtitle = new Label();
        subtitle.AutoSize = true;
        subtitle.Text = "Backend Control";
        subtitle.Font = new Font(Font.FontFamily, 9F, FontStyle.Regular);
        subtitle.ForeColor = Color.FromArgb(176, 188, 210);
        subtitle.Location = new Point(5, 40);
        titleBar.Controls.Add(subtitle);

        GlassButton closeButton = new GlassButton();
        closeButton.Text = "×";
        closeButton.Width = 42;
        closeButton.Height = 36;
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Location = new Point(titleBar.Width - 42, 0);
        closeButton.Click += delegate { Close(); };
        titleBar.Controls.Add(closeButton);
        titleBar.Resize += delegate { closeButton.Location = new Point(titleBar.Width - closeButton.Width, 0); };

        overallPill = new StatusPill();
        overallPill.Dock = DockStyle.Fill;
        overallPill.Margin = new Padding(0, 8, 0, 12);
        overallPill.SetState(StatusKind.Working, "正在检查连接", "会同时确认本机后端、公网 API、前端页面和移动硬盘。");
        layout.Controls.Add(overallPill, 0, 1);

        GlassPanel statusPanel = new GlassPanel();
        statusPanel.Dock = DockStyle.Fill;
        statusPanel.Padding = new Padding(16, 14, 16, 14);
        statusPanel.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(statusPanel, 0, 2);

        TableLayoutPanel rows = new TableLayoutPanel();
        rows.Dock = DockStyle.Fill;
        rows.ColumnCount = 1;
        rows.RowCount = 4;
        rows.BackColor = Color.Transparent;
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        statusPanel.Controls.Add(rows);

        localRow = new StatusRow("本机后端", "127.0.0.1:8787/api/status");
        publicApiRow = new StatusRow("公网 API", CombineUrl(publicBaseUrl, "/api/status"));
        frontendRow = new StatusRow("前端页面", publicBaseUrl);
        driveRow = new StatusRow("移动硬盘", "等待本机后端返回硬盘状态");
        rows.Controls.Add(localRow, 0, 0);
        rows.Controls.Add(publicApiRow, 0, 1);
        rows.Controls.Add(frontendRow, 0, 2);
        rows.Controls.Add(driveRow, 0, 3);

        detailBox = new TextBox();
        detailBox.Dock = DockStyle.Fill;
        detailBox.Multiline = true;
        detailBox.ReadOnly = true;
        detailBox.ScrollBars = ScrollBars.Vertical;
        detailBox.BorderStyle = BorderStyle.None;
        detailBox.BackColor = Color.FromArgb(15, 18, 27);
        detailBox.ForeColor = Color.FromArgb(218, 226, 242);
        detailBox.Font = new Font("Consolas", 9.4F);
        detailBox.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(detailBox, 0, 3);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.BackColor = Color.Transparent;
        actions.WrapContents = false;
        actions.FlowDirection = FlowDirection.LeftToRight;
        layout.Controls.Add(actions, 0, 4);

        startButton = CreateActionButton("启动后端", true);
        stopButton = CreateActionButton("停止后端", false);
        checkButton = CreateActionButton("检查连接", false);
        openFrontendButton = CreateActionButton("打开前端", false);
        actions.Controls.Add(startButton);
        actions.Controls.Add(stopButton);
        actions.Controls.Add(checkButton);
        actions.Controls.Add(openFrontendButton);

        startButton.Click += async delegate { await StartBackendAsync(); };
        stopButton.Click += async delegate { await StopBackendAsync(); };
        checkButton.Click += async delegate { await CheckConnectionsAsync(); };
        openFrontendButton.Click += delegate { OpenFrontend(); };
        Shown += async delegate { await CheckConnectionsAsync(); };
        Resize += delegate { ApplyRoundRegion(); };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(14, 17, 26), Color.FromArgb(4, 7, 13), 135F))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        DrawGlow(e.Graphics, new Rectangle(-90, -70, 250, 210), Color.FromArgb(70, 79, 209, 255));
        DrawGlow(e.Graphics, new Rectangle(Width - 220, 78, 260, 240), Color.FromArgb(66, 190, 130, 255));
        DrawGlow(e.Graphics, new Rectangle(120, Height - 210, 300, 220), Color.FromArgb(50, 255, 114, 184));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
        using (Pen pen = new Pen(Color.FromArgb(82, 255, 255, 255), 1F))
        {
            e.Graphics.DrawPath(pen, path);
        }

        base.OnPaint(e);
    }

    private GlassButton CreateActionButton(string text, bool primary)
    {
        GlassButton button = new GlassButton();
        button.Text = text;
        button.Width = 132;
        button.Height = 48;
        button.Margin = new Padding(0, 8, 10, 0);
        button.Primary = primary;
        return button;
    }

    private async Task StartBackendAsync()
    {
        SetBusy(true, "正在启动后端", "启动后会自动检查前后端连接。");
        await RunPowerShellScriptAsync("start-gallery.ps1", "-ProjectRoot " + Quote(projectRoot));
        await Task.Delay(1800);
        await CheckConnectionsAsync();
    }

    private async Task StopBackendAsync()
    {
        SetBusy(true, "正在停止后端", "停止后可以安全尝试弹出移动硬盘。");
        await RunPowerShellScriptAsync("stop-gallery.ps1", string.Empty);
        await Task.Delay(700);
        await CheckConnectionsAsync();
    }

    private async Task CheckConnectionsAsync()
    {
        SetBusy(true, "正在检查连接", "正在请求本机后端、公网 API 和前端页面。");
        ConnectionResult local = await CheckUrlAsync(localStatusUrl, true);
        ConnectionResult publicApi = await CheckUrlAsync(CombineUrl(publicBaseUrl, "/api/status"), true);
        ConnectionResult frontend = await CheckUrlAsync(publicBaseUrl, false);

        localRow.SetState(local.Ok ? StatusKind.Good : StatusKind.Bad, local.Ok ? "正常" : "未连接", local.Message);
        publicApiRow.SetState(publicApi.Ok ? StatusKind.Good : StatusKind.Bad, publicApi.Ok ? "正常" : "未连接", publicApi.Message);
        frontendRow.SetState(frontend.Ok ? StatusKind.Good : StatusKind.Bad, frontend.Ok ? "可访问" : "不可访问", frontend.Message);
        UpdateDriveRow(local);

        if (local.Ok && publicApi.Ok && frontend.Ok)
        {
            overallPill.SetState(StatusKind.Good, "前后端连接成功", "后端、隧道和前端页面都可以访问。");
        }
        else if (local.Ok)
        {
            overallPill.SetState(StatusKind.Warning, "本机后端正常，公网链路未完全连通", "请检查内网穿透客户端或公网域名。");
        }
        else
        {
            overallPill.SetState(StatusKind.Bad, "后端未启动或无法连接", "点击“启动后端”后再检查。");
        }

        detailBox.Text =
            "Local backend : " + localStatusUrl + Environment.NewLine +
            "Public API    : " + CombineUrl(publicBaseUrl, "/api/status") + Environment.NewLine +
            "Frontend      : " + publicBaseUrl + Environment.NewLine + Environment.NewLine +
            "Local result  : " + local.Message + Environment.NewLine +
            "Public result : " + publicApi.Message + Environment.NewLine +
            "Frontend      : " + frontend.Message;

        SetBusy(false, null, null);
    }

    private void UpdateDriveRow(ConnectionResult local)
    {
        if (!local.Ok)
        {
            driveRow.SetState(StatusKind.Warning, "未检查", "需要先连接本机后端。");
            return;
        }

        bool drivePresent = ContainsJsonTrue(local.Body, "drivePresent");
        bool rootPresent = ContainsJsonTrue(local.Body, "mediaRootPresent");
        bool serialMatches = ContainsJsonTrue(local.Body, "diskSerialMatches");

        if (drivePresent && rootPresent && serialMatches)
        {
            driveRow.SetState(StatusKind.Good, "已连接", "移动硬盘已就绪，序列号匹配。");
        }
        else
        {
            driveRow.SetState(StatusKind.Bad, "未就绪", "移动硬盘未连接、影像备份不存在或序列号不匹配。");
        }
    }

    private async Task RunPowerShellScriptAsync(string scriptName, string extraArguments)
    {
        string scriptPath = Path.Combine(projectRoot, "scripts", scriptName);
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show("找不到脚本：" + scriptPath, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File " + Quote(scriptPath);
        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            arguments += " " + extraArguments;
        }

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = "powershell.exe";
        startInfo.Arguments = arguments;
        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.WorkingDirectory = projectRoot;

        await Task.Run(delegate
        {
            using (Process process = Process.Start(startInfo))
            {
                if (process != null)
                {
                    process.WaitForExit();
                }
            }
        });
    }

    private async Task<ConnectionResult> CheckUrlAsync(string url, bool requireOkJson)
    {
        return await Task.Run(delegate
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.UserAgent = "zxn-photo-gallery-control";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    bool httpOk = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                    bool bodyOk = !requireOkJson || ContainsJsonTrue(body, "ok");
                    string message = ((int)response.StatusCode).ToString() + " " + response.StatusDescription;
                    if (body.Length > 0)
                    {
                        message += " | " + Shorten(body.Replace("\r", " ").Replace("\n", " "), 220);
                    }

                    return new ConnectionResult(httpOk && bodyOk, message, body);
                }
            }
            catch (Exception ex)
            {
                return new ConnectionResult(false, ex.Message, string.Empty);
            }
        });
    }

    private string ReadPublicBaseUrl()
    {
        string fallback = "http://photo.fucku.top";
        string configPath = Path.Combine(projectRoot, "web", "config.js");
        if (!File.Exists(configPath))
        {
            return fallback;
        }

        string config = File.ReadAllText(configPath, Encoding.UTF8);
        Match match = Regex.Match(config, "apiBase\\s*:\\s*[\"']([^\"']+)[\"']");
        return match.Success ? match.Groups[1].Value.Trim().TrimEnd('/') : fallback;
    }

    private void OpenFrontend()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = publicBaseUrl;
        startInfo.UseShellExecute = true;
        Process.Start(startInfo);
    }

    private void SetBusy(bool busy, string title, string detail)
    {
        isBusy = busy;
        startButton.Enabled = !busy;
        stopButton.Enabled = !busy;
        checkButton.Enabled = !busy;
        openFrontendButton.Enabled = !busy;
        if (!string.IsNullOrEmpty(title))
        {
            overallPill.SetState(StatusKind.Working, title, detail ?? string.Empty);
        }
    }

    private void BeginDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || isBusy)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    private void ApplyRoundRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius))
        {
            Region = new Region(path);
        }
    }

    private static void DrawGlow(Graphics graphics, Rectangle bounds, Color color)
    {
        using (GraphicsPath path = new GraphicsPath())
        {
            path.AddEllipse(bounds);
            using (PathGradientBrush brush = new PathGradientBrush(path))
            {
                brush.CenterColor = color;
                brush.SurroundColors = new Color[] { Color.FromArgb(0, color) };
                graphics.FillPath(brush, path);
            }
        }
    }

    internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool ContainsJsonTrue(string body, string propertyName)
    {
        return Regex.IsMatch(body, "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*true", RegexOptions.IgnoreCase);
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string Shorten(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

internal enum StatusKind
{
    Good,
    Warning,
    Bad,
    Working
}

internal sealed class ConnectionResult
{
    public readonly bool Ok;
    public readonly string Message;
    public readonly string Body;

    public ConnectionResult(bool ok, string message, string body)
    {
        Ok = ok;
        Message = message;
        Body = body;
    }
}

internal sealed class GlassPanel : Panel
{
    public GlassPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 24))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(76, 255, 255, 255), Color.FromArgb(24, 255, 255, 255), 135F))
        using (Pen border = new Pen(Color.FromArgb(72, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        base.OnPaint(e);
    }
}

internal sealed class StatusPill : Control
{
    private StatusKind kind = StatusKind.Working;
    private string title = "";
    private string detail = "";

    public StatusPill()
    {
        DoubleBuffered = true;
        Height = 82;
    }

    public void SetState(StatusKind statusKind, string titleText, string detailText)
    {
        kind = statusKind;
        title = titleText;
        detail = detailText;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color accent = Palette(kind);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 24))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(78, accent), Color.FromArgb(32, 255, 255, 255), 0F))
        using (Pen border = new Pen(Color.FromArgb(95, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        using (SolidBrush dot = new SolidBrush(accent))
        {
            e.Graphics.FillEllipse(dot, 20, 27, 18, 18);
        }

        using (SolidBrush textBrush = new SolidBrush(Color.White))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(198, 216, 232, 248)))
        {
            e.Graphics.DrawString(title, new Font(Font.FontFamily, 12.8F, FontStyle.Bold), textBrush, 50, 18);
            e.Graphics.DrawString(detail, new Font(Font.FontFamily, 9.2F, FontStyle.Regular), detailBrush, 51, 45);
        }
    }

    internal static Color Palette(StatusKind kind)
    {
        if (kind == StatusKind.Good) return Color.FromArgb(74, 222, 128);
        if (kind == StatusKind.Warning) return Color.FromArgb(250, 204, 21);
        if (kind == StatusKind.Bad) return Color.FromArgb(248, 113, 113);
        return Color.FromArgb(125, 211, 252);
    }
}

internal sealed class StatusRow : Control
{
    private readonly string name;
    private string state = "未检查";
    private string detail;
    private StatusKind kind = StatusKind.Warning;

    public StatusRow(string rowName, string rowDetail)
    {
        name = rowName;
        detail = rowDetail;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 10);
    }

    public void SetState(StatusKind statusKind, string stateText, string detailText)
    {
        kind = statusKind;
        state = stateText;
        detail = detailText;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 18))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
        {
            e.Graphics.FillPath(brush, path);
        }

        Color accent = StatusPill.Palette(kind);
        using (SolidBrush dot = new SolidBrush(accent))
        using (SolidBrush nameBrush = new SolidBrush(Color.White))
        using (SolidBrush stateBrush = new SolidBrush(accent))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(175, 210, 221, 238)))
        {
            e.Graphics.FillEllipse(dot, 16, Height / 2 - 6, 12, 12);
            e.Graphics.DrawString(name, new Font(Font.FontFamily, 10.5F, FontStyle.Bold), nameBrush, 40, 10);
            SizeF stateSize = e.Graphics.MeasureString(state, Font);
            e.Graphics.DrawString(state, new Font(Font.FontFamily, 10F, FontStyle.Bold), stateBrush, Width - stateSize.Width - 22, 10);
            e.Graphics.DrawString(TrimToWidth(e.Graphics, detail, Width - 62), new Font(Font.FontFamily, 8.5F), detailBrush, 40, 36);
        }
    }

    private string TrimToWidth(Graphics graphics, string value, int maxWidth)
    {
        if (graphics.MeasureString(value, Font).Width <= maxWidth) return value;
        string ellipsis = "...";
        for (int length = Math.Max(0, value.Length - 1); length > 0; length--)
        {
            string candidate = value.Substring(0, length) + ellipsis;
            if (graphics.MeasureString(candidate, Font).Width <= maxWidth) return candidate;
        }
        return ellipsis;
    }
}

internal sealed class GlassButton : Button
{
    public bool Primary;

    public GlassButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9.6F, FontStyle.Bold);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color top = Primary ? Color.FromArgb(210, 125, 211, 252) : Color.FromArgb(82, 255, 255, 255);
        Color bottom = Primary ? Color.FromArgb(190, 192, 132, 252) : Color.FromArgb(36, 255, 255, 255);

        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 16))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, top, bottom, 90F))
        using (Pen border = new Pen(Color.FromArgb(95, 255, 255, 255), 1F))
        {
            pevent.Graphics.FillPath(brush, path);
            pevent.Graphics.DrawPath(border, path);
        }

        TextRenderer.DrawText(pevent.Graphics, Text, Font, rect, Primary ? Color.FromArgb(5, 13, 24) : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
