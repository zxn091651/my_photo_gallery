using System;
using System.Collections.Generic;
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
    private const int CornerRadius = 30;
    private readonly string projectRoot;
    private readonly GalleryConfig config;
    private readonly string localStatusUrl;
    private readonly StatusPill overallPill;
    private readonly StatusCard driveCard;
    private readonly StatusCard backendCard;
    private readonly GlassButton startButton;
    private readonly GlassButton stopButton;
    private readonly GlassButton checkButton;
    private bool isBusy;

    public BackendControlForm()
    {
        projectRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        config = GalleryConfig.Load(projectRoot);
        localStatusUrl = "http://127.0.0.1:" + config.Port + "/api/status";

        Text = "zxn's Photo Gallery";
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 430);
        Size = new Size(560, 460);
        BackColor = Color.FromArgb(8, 10, 16);
        ForeColor = Color.White;
        DoubleBuffered = true;
        ApplyRoundRegion();

        GlassFrame root = new GlassFrame();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(24);
        Controls.Add(root);

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.BackColor = Color.Transparent;
        layout.ColumnCount = 1;
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.Controls.Add(layout);

        Panel titleBar = new Panel();
        titleBar.Dock = DockStyle.Fill;
        titleBar.BackColor = Color.Transparent;
        titleBar.MouseDown += delegate(object sender, MouseEventArgs e) { BeginDrag(e); };
        layout.Controls.Add(titleBar, 0, 0);

        Label title = new Label();
        title.AutoSize = true;
        title.Text = "zxn's Photo Gallery";
        title.Font = new Font(Font.FontFamily, 20F, FontStyle.Bold);
        title.ForeColor = Color.White;
        title.Location = new Point(2, 2);
        titleBar.Controls.Add(title);

        Label subtitle = new Label();
        subtitle.AutoSize = true;
        subtitle.Text = "后端控制器";
        subtitle.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
        subtitle.ForeColor = Color.FromArgb(172, 188, 210);
        subtitle.Location = new Point(5, 42);
        titleBar.Controls.Add(subtitle);

        GlassButton closeButton = new GlassButton("X", false);
        closeButton.Width = 40;
        closeButton.Height = 34;
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Location = new Point(titleBar.Width - closeButton.Width, 0);
        closeButton.Click += delegate { Close(); };
        titleBar.Controls.Add(closeButton);
        titleBar.Resize += delegate { closeButton.Location = new Point(titleBar.Width - closeButton.Width, 0); };

        overallPill = new StatusPill();
        overallPill.Dock = DockStyle.Fill;
        overallPill.Margin = new Padding(0, 6, 0, 14);
        overallPill.SetState(StatusKind.Working, "正在检查状态", "启动前会先确认移动硬盘已插入。");
        layout.Controls.Add(overallPill, 0, 1);

        TableLayoutPanel cards = new TableLayoutPanel();
        cards.Dock = DockStyle.Fill;
        cards.BackColor = Color.Transparent;
        cards.ColumnCount = 1;
        cards.RowCount = 2;
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(cards, 0, 2);

        driveCard = new StatusCard("移动硬盘", "正在检测 " + config.MediaRoot);
        backendCard = new StatusCard("本机后端", localStatusUrl);
        cards.Controls.Add(driveCard, 0, 0);
        cards.Controls.Add(backendCard, 0, 1);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.BackColor = Color.Transparent;
        actions.WrapContents = false;
        actions.FlowDirection = FlowDirection.LeftToRight;
        layout.Controls.Add(actions, 0, 3);

        startButton = new GlassButton("启动后端", true);
        stopButton = new GlassButton("停止后端", false);
        checkButton = new GlassButton("检查状态", false);
        actions.Controls.Add(startButton);
        actions.Controls.Add(stopButton);
        actions.Controls.Add(checkButton);

        startButton.Click += async delegate { await StartBackendAsync(); };
        stopButton.Click += async delegate { await StopBackendAsync(); };
        checkButton.Click += async delegate { await CheckStatusAsync(); };
        Shown += async delegate { await CheckStatusAsync(); };
        Resize += delegate { ApplyRoundRegion(); };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(12, 15, 24), Color.FromArgb(3, 6, 12), 135F))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        DrawGlow(e.Graphics, new Rectangle(-90, -80, 260, 220), Color.FromArgb(76, 75, 203, 255));
        DrawGlow(e.Graphics, new Rectangle(Width - 230, 70, 280, 240), Color.FromArgb(58, 93, 255, 202));
        DrawGlow(e.Graphics, new Rectangle(120, Height - 210, 300, 220), Color.FromArgb(46, 214, 130, 255));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
        using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1F))
        {
            e.Graphics.DrawPath(pen, path);
        }

        base.OnPaint(e);
    }

    private async Task StartBackendAsync()
    {
        SetBusy(true, "正在检测移动硬盘", "未插入或序列号不匹配时不会启动后端。");
        DriveCheckResult drive = await CheckDriveAsync();
        UpdateDriveCard(drive);

        if (!drive.Ready)
        {
            overallPill.SetState(StatusKind.Bad, "无法启动后端", drive.Summary);
            SetBusy(false, null, null);
            return;
        }

        SetBusy(true, "正在启动后端", "移动硬盘已就绪。");
        await RunPowerShellScriptAsync("start-gallery.ps1", "-ProjectRoot " + Quote(projectRoot));
        await Task.Delay(1600);
        await CheckStatusAsync();
    }

    private async Task StopBackendAsync()
    {
        SetBusy(true, "正在停止后端", "停止后可以尝试弹出移动硬盘。");
        await RunPowerShellScriptAsync("stop-gallery.ps1", string.Empty);
        await Task.Delay(700);
        await CheckStatusAsync();
    }

    private async Task CheckStatusAsync()
    {
        SetBusy(true, "正在检查状态", "正在检测移动硬盘和本机后端。");
        DriveCheckResult drive = await CheckDriveAsync();
        ConnectionResult backend = await CheckBackendAsync();
        UpdateDriveCard(drive);
        UpdateBackendCard(backend);

        if (drive.Ready && backend.Ok)
        {
            overallPill.SetState(StatusKind.Good, "后端正在工作", "移动硬盘已连接，本机后端可访问。");
        }
        else if (drive.Ready)
        {
            overallPill.SetState(StatusKind.Warning, "后端未启动", "移动硬盘已就绪，可以点击启动后端。");
        }
        else
        {
            overallPill.SetState(StatusKind.Bad, "请先插入移动硬盘", drive.Summary);
        }

        SetBusy(false, null, null);
    }

    private void UpdateDriveCard(DriveCheckResult drive)
    {
        driveCard.SetState(
            drive.Ready ? StatusKind.Good : StatusKind.Bad,
            drive.Ready ? "已连接" : "未就绪",
            drive.Summary
        );
    }

    private void UpdateBackendCard(ConnectionResult backend)
    {
        backendCard.SetState(
            backend.Ok ? StatusKind.Good : StatusKind.Bad,
            backend.Ok ? "运行中" : "未启动",
            backend.Message
        );
    }

    private async Task<DriveCheckResult> CheckDriveAsync()
    {
        return await Task.Run(delegate
        {
            bool driveRootPresent = Directory.Exists(config.DriveLetter + ":\\");
            bool mediaRootPresent = Directory.Exists(config.MediaRoot);
            DiskSerialResult serial = GetDiskSerials(config.DriveLetter);
            string expectedSerial = NormalizeSerial(config.ExpectedDiskSerial);
            bool serialMatches = expectedSerial.Length > 0
                ? serial.ConnectedSerials.Contains(expectedSerial)
                : NormalizeSerial(serial.DriveSerial).Length > 0;
            bool ready = driveRootPresent && mediaRootPresent && serialMatches;

            string summary;
            if (ready)
            {
                summary = config.DriveLetter + ": 已插入，影像备份可访问，序列号匹配。";
            }
            else if (!driveRootPresent)
            {
                summary = "未检测到 " + config.DriveLetter + ": 盘，请先插入移动硬盘。";
            }
            else if (!mediaRootPresent)
            {
                summary = "已检测到 " + config.DriveLetter + ":，但找不到 " + config.MediaRoot + "。";
            }
            else if (!serialMatches)
            {
                summary = "硬盘序列号不匹配，当前：" + (serial.DriveSerial.Length > 0 ? serial.DriveSerial : "未读取到");
            }
            else
            {
                summary = "移动硬盘未就绪。";
            }

            return new DriveCheckResult(ready, summary);
        });
    }

    private async Task<ConnectionResult> CheckBackendAsync()
    {
        return await Task.Run(delegate
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(localStatusUrl);
                request.Method = "GET";
                request.Timeout = 3500;
                request.ReadWriteTimeout = 3500;
                request.UserAgent = "zxn-photo-gallery-control";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    bool httpOk = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                    bool bodyOk = Regex.IsMatch(body, "\"ok\"\\s*:\\s*true", RegexOptions.IgnoreCase);
                    return new ConnectionResult(httpOk && bodyOk, httpOk && bodyOk ? "本机后端连接成功。" : "本机后端返回异常。");
                }
            }
            catch
            {
                return new ConnectionResult(false, "后端没有运行。");
            }
        });
    }

    private DiskSerialResult GetDiskSerials(string driveLetter)
    {
        string command =
            "$driveLetter = '" + driveLetter.Replace("'", "") + "'; " +
            "$driveDisk = $null; " +
            "try { $driveDisk = Get-Partition -DriveLetter $driveLetter -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch {}; " +
            "if ($driveDisk) { 'DRIVE=' + [string]$driveDisk.SerialNumber }; " +
            "Get-Disk | ForEach-Object { 'SERIAL=' + [string]$_.SerialNumber }";
        string output = RunHiddenProcess("powershell.exe", "-NoProfile -Command " + Quote(command), 5000);
        string driveSerial = string.Empty;
        List<string> connected = new List<string>();

        foreach (string rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("DRIVE=", StringComparison.OrdinalIgnoreCase))
            {
                driveSerial = line.Substring(6).Trim();
            }
            else if (line.StartsWith("SERIAL=", StringComparison.OrdinalIgnoreCase))
            {
                string serial = NormalizeSerial(line.Substring(7));
                if (serial.Length > 0 && !connected.Contains(serial))
                {
                    connected.Add(serial);
                }
            }
        }

        return new DiskSerialResult(driveSerial, connected);
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

        await Task.Run(delegate { RunHiddenProcess("powershell.exe", arguments, 20000); });
    }

    private static string RunHiddenProcess(string fileName, string arguments, int timeoutMs)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = fileName;
        startInfo.Arguments = arguments;
        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;

        using (Process process = Process.Start(startInfo))
        {
            if (process == null)
            {
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(timeoutMs);
            return output;
        }
    }

    private void SetBusy(bool busy, string title, string detail)
    {
        isBusy = busy;
        startButton.Enabled = !busy;
        stopButton.Enabled = !busy;
        checkButton.Enabled = !busy;
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

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string NormalizeSerial(string value)
    {
        return Regex.Replace(value ?? string.Empty, "\\s+", "").ToUpperInvariant();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

internal sealed class GalleryConfig
{
    public readonly int Port;
    public readonly string DriveLetter;
    public readonly string MediaRoot;
    public readonly string ExpectedDiskSerial;

    private GalleryConfig(int port, string driveLetter, string mediaRoot, string expectedDiskSerial)
    {
        Port = port;
        DriveLetter = driveLetter;
        MediaRoot = mediaRoot;
        ExpectedDiskSerial = expectedDiskSerial;
    }

    public static GalleryConfig Load(string projectRoot)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string envPath = Path.Combine(projectRoot, ".env");
        if (File.Exists(envPath))
        {
            foreach (string rawLine in File.ReadAllLines(envPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || !line.Contains("="))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim().Trim('"');
                values[key] = value;
            }
        }

        int parsedPort;
        int port = values.ContainsKey("PORT") && int.TryParse(values["PORT"], out parsedPort) ? parsedPort : 8787;
        string mediaRoot = values.ContainsKey("GALLERY_ROOT") ? values["GALLERY_ROOT"] : @"F:\影像备份";
        string drive = values.ContainsKey("GALLERY_DRIVE") ? values["GALLERY_DRIVE"] : Path.GetPathRoot(mediaRoot).Replace(":", "").Replace("\\", "");
        if (string.IsNullOrWhiteSpace(drive)) drive = "F";
        string serial = values.ContainsKey("GALLERY_DISK_SERIAL") ? values["GALLERY_DISK_SERIAL"] : string.Empty;
        return new GalleryConfig(port, drive.Trim().Substring(0, 1).ToUpperInvariant(), mediaRoot, serial);
    }
}

internal sealed class DriveCheckResult
{
    public readonly bool Ready;
    public readonly string Summary;

    public DriveCheckResult(bool ready, string summary)
    {
        Ready = ready;
        Summary = summary;
    }
}

internal sealed class DiskSerialResult
{
    public readonly string DriveSerial;
    public readonly List<string> ConnectedSerials;

    public DiskSerialResult(string driveSerial, List<string> connectedSerials)
    {
        DriveSerial = driveSerial;
        ConnectedSerials = connectedSerials;
    }
}

internal sealed class ConnectionResult
{
    public readonly bool Ok;
    public readonly string Message;

    public ConnectionResult(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }
}

internal enum StatusKind
{
    Good,
    Warning,
    Bad,
    Working
}

internal sealed class GlassFrame : Panel
{
    public GlassFrame()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 24, 34);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(20, 24, 34));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 28))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(46, 52, 66), Color.FromArgb(24, 29, 39), 135F))
        using (Pen border = new Pen(Color.FromArgb(78, 255, 255, 255), 1F))
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
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        ApplyClip();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyClip();
    }

    private void ApplyClip()
    {
        if (Width <= 0 || Height <= 0) return;
        using (GraphicsPath path = BackendControlForm.RoundedRect(new Rectangle(0, 0, Width, Height), 24))
        {
            Region = new Region(path);
        }
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
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Blend(accent, Color.FromArgb(34, 38, 50), 0.18F), Color.FromArgb(30, 34, 45), 0F))
        using (Pen border = new Pen(Color.FromArgb(76, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        using (SolidBrush glow = new SolidBrush(Color.FromArgb(58, accent)))
        using (SolidBrush dot = new SolidBrush(accent))
        {
            e.Graphics.FillEllipse(glow, 16, 22, 34, 34);
            e.Graphics.FillEllipse(dot, 25, 31, 16, 16);
        }

        using (SolidBrush titleBrush = new SolidBrush(Color.White))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(190, 216, 226, 240)))
        {
            e.Graphics.DrawString(title, new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), titleBrush, 58, 20);
            e.Graphics.DrawString(detail, new Font("Microsoft YaHei UI", 9.2F), detailBrush, 59, 48);
        }
    }

    internal static Color Palette(StatusKind kind)
    {
        if (kind == StatusKind.Good) return Color.FromArgb(77, 223, 148);
        if (kind == StatusKind.Warning) return Color.FromArgb(250, 204, 60);
        if (kind == StatusKind.Bad) return Color.FromArgb(255, 112, 128);
        return Color.FromArgb(116, 205, 255);
    }

    internal static Color Blend(Color a, Color b, float amount)
    {
        return Color.FromArgb(
            (int)(a.R * amount + b.R * (1F - amount)),
            (int)(a.G * amount + b.G * (1F - amount)),
            (int)(a.B * amount + b.B * (1F - amount))
        );
    }
}

internal sealed class StatusCard : Control
{
    private readonly string name;
    private string state = "未检查";
    private string detail;
    private StatusKind kind = StatusKind.Warning;

    public StatusCard(string title, string subtitle)
    {
        name = title;
        detail = subtitle;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 12);
        ApplyClip();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyClip();
    }

    private void ApplyClip()
    {
        if (Width <= 0 || Height <= 0) return;
        using (GraphicsPath path = BackendControlForm.RoundedRect(new Rectangle(0, 0, Width, Height), 22))
        {
            Region = new Region(path);
        }
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
        Color accent = StatusPill.Palette(kind);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 22))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(38, 44, 58), Color.FromArgb(28, 33, 45), 120F))
        using (Pen border = new Pen(Color.FromArgb(60, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        using (SolidBrush accentBrush = new SolidBrush(accent))
        using (SolidBrush titleBrush = new SolidBrush(Color.White))
        using (SolidBrush stateBrush = new SolidBrush(accent))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(172, 205, 216, 232)))
        {
            e.Graphics.FillEllipse(accentBrush, 20, Height / 2 - 7, 14, 14);
            e.Graphics.DrawString(name, new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), titleBrush, 48, 20);
            SizeF stateSize = e.Graphics.MeasureString(state, new Font("Microsoft YaHei UI", 11F, FontStyle.Bold));
            e.Graphics.DrawString(state, new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), stateBrush, Width - stateSize.Width - 24, 20);
            e.Graphics.DrawString(TrimToWidth(e.Graphics, detail, Width - 74), new Font("Microsoft YaHei UI", 9F), detailBrush, 49, 50);
        }
    }

    private string TrimToWidth(Graphics graphics, string value, int maxWidth)
    {
        if (graphics.MeasureString(value, Font).Width <= maxWidth) return value;
        for (int length = value.Length - 1; length > 0; length--)
        {
            string candidate = value.Substring(0, length) + "...";
            if (graphics.MeasureString(candidate, Font).Width <= maxWidth) return candidate;
        }
        return "...";
    }
}

internal sealed class GlassButton : Control
{
    private readonly bool primary;
    private bool hovering;
    private bool pressing;

    public GlassButton(string text, bool isPrimary)
    {
        Text = text;
        primary = isPrimary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Width = text.Length <= 1 ? 40 : 132;
        Height = text.Length <= 1 ? 34 : 46;
        Margin = new Padding(0, 8, 10, 0);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        ApplyClip();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyClip();
    }

    private void ApplyClip()
    {
        if (Width <= 0 || Height <= 0) return;
        using (GraphicsPath path = BackendControlForm.RoundedRect(new Rectangle(0, 0, Width, Height), 16))
        {
            Region = new Region(path);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        pressing = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            pressing = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool shouldClick = pressing && ClientRectangle.Contains(e.Location);
        pressing = false;
        Invalidate();
        if (shouldClick)
        {
            OnClick(EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color top = primary ? Color.FromArgb(120, 213, 255) : Color.FromArgb(56, 64, 82);
        Color bottom = primary ? Color.FromArgb(186, 150, 255) : Color.FromArgb(38, 44, 60);
        if (hovering) top = StatusPill.Blend(Color.White, top, 0.12F);
        if (pressing) bottom = StatusPill.Blend(Color.Black, bottom, 0.18F);

        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 16))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, top, bottom, 90F))
        using (Pen border = new Pen(Color.FromArgb(82, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        TextRenderer.DrawText(e.Graphics, Text, new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold), rect, primary ? Color.FromArgb(8, 17, 28) : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
