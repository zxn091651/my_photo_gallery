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
    private readonly ChainView chainView;
    private readonly ComboBox tunnelCombo;
    private readonly ToolTip tunnelToolTip;
    private readonly GlassButton saveTunnelButton;
    private readonly GlassButton refreshTunnelButton;
    private readonly GlassButton startButton;
    private readonly GlassButton stopButton;
    private readonly GlassButton checkButton;
    private readonly Timer statusTimer;
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private bool isBusy;
    private bool isChecking;
    private bool allowExit;
    private DateTime lastTunnelRestartAt = DateTime.MinValue;

    public BackendControlForm()
    {
        projectRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        config = GalleryConfig.Load(projectRoot);
        localStatusUrl = "http://127.0.0.1:" + config.Port + "/api/status";

        Text = "zxn's Photo Gallery";
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 500);
        Size = new Size(680, 520);
        BackColor = Color.FromArgb(8, 10, 16);
        ForeColor = Color.White;
        DoubleBuffered = true;
        try
        {
            Icon extractedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extractedIcon != null)
            {
                Icon = extractedIcon;
            }
        }
        catch {}
        ApplyRoundRegion();

        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示界面", null, delegate { ShowFromTray(); });
        trayMenu.Items.Add("关闭控制器", null, delegate { ExitFromTray(); });

        trayIcon = new NotifyIcon();
        trayIcon.Text = "zxn's Photo Gallery 后端控制器";
        trayIcon.Icon = Icon ?? SystemIcons.Application;
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = false;
        trayIcon.DoubleClick += delegate { ShowFromTray(); };

        GlassFrame root = new GlassFrame();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(24);
        Controls.Add(root);

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.BackColor = Color.Transparent;
        layout.ColumnCount = 1;
        layout.RowCount = 5;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
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
        closeButton.Click += delegate { HideToTray(); };
        titleBar.Controls.Add(closeButton);
        titleBar.Resize += delegate { closeButton.Location = new Point(titleBar.Width - closeButton.Width, 0); };

        overallPill = new StatusPill();
        overallPill.Dock = DockStyle.Fill;
        overallPill.Margin = new Padding(0, 6, 0, 14);
        overallPill.SetState(StatusKind.Working, "正在检查状态", "启动前会先确认移动硬盘已插入。");
        layout.Controls.Add(overallPill, 0, 1);

        chainView = new ChainView();
        chainView.Dock = DockStyle.Fill;
        chainView.Margin = new Padding(0, 0, 0, 10);
        chainView.SetState(
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            "正在检测前端、隧道、后端和硬盘链路。"
        );
        layout.Controls.Add(chainView, 0, 2);

        Panel tunnelPanel = new Panel();
        tunnelPanel.Dock = DockStyle.Fill;
        tunnelPanel.BackColor = Color.Transparent;
        layout.Controls.Add(tunnelPanel, 0, 3);

        Label tunnelLabel = new Label();
        tunnelLabel.AutoSize = true;
        tunnelLabel.Text = "隧道配置";
        tunnelLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        tunnelLabel.ForeColor = Color.FromArgb(190, 216, 226, 240);
        tunnelLabel.Location = new Point(4, 16);
        tunnelPanel.Controls.Add(tunnelLabel);

        tunnelCombo = new ComboBox();
        tunnelCombo.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        tunnelCombo.DropDownStyle = ComboBoxStyle.DropDown;
        tunnelCombo.Width = 330;
        tunnelCombo.Height = 28;
        tunnelCombo.Location = new Point(78, 12);
        tunnelCombo.Font = new Font(Font.FontFamily, 9F, FontStyle.Regular);
        tunnelPanel.Controls.Add(tunnelCombo);
        tunnelToolTip = new ToolTip();
        tunnelCombo.SelectedIndexChanged += delegate { UpdateTunnelToolTip(); };
        tunnelCombo.TextChanged += delegate { UpdateTunnelToolTip(); };

        refreshTunnelButton = new GlassButton("刷新", false);
        refreshTunnelButton.Width = 76;
        refreshTunnelButton.Height = 36;
        refreshTunnelButton.Location = new Point(tunnelPanel.Width - 166, 8);
        refreshTunnelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        tunnelPanel.Controls.Add(refreshTunnelButton);

        saveTunnelButton = new GlassButton("保存", false);
        saveTunnelButton.Width = 76;
        saveTunnelButton.Height = 36;
        saveTunnelButton.Location = new Point(tunnelPanel.Width - 82, 8);
        saveTunnelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        tunnelPanel.Controls.Add(saveTunnelButton);

        tunnelPanel.Resize += delegate
        {
            tunnelCombo.Width = Math.Max(160, tunnelPanel.Width - 254);
            refreshTunnelButton.Location = new Point(tunnelPanel.Width - 166, 8);
            saveTunnelButton.Location = new Point(tunnelPanel.Width - 82, 8);
        };

        refreshTunnelButton.Click += delegate { LoadTunnelOptions(); };
        saveTunnelButton.Click += async delegate { await SaveSelectedTunnelConfigAsync(); };
        LoadTunnelOptions();

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.BackColor = Color.Transparent;
        actions.WrapContents = false;
        actions.FlowDirection = FlowDirection.LeftToRight;
        layout.Controls.Add(actions, 0, 4);

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

        statusTimer = new Timer();
        statusTimer.Interval = 10000;
        statusTimer.Tick += async delegate
        {
            if (!isBusy)
            {
                await CheckStatusAsync(false);
            }
        };
        statusTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        trayIcon.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();
        trayMenu.Dispose();
        base.OnFormClosed(e);
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        trayIcon.Visible = true;
    }

    private void ShowFromTray()
    {
        trayIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        allowExit = true;
        trayIcon.Visible = false;
        Close();
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
        chainView.SetState(
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            drive.Ready ? StatusKind.Good : StatusKind.Bad,
            StatusKind.Working,
            StatusKind.Working,
            StatusKind.Working,
            drive.Summary
        );

        if (!drive.Ready)
        {
            overallPill.SetState(StatusKind.Bad, "无法启动后端", drive.Summary);
            SetBusy(false, null, null);
            return;
        }

        SetBusy(true, "正在启动后端", "移动硬盘已就绪，正在直接启动 frpc 隧道。");
        await SaveSelectedTunnelConfigAsync(false);
        await StartFrpcAsync();
        await RunPowerShellScriptAsync("start-gallery.ps1", "-ProjectRoot " + Quote(projectRoot));
        await Task.Delay(1600);
        await CheckStatusAsync();
    }

    private async Task StopBackendAsync()
    {
        SetBusy(true, "正在停止后端", "正在停止本机后端和 frpc 隧道。");
        await RunPowerShellScriptAsync("stop-gallery.ps1", string.Empty);
        await StopFrpcAsync();
        await Task.Delay(700);
        await CheckStatusAsync();
    }

    private async Task CheckStatusAsync()
    {
        await CheckStatusAsync(true);
    }

    private async Task CheckStatusAsync(bool showBusy)
    {
        if (isChecking)
        {
            return;
        }

        isChecking = true;
        try
        {
            if (showBusy)
            {
                SetBusy(true, "正在检查状态", "正在检测移动硬盘、本机后端和公网链路。");
            }

            DriveCheckResult drive = await CheckDriveAsync();
            ConnectionResult backend = await CheckBackendAsync();
            ConnectionResult publicChain = await CheckPublicChainAsync();
            bool restartedTunnel = false;

            if (drive.Ready && backend.Ok && !publicChain.Ok && ShouldRestartTunnel())
            {
                restartedTunnel = true;
                overallPill.SetState(StatusKind.Working, "正在重启隧道", "公网链路异常，正在重启 frpc 后复查。");
                await RestartFrpcAsync();
                publicChain = await CheckPublicChainAsync();
            }

            UpdateChainView(drive, backend, publicChain, restartedTunnel);

            bool effectiveBackendOk = backend.Ok || publicChain.Ok;

            if (drive.Ready && effectiveBackendOk && publicChain.Ok)
            {
                overallPill.SetState(StatusKind.Good, "公网访问正常", "前端可通过隧道连接到本机后端。");
            }
            else if (drive.Ready && effectiveBackendOk)
            {
                overallPill.SetState(
                    StatusKind.Warning,
                    restartedTunnel ? "隧道重启后仍未连通" : "本机后端正常，隧道未连通",
                    publicChain.Message
                );
            }
            else if (drive.Ready)
            {
                overallPill.SetState(StatusKind.Warning, "后端未启动", "移动硬盘已就绪，可以点击启动后端。");
            }
            else
            {
                overallPill.SetState(StatusKind.Bad, "请先插入移动硬盘", drive.Summary);
            }
        }
        finally
        {
            isChecking = false;
            if (showBusy)
            {
                SetBusy(false, null, null);
            }
        }
    }

    private void UpdateChainView(DriveCheckResult drive, ConnectionResult backend, ConnectionResult publicChain, bool restartedTunnel)
    {
        bool tunnelRunning = IsSelectedFrpcRunning();
        bool effectiveBackendOk = backend.Ok || publicChain.Ok;
        StatusKind frontendNode = string.IsNullOrWhiteSpace(config.PublicStatusUrl) ? StatusKind.Warning : StatusKind.Good;
        StatusKind tunnelNode = tunnelRunning || publicChain.Ok ? StatusKind.Good : StatusKind.Bad;
        StatusKind backendNode = effectiveBackendOk ? StatusKind.Good : StatusKind.Bad;
        StatusKind driveNode = drive.Ready ? StatusKind.Good : StatusKind.Bad;
        StatusKind frontendToTunnel = publicChain.Ok ? StatusKind.Good : StatusKind.Bad;
        StatusKind tunnelToBackend = publicChain.Ok && effectiveBackendOk ? StatusKind.Good : StatusKind.Bad;
        StatusKind backendToDrive = effectiveBackendOk && drive.Ready ? StatusKind.Good : StatusKind.Bad;

        string detail;
        if (publicChain.Ok && effectiveBackendOk && drive.Ready)
        {
            detail = "前端 → 隧道 → 后端 → 硬盘 全链路正常。";
        }
        else if (effectiveBackendOk && drive.Ready && !publicChain.Ok)
        {
            detail = restartedTunnel
                ? "隧道异常，已自动重启 frpc 后复查，仍未连通。"
                : "本机后端和硬盘正常，公网链路未连通。";
        }
        else if (!backend.Ok)
        {
            detail = backend.Message;
        }
        else
        {
            detail = drive.Summary;
        }

        chainView.SetState(frontendNode, tunnelNode, backendNode, driveNode, frontendToTunnel, tunnelToBackend, backendToDrive, detail);
    }

    private async Task<DriveCheckResult> CheckDriveAsync()
    {
        return await Task.Run(delegate
        {
            DiskSerialResult serial = FindDriveBySerial();
            string expectedSerial = NormalizeSerial(config.ExpectedDiskSerial);
            bool serialConfigured = expectedSerial.Length > 0;
            bool serialMatches = serialConfigured && NormalizeSerial(serial.DriveSerial) == expectedSerial;
            string mediaRoot = serialMatches && serial.DriveLetter.Length > 0
                ? Path.Combine(serial.DriveLetter + ":\\", config.MediaFolderName)
                : string.Empty;
            bool mediaRootPresent = mediaRoot.Length > 0 && Directory.Exists(mediaRoot);
            bool ready = serialMatches && mediaRootPresent;

            string summary;
            if (ready)
            {
                summary = serial.DriveLetter + ": 已插入，" + config.MediaFolderName + " 可访问，序列号匹配。";
            }
            else if (!serialConfigured)
            {
                summary = "未配置 GALLERY_DISK_SERIAL，无法识别移动硬盘。";
            }
            else if (!serialMatches)
            {
                summary = "未找到序列号为 " + config.ExpectedDiskSerial + " 的移动硬盘。";
            }
            else if (serial.DriveLetter.Length == 0)
            {
                summary = "已找到移动硬盘，但 Windows 没有分配盘符。";
            }
            else if (!mediaRootPresent)
            {
                summary = "已找到移动硬盘 " + serial.DriveLetter + ":，但找不到 " + mediaRoot + "。";
            }
            else
            {
                summary = "移动硬盘未就绪。";
            }

            return new DriveCheckResult(ready, summary);
        });
    }

    private DiskSerialResult FindDriveBySerial()
    {
        string expectedSerial = NormalizeSerial(config.ExpectedDiskSerial);
        string command =
            "$disks = Get-Disk | ForEach-Object { " +
            "$disk = $_; " +
            "$letters = @(); " +
            "try { $letters = @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter }) } catch {}; " +
            "'DISK=' + [string]$disk.SerialNumber + '|' + (($letters -join ',')) " +
            "}; $disks";
        string output = RunHiddenProcess("powershell.exe", "-NoProfile -Command " + Quote(command), 5000);

        foreach (string rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("DISK=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string payload = line.Substring(5);
            string[] parts = payload.Split(new[] { '|' }, 2);
            string serial = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            if (NormalizeSerial(serial) != expectedSerial)
            {
                continue;
            }

            string driveLetter = string.Empty;
            if (parts.Length > 1)
            {
                string[] letters = parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string letter in letters)
                {
                    string clean = Regex.Replace(letter, "[^A-Za-z]", "").ToUpperInvariant();
                    if (clean.Length > 0)
                    {
                        driveLetter = clean.Substring(0, 1);
                        break;
                    }
                }
            }

            return new DiskSerialResult(serial, driveLetter);
        }

        return new DiskSerialResult(string.Empty, string.Empty);
    }

    private async Task<ConnectionResult> CheckBackendAsync()
    {
        return await Task.Run(delegate
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(localStatusUrl);
                request.Method = "GET";
                request.Timeout = 12000;
                request.ReadWriteTimeout = 12000;
                request.UserAgent = "zxn-photo-gallery-control";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    bool httpOk = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                    bool apiShapeOk = Regex.IsMatch(body, "\"computerOnline\"\\s*:", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(body, "\"ok\"\\s*:", RegexOptions.IgnoreCase);
                    bool backendOk = httpOk && apiShapeOk;
                    return new ConnectionResult(backendOk, backendOk ? "本机后端连接成功。" : "本机后端返回异常。");
                }
            }
            catch
            {
                if (IsProjectBackendListening())
                {
                    return new ConnectionResult(true, "本机后端端口正在监听。");
                }

                return new ConnectionResult(false, "后端没有运行。");
            }
        });
    }

    private async Task<ConnectionResult> CheckPublicChainAsync()
    {
        return await Task.Run(delegate
        {
            if (string.IsNullOrWhiteSpace(config.PublicStatusUrl))
            {
                return new ConnectionResult(false, "未配置公网状态地址。");
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(config.PublicStatusUrl);
                request.Method = "GET";
                request.Timeout = 12000;
                request.ReadWriteTimeout = 12000;
                request.UserAgent = "zxn-photo-gallery-control";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    bool httpOk = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                    bool apiShapeOk = Regex.IsMatch(body, "\"computerOnline\"\\s*:", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(body, "\"ok\"\\s*:", RegexOptions.IgnoreCase);
                    bool chainOk = httpOk && apiShapeOk;
                    return new ConnectionResult(
                        chainOk,
                        chainOk ? "公网 API 可访问，隧道已连到本机后端。" : "公网 API 返回异常。"
                    );
                }
            }
            catch
            {
                return new ConnectionResult(false, "公网 API 无法访问，请检查 frpc 隧道。");
            }
        });
    }

    private bool IsProjectBackendListening()
    {
        string command =
            "$listeners = @(Get-NetTCPConnection -LocalPort " + config.Port + " -State Listen -ErrorAction SilentlyContinue); " +
            "foreach ($listener in $listeners) { " +
            "$process = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $listener.OwningProcess) -ErrorAction SilentlyContinue; " +
            "if ($process -and $process.Name -ieq 'node.exe' -and $process.CommandLine -match 'server\\.js') { 'OK'; break } " +
            "}";
        string output = RunHiddenProcess("powershell.exe", "-NoProfile -Command " + Quote(command), 6000);
        return output.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private async Task StartFrpcAsync()
    {
        TunnelConfigOption selectedOption = GetSelectedTunnelOption();
        string selectedConfigPath = selectedOption == null ? GetSelectedTunnelConfigPath() : selectedOption.ConfigPath;
        string selectedProxyId = selectedOption == null ? ExtractProxyId(selectedConfigPath) : selectedOption.ProxyId;
        await Task.Run(delegate
        {
            if (selectedProxyId.Length > 0)
            {
                if (IsFrpcRunningForProxy(selectedProxyId))
                {
                    return;
                }

                string userToken = selectedOption == null ? ReadChmlFrpUserToken() : selectedOption.UserToken;
                if (userToken.Length == 0 || config.FrpcPath.Length == 0 || !File.Exists(config.FrpcPath))
                {
                    return;
                }

                ProcessStartInfo proxyStartInfo = new ProcessStartInfo();
                proxyStartInfo.FileName = config.FrpcPath;
                proxyStartInfo.Arguments = "-u " + Quote(userToken) + " -p " + Quote(selectedProxyId);
                proxyStartInfo.WorkingDirectory = Path.GetDirectoryName(config.FrpcPath);
                proxyStartInfo.CreateNoWindow = true;
                proxyStartInfo.UseShellExecute = false;
                proxyStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(proxyStartInfo);
                return;
            }

            if (selectedConfigPath.Length == 0 || !File.Exists(selectedConfigPath))
            {
                return;
            }

            if (IsFrpcRunningForConfig(selectedConfigPath))
            {
                return;
            }

            if (config.FrpcPath.Length == 0 || !File.Exists(config.FrpcPath))
            {
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = config.FrpcPath;
            startInfo.Arguments = "-c " + Quote(selectedConfigPath);
            startInfo.WorkingDirectory = Path.GetDirectoryName(config.FrpcPath);
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(startInfo);
        });

        await Task.Delay(1800);
    }

    private async Task StopFrpcAsync()
    {
        TunnelConfigOption selectedOption = GetSelectedTunnelOption();
        string selectedConfigPath = selectedOption == null ? GetSelectedTunnelConfigPath() : selectedOption.ConfigPath;
        string selectedProxyId = selectedOption == null ? ExtractProxyId(selectedConfigPath) : selectedOption.ProxyId;
        await Task.Run(delegate
        {
            try
            {
                foreach (FrpcProcessInfo frpc in GetRunningFrpcProcesses())
                {
                    bool matchesProxy = selectedProxyId.Length > 0 && CommandLineUsesProxy(frpc.CommandLine, selectedProxyId);
                    bool matchesConfig = selectedConfigPath.Length > 0 && CommandLineUsesConfig(frpc.CommandLine, selectedConfigPath);
                    if (!matchesProxy && !matchesConfig)
                    {
                        continue;
                    }

                    try
                    {
                        Process process = Process.GetProcessById(frpc.ProcessId);
                        process.Kill();
                        process.WaitForExit(4000);
                        process.Dispose();
                    }
                    catch {}
                }
            }
            catch {}
        });
    }

    private bool ShouldRestartTunnel()
    {
        return DateTime.Now - lastTunnelRestartAt > TimeSpan.FromSeconds(60);
    }

    private async Task RestartFrpcAsync()
    {
        lastTunnelRestartAt = DateTime.Now;
        await StopFrpcAsync();
        await Task.Delay(1200);
        await StartFrpcAsync();
        await Task.Delay(3500);
    }

    private string GetSelectedTunnelConfigPath()
    {
        TunnelConfigOption selectedOption = tunnelCombo.SelectedItem as TunnelConfigOption;
        if (selectedOption != null)
        {
            return selectedOption.ConfigPath;
        }

        string selectedText = tunnelCombo.Text == null ? string.Empty : tunnelCombo.Text.Trim().Trim('"');
        foreach (object item in tunnelCombo.Items)
        {
            TunnelConfigOption option = item as TunnelConfigOption;
            if (option != null && string.Equals(option.DisplayName, selectedText, StringComparison.OrdinalIgnoreCase))
            {
                return option.ConfigPath;
            }
        }

        return selectedText;
    }

    private TunnelConfigOption GetSelectedTunnelOption()
    {
        TunnelConfigOption selectedOption = tunnelCombo.SelectedItem as TunnelConfigOption;
        if (selectedOption != null)
        {
            return selectedOption;
        }

        string selectedText = tunnelCombo.Text == null ? string.Empty : tunnelCombo.Text.Trim().Trim('"');
        foreach (object item in tunnelCombo.Items)
        {
            TunnelConfigOption option = item as TunnelConfigOption;
            if (option != null && (string.Equals(option.DisplayName, selectedText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.ConfigPath, selectedText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.ToString(), selectedText, StringComparison.OrdinalIgnoreCase)))
            {
                return option;
            }
        }

        return null;
    }

    private void LoadTunnelOptions()
    {
        string selected = GetSelectedTunnelConfigPath();
        if (selected.Length == 0)
        {
            selected = config.FrpcConfigPath;
        }

        List<TunnelConfigOption> options = DiscoverTunnelConfigs();
        tunnelCombo.Items.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < options.Count; index++)
        {
            TunnelConfigOption option = options[index];
            tunnelCombo.Items.Add(option);
            if (selected.Length > 0 && string.Equals(NormalizePath(option.ConfigPath), NormalizePath(selected), StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
            }
        }

        if (selectedIndex >= 0)
        {
            tunnelCombo.SelectedIndex = selectedIndex;
        }
        else if (selected.Length > 0 && File.Exists(selected))
        {
            tunnelCombo.Text = selected;
        }
        else if (tunnelCombo.Items.Count > 0)
        {
            tunnelCombo.SelectedIndex = 0;
        }

        UpdateTunnelToolTip();
    }

    private List<TunnelConfigOption> DiscoverTunnelConfigs()
    {
        return DiscoverTunnelConfigsV2();
    }

    private List<TunnelConfigOption> DiscoverTunnelConfigsV2()
    {
        List<TunnelConfigOption> options = new List<TunnelConfigOption>();
        string appDataConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "net.chmlfrp.launcher");
        string userToken = ReadChmlFrpUserToken();

        AddTunnelOption(options, config.FrpcConfigPath, "已保存的隧道", false, false);

        AddLauncherProxyOptions(options, appDataConfigDir, userToken);

        foreach (FrpcProcessInfo frpc in GetRunningFrpcProcesses())
        {
            AddTunnelOption(options, ExtractFrpcConfigPath(frpc.CommandLine), "当前运行隧道", true, true);
        }

        AddConfigFiles(options, appDataConfigDir);
        AddConfigFiles(options, Path.Combine(projectRoot, "tunnels"));
        AddConfigFiles(options, projectRoot);

        options.RemoveAll(delegate(TunnelConfigOption option)
        {
            return option.ProxyId.Length == 0 && !File.Exists(option.ConfigPath);
        });

        return options;
    }

    private static void AddConfigFiles(List<TunnelConfigOption> options, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.GetFiles(directory, "*.ini", SearchOption.TopDirectoryOnly))
            {
                AddTunnelOption(options, file, "隧道", false, false);
            }
        }
        catch {}
    }

    private static void AddRunningLauncherTunnelIds(List<TunnelConfigOption> options, string appDataConfigDir)
    {
        if (string.IsNullOrWhiteSpace(appDataConfigDir) || !Directory.Exists(appDataConfigDir))
        {
            return;
        }

        AddTunnelIdsFromJson(options, Path.Combine(appDataConfigDir, "running_tunnels.json"), appDataConfigDir, "当前运行隧道");
    }

    private static void AddTunnelIdsFromJson(List<TunnelConfigOption> options, string jsonPath, string appDataConfigDir, string prefix)
    {
        if (!File.Exists(jsonPath))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(jsonPath, Encoding.UTF8);
            MatchCollection matches = Regex.Matches(text, "\"(?:api_)?(?<id>\\d+)\"\\s*:", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string id = match.Groups["id"].Value;
                string path = Path.Combine(appDataConfigDir, "g_" + id + ".ini");
                bool running = string.Equals(prefix, "当前运行隧道", StringComparison.OrdinalIgnoreCase);
                AddTunnelOption(options, path, prefix, running, running);
            }
        }
        catch {}
    }

    private static void AddTunnelOption(List<TunnelConfigOption> options, string pathValue, string prefix, bool running, bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return;
        }

        string clean = pathValue.Trim().Trim('"');
        if (!allowMissing && !File.Exists(clean))
        {
            return;
        }

        foreach (TunnelConfigOption existing in options)
        {
            if (string.Equals(NormalizePath(existing.ConfigPath), NormalizePath(clean), StringComparison.OrdinalIgnoreCase))
            {
                if (running)
                {
                    existing.MarkRunning();
                }
                return;
            }
        }

        options.Add(new TunnelConfigOption(BuildTunnelDisplayName(clean, prefix, running), clean, File.Exists(clean), running));
    }

    private static void AddLauncherProxyOptions(List<TunnelConfigOption> options, string appDataConfigDir, string userToken)
    {
        List<TunnelConfigOption> apiOptions = FetchChmlFrpApiTunnelOptions(userToken);
        if (apiOptions.Count > 0)
        {
            foreach (TunnelConfigOption option in apiOptions)
            {
                AddProxyTunnelOption(options, option.ProxyId, option.DisplayName, userToken, IsFrpcRunningForProxyStatic(option.ProxyId));
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(appDataConfigDir) || !Directory.Exists(appDataConfigDir))
        {
            return;
        }

        List<string> ids = new List<string>();
        AddTunnelIdsFromJson(ids, Path.Combine(appDataConfigDir, "running_tunnels.json"));

        Dictionary<string, string> namesById = ReadChmlFrpTunnelNamesById();
        List<string> recentNames = ReadRecentChmlFrpTunnelNames();

        for (int index = 0; index < ids.Count; index++)
        {
            string id = ids[index];
            string name = namesById.ContainsKey(id) ? namesById[id] : string.Empty;
            if (name.Length == 0)
            {
                name = PickRecentTunnelName(recentNames, index, ids.Count);
            }
            AddProxyTunnelOption(options, id, name, userToken, IsFrpcRunningForProxyStatic(id));
        }
    }

    private static void AddTunnelIdsFromJson(List<string> ids, string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(jsonPath, Encoding.UTF8);
            MatchCollection matches = Regex.Matches(text, "\"(?:api_)?(?<id>\\d+)\"\\s*:", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string id = match.Groups["id"].Value;
                if (!ContainsIgnoreCase(ids, id))
                {
                    ids.Add(id);
                }
            }
        }
        catch {}
    }

    private static List<TunnelConfigOption> FetchChmlFrpApiTunnelOptions(string userToken)
    {
        List<TunnelConfigOption> result = new List<TunnelConfigOption>();
        if (string.IsNullOrWhiteSpace(userToken))
        {
            return result;
        }

        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://cf-v2.uapis.cn/tunnel");
            request.Method = "GET";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "zxn-photo-gallery-control";
            request.Headers[HttpRequestHeader.Authorization] = userToken;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                if (!Regex.IsMatch(body, "\"code\"\\s*:\\s*200|\"state\"\\s*:\\s*(true|\"success\")", RegexOptions.IgnoreCase))
                {
                    return result;
                }

                MatchCollection objects = Regex.Matches(body, "\\{[^{}]*\"id\"\\s*:\\s*\\d+[^{}]*\\}", RegexOptions.IgnoreCase);
                foreach (Match item in objects)
                {
                    string text = item.Value;
                    Match idMatch = Regex.Match(text, "\"id\"\\s*:\\s*(?<id>\\d+)", RegexOptions.IgnoreCase);
                    Match nameMatch = Regex.Match(text, "\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
                    if (!idMatch.Success)
                    {
                        continue;
                    }

                    string id = idMatch.Groups["id"].Value;
                    string name = nameMatch.Success ? DecodeJsonString(nameMatch.Groups["name"].Value) : string.Empty;
                    result.Add(new TunnelConfigOption(CleanTunnelName(name), BuildProxyKey(id), true, false, id, userToken));
                }
            }
        }
        catch {}

        return result;
    }

    private static void AddProxyTunnelOption(List<TunnelConfigOption> options, string proxyId, string tunnelName, string userToken, bool running)
    {
        string id = Regex.Replace(proxyId ?? string.Empty, "[^0-9]", "");
        if (id.Length == 0)
        {
            return;
        }

        string key = BuildProxyKey(id);
        foreach (TunnelConfigOption existing in options)
        {
            if (string.Equals(existing.ConfigPath, key, StringComparison.OrdinalIgnoreCase))
            {
                if (running)
                {
                    existing.MarkRunning();
                }
                return;
            }
        }

        string cleanName = CleanTunnelName(tunnelName);
        string displayName = cleanName.Length > 0 ? cleanName + " · " + id : "ChmlFrp " + id;
        options.Add(new TunnelConfigOption(displayName, key, true, running, id, userToken));
    }

    private static string PickRecentTunnelName(List<string> names, int index, int idCount)
    {
        if (names.Count == 0)
        {
            return string.Empty;
        }

        if (idCount == 2 && names.Count >= 2)
        {
            // ChmlFrp's auto-start JSON is stable by id, while WebView autofill tends to keep
            // the newest visible tunnel names near the front. Reversing keeps the old gallery
            // tunnel paired with its earlier id when a second tunnel is added.
            int reversed = idCount - 1 - index;
            if (reversed >= 0 && reversed < names.Count)
            {
                return names[reversed];
            }
        }

        return index < names.Count ? names[index] : string.Empty;
    }

    private static string LookupTunnelName(string proxyId)
    {
        Dictionary<string, string> namesById = ReadChmlFrpTunnelNamesById();
        string id = Regex.Replace(proxyId ?? string.Empty, "[^0-9]", "");
        return namesById.ContainsKey(id) ? namesById[id] : string.Empty;
    }

    private static string BuildProxyKey(string proxyId)
    {
        return "chmlfrp://proxy/" + Regex.Replace(proxyId ?? string.Empty, "[^0-9]", "");
    }

    private static bool ContainsIgnoreCase(List<string> values, string value)
    {
        foreach (string existing in values)
        {
            if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string CleanTunnelName(string value)
    {
        string clean = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9_.-]", "").Trim();
        string compact = clean.ToLowerInvariant();
        for (int length = 3; length <= clean.Length / 2; length++)
        {
            string first = compact.Substring(0, length);
            string second = compact.Substring(length, length);
            if (first == second)
            {
                clean = clean.Substring(0, length);
                break;
            }
        }

        if (clean.Length > 64)
        {
            clean = clean.Substring(0, 64);
        }
        return clean;
    }

    private static string DecodeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Regex.Unescape(value.Replace("\\\"", "\""));
        }
        catch
        {
            return value;
        }
    }

    private static string ReadChmlFrpUserToken()
    {
        string configured = ReadConfiguredChmlFrpUserToken();
        if (configured.Length > 0)
        {
            return configured;
        }

        string sdkToken = ReadSdkChmlFrpUserToken();
        if (sdkToken.Length > 0)
        {
            return sdkToken;
        }

        string compact = ReadChmlFrpLocalStorageCompactText();
        MatchCollection matches = Regex.Matches(compact, "usertoken\\\":\\\"(?<token>[A-Za-z0-9_-]{16,100})", RegexOptions.IgnoreCase);
        return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Groups["token"].Value;
    }

    private static string ReadConfiguredChmlFrpUserToken()
    {
        try
        {
            string envPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), ".env");
            if (!File.Exists(envPath))
            {
                return string.Empty;
            }

            foreach (string rawLine in File.ReadAllLines(envPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || !line.Contains("="))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                string key = line.Substring(0, separator).Trim();
                if (!string.Equals(key, "CHMLFRP_USER_TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(separator + 1).Trim().Trim('"');
            }
        }
        catch {}

        return string.Empty;
    }

    private static string ReadSdkChmlFrpUserToken()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ChmlFrp\user.json");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            Match match = Regex.Match(text, "\"usertoken\"\\s*:\\s*\"(?<token>[A-Za-z0-9_-]{16,100})\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["token"].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ReadChmlFrpTunnelNamesById()
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string compact = ReadChmlFrpLocalStorageCompactText();
        MatchCollection objects = Regex.Matches(compact, "\\{[^{}]{0,1200}?\\}", RegexOptions.IgnoreCase);
        foreach (Match item in objects)
        {
            string text = item.Value;
            Match idMatch = Regex.Match(text, "(?:tunnel_id|tunnelId|id)\\\":\\\"?(?:api_)?(?<id>\\d+)", RegexOptions.IgnoreCase);
            Match nameMatch = Regex.Match(text, "(?:tunnel_name|tunnelName|name)\\\":\\\"(?<name>[A-Za-z0-9_.-]{1,80})", RegexOptions.IgnoreCase);
            if (idMatch.Success && nameMatch.Success)
            {
                result[idMatch.Groups["id"].Value] = CleanTunnelName(nameMatch.Groups["name"].Value);
            }
        }
        return result;
    }

    private static List<string> ReadRecentChmlFrpTunnelNames()
    {
        List<string> names = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string webDataPath = Path.Combine(localAppData, @"net.chmlfrp.launcher\EBWebView\Default\Web Data");
        AddTunnelNamesFromBinaryFile(names, webDataPath);

        string compact = ReadChmlFrpLocalStorageCompactText();
        MatchCollection matches = Regex.Matches(compact, "tunnelName(?<name>[A-Za-z0-9_.-]{1,80})", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            AddUniqueTunnelName(names, match.Groups["name"].Value);
        }

        return names;
    }

    private static void AddTunnelNamesFromBinaryFile(List<string> names, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            byte[] bytes = ReadSharedFileBytes(path, 4 * 1024 * 1024);
            string text = Encoding.UTF8.GetString(bytes);
            MatchCollection matches = Regex.Matches(text, "tunnelName(?<name>[A-Za-z0-9_.-]{1,80})", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                AddUniqueTunnelName(names, match.Groups["name"].Value);
            }
        }
        catch {}
    }

    private static void AddUniqueTunnelName(List<string> names, string value)
    {
        string clean = CleanTunnelName(value);
        if (clean.Length == 0)
        {
            return;
        }

        string lower = clean.ToLowerInvariant();
        if (lower == "tunnelname" || lower == "domain" || lower == "localip")
        {
            return;
        }

        foreach (string existing in names)
        {
            if (string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        names.Add(clean);
    }

    private static string ReadChmlFrpLocalStorageCompactText()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string levelDbDir = Path.Combine(localAppData, @"net.chmlfrp.launcher\EBWebView\Default\Local Storage\leveldb");
            if (!Directory.Exists(levelDbDir))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            foreach (string file in Directory.GetFiles(levelDbDir))
            {
                if (string.Equals(Path.GetFileName(file), "LOCK", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] bytes = ReadSharedFileBytes(file, 2 * 1024 * 1024);
                builder.Append(Encoding.UTF8.GetString(bytes));
                builder.Append(Encoding.Unicode.GetString(bytes));
            }

            return Regex.Replace(builder.ToString(), "[\\x00\\s]", "");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] ReadSharedFileBytes(string path, int maxBytes)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int length = (int)Math.Min(stream.Length, maxBytes);
            byte[] bytes = new byte[length];
            int read = stream.Read(bytes, 0, length);
            if (read == bytes.Length)
            {
                return bytes;
            }

            byte[] trimmed = new byte[read];
            Array.Copy(bytes, trimmed, read);
            return trimmed;
        }
    }

    private static string BuildTunnelDisplayName(string pathValue, string prefix, bool running)
    {
        string tunnelName = ReadTunnelNameFromIni(pathValue);
        string id = ExtractTunnelId(pathValue);

        if (tunnelName.Length > 0 && id.Length > 0)
        {
            return tunnelName + "（" + id + "）";
        }

        if (tunnelName.Length > 0)
        {
            return tunnelName;
        }

        if (id.Length > 0)
        {
            return "隧道 " + id;
        }

        return Path.GetFileName(pathValue);
    }

    private static string ReadTunnelNameFromIni(string pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue) || !File.Exists(pathValue))
        {
            return string.Empty;
        }

        try
        {
            string firstProxySection = string.Empty;
            foreach (string rawLine in File.ReadAllLines(pathValue, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                Match section = Regex.Match(line, "^\\[(?<name>[^\\]]+)\\]$");
                if (section.Success)
                {
                    string sectionName = section.Groups["name"].Value.Trim();
                    if (!string.Equals(sectionName, "common", StringComparison.OrdinalIgnoreCase) && firstProxySection.Length == 0)
                    {
                        firstProxySection = sectionName;
                    }
                    continue;
                }

                Match keyValue = Regex.Match(line, "^(name|tunnel_name|tunnelName|proxy_name|remark|description)\\s*=\\s*(?<value>.+)$", RegexOptions.IgnoreCase);
                if (keyValue.Success)
                {
                    string value = keyValue.Groups["value"].Value.Trim().Trim('"');
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }

            return firstProxySection;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractTunnelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Match match = Regex.Match(value, "(?:g_|api_)(?<id>\\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : string.Empty;
    }

    private void UpdateTunnelToolTip()
    {
        TunnelConfigOption selectedOption = tunnelCombo.SelectedItem as TunnelConfigOption;
        if (selectedOption != null)
        {
            tunnelToolTip.SetToolTip(tunnelCombo, selectedOption.ConfigPath);
            return;
        }

        string selected = tunnelCombo.Text == null ? string.Empty : tunnelCombo.Text.Trim();
        tunnelToolTip.SetToolTip(tunnelCombo, selected);
    }

    private async Task SaveSelectedTunnelConfigAsync()
    {
        await SaveSelectedTunnelConfigAsync(true);
    }

    private async Task SaveSelectedTunnelConfigAsync(bool showMessage)
    {
        string selected = GetSelectedTunnelConfigPath();
        if (selected.Length == 0)
        {
            if (showMessage)
            {
                overallPill.SetState(StatusKind.Bad, "没有选择隧道", "请先选择或填写 frpc ini 配置路径。");
            }
            return;
        }

        bool saved = await Task.Run(delegate
        {
            return SaveEnvValue("CHMLFRP_CONFIG_PATH", selected);
        });

        if (showMessage)
        {
            if (!saved)
            {
                overallPill.SetState(StatusKind.Warning, "配置暂时未保存", ".env 正被其它进程占用，本次仍会继续使用当前选择。");
                return;
            }

            overallPill.SetState(
                File.Exists(selected) ? StatusKind.Good : StatusKind.Warning,
                "隧道配置已保存",
                File.Exists(selected) ? selected : "已保存，但当前找不到这个 ini 文件。"
            );
        }
    }

    private bool SaveEnvValue(string key, string value)
    {
        string envPath = Path.Combine(projectRoot, ".env");
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(envPath))
                {
                    using (FileStream readStream = new FileStream(envPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(readStream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        lines.AddRange(content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                    }
                }

                while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                bool updated = false;
                for (int index = 0; index < lines.Count; index++)
                {
                    string line = lines[index].Trim();
                    if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[index] = key + "=" + value;
                        updated = true;
                        break;
                    }
                }

                if (!updated)
                {
                    lines.Add(key + "=" + value);
                }

                using (FileStream writeStream = new FileStream(envPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter writer = new StreamWriter(writeStream, Encoding.UTF8))
                {
                    foreach (string line in lines)
                    {
                        writer.WriteLine(line);
                    }
                }

                return true;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(180);
            }
            catch (UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(180);
            }
        }

        return false;
    }

    private bool IsFrpcRunningForConfig(string configPath)
    {
        foreach (FrpcProcessInfo frpc in GetRunningFrpcProcesses())
        {
            if (CommandLineUsesConfig(frpc.CommandLine, configPath))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsFrpcRunningForProxy(string proxyId)
    {
        return IsFrpcRunningForProxyStatic(proxyId);
    }

    private static bool IsFrpcRunningForProxyStatic(string proxyId)
    {
        foreach (FrpcProcessInfo frpc in GetRunningFrpcProcesses())
        {
            if (CommandLineUsesProxy(frpc.CommandLine, proxyId))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsSelectedFrpcRunning()
    {
        TunnelConfigOption option = GetSelectedTunnelOption();
        string selected = option == null ? GetSelectedTunnelConfigPath() : option.ConfigPath;
        string proxyId = option == null ? ExtractProxyId(selected) : option.ProxyId;
        if (proxyId.Length > 0)
        {
            return IsFrpcRunningForProxy(proxyId);
        }

        return selected.Length > 0 && IsFrpcRunningForConfig(selected);
    }

    private static bool CommandLineUsesConfig(string commandLine, string configPath)
    {
        string activeConfig = ExtractFrpcConfigPath(commandLine);
        return activeConfig.Length > 0 && string.Equals(NormalizePath(activeConfig), NormalizePath(configPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool CommandLineUsesProxy(string commandLine, string proxyId)
    {
        string activeProxyId = ExtractProxyId(commandLine);
        return activeProxyId.Length > 0 && string.Equals(activeProxyId, Regex.Replace(proxyId ?? string.Empty, "[^0-9]", ""), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFrpcConfigPath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        Match match = Regex.Match(commandLine, "\\s-c\\s+(\"[^\"]+\"|\\S+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups[1].Value.Trim().Trim('"');
    }

    private static string ExtractProxyId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Match keyMatch = Regex.Match(value, "chmlfrp://proxy/(?<id>\\d+)", RegexOptions.IgnoreCase);
        if (keyMatch.Success)
        {
            return keyMatch.Groups["id"].Value;
        }

        Match commandMatch = Regex.Match(value, "\\s-(?:p|-id)\\s+(\"(?<id>\\d+)\"|(?<id>\\d+))", RegexOptions.IgnoreCase);
        if (commandMatch.Success)
        {
            return commandMatch.Groups["id"].Value;
        }

        Match apiMatch = Regex.Match(value, "(?:api_|g_)(?<id>\\d+)", RegexOptions.IgnoreCase);
        return apiMatch.Success ? apiMatch.Groups["id"].Value : string.Empty;
    }

    private static string NormalizePath(string value)
    {
        try
        {
            return Path.GetFullPath(value.Trim().Trim('"'));
        }
        catch
        {
            return value.Trim().Trim('"');
        }
    }

    private static List<FrpcProcessInfo> GetRunningFrpcProcesses()
    {
        List<FrpcProcessInfo> processes = new List<FrpcProcessInfo>();
        string command = "Get-CimInstance Win32_Process -Filter \"name='frpc.exe'\" | ForEach-Object { [string]$_.ProcessId + \"`t\" + [string]$_.CommandLine }";
        string output = RunHiddenProcess("powershell.exe", "-NoProfile -Command " + Quote(command), 6000);
        foreach (string rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = rawLine.Split(new[] { '\t' }, 2);
            int pid;
            if (parts.Length == 2 && int.TryParse(parts[0], out pid))
            {
                processes.Add(new FrpcProcessInfo(pid, parts[1]));
            }
        }
        return processes;
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
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
    public readonly string MediaFolderName;
    public readonly string ExpectedDiskSerial;
    public readonly string FrpcPath;
    public readonly string FrpcConfigPath;
    public readonly string PublicStatusUrl;

    private GalleryConfig(int port, string mediaFolderName, string expectedDiskSerial, string frpcPath, string frpcConfigPath, string publicStatusUrl)
    {
        Port = port;
        MediaFolderName = mediaFolderName;
        ExpectedDiskSerial = expectedDiskSerial;
        FrpcPath = frpcPath;
        FrpcConfigPath = frpcConfigPath;
        PublicStatusUrl = publicStatusUrl;
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
        string configuredRoot = values.ContainsKey("GALLERY_ROOT") ? values["GALLERY_ROOT"] : string.Empty;
        string mediaFolderName = values.ContainsKey("GALLERY_MEDIA_FOLDER") ? values["GALLERY_MEDIA_FOLDER"] : Path.GetFileName(configuredRoot.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(mediaFolderName)) mediaFolderName = "影像备份";
        string serial = values.ContainsKey("GALLERY_DISK_SERIAL") ? values["GALLERY_DISK_SERIAL"] : string.Empty;
        string frpcPath = values.ContainsKey("CHMLFRP_FRPC_PATH")
            ? values["CHMLFRP_FRPC_PATH"]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"net.chmlfrp.launcher\frpc.exe");
        string frpcConfigPath = values.ContainsKey("CHMLFRP_CONFIG_PATH")
            ? values["CHMLFRP_CONFIG_PATH"]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"net.chmlfrp.launcher\g_314121.ini");
        string publicStatusUrl = values.ContainsKey("GALLERY_PUBLIC_STATUS_URL")
            ? values["GALLERY_PUBLIC_STATUS_URL"]
            : "http://photo.fucku.top/api/status";
        return new GalleryConfig(port, mediaFolderName, serial, frpcPath, frpcConfigPath, publicStatusUrl);
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
    public readonly string DriveLetter;

    public DiskSerialResult(string driveSerial, string driveLetter)
    {
        DriveSerial = driveSerial;
        DriveLetter = driveLetter;
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

internal sealed class FrpcProcessInfo
{
    public readonly int ProcessId;
    public readonly string CommandLine;

    public FrpcProcessInfo(int processId, string commandLine)
    {
        ProcessId = processId;
        CommandLine = commandLine ?? "";
    }
}

internal sealed class TunnelConfigOption
{
    public readonly string DisplayName;
    public readonly string ConfigPath;
    public readonly bool Exists;
    public readonly string ProxyId;
    public readonly string UserToken;
    private bool isRunning;

    public TunnelConfigOption(string displayName, string configPath, bool exists, bool running)
        : this(displayName, configPath, exists, running, string.Empty, string.Empty)
    {
    }

    public TunnelConfigOption(string displayName, string configPath, bool exists, bool running, string proxyId, string userToken)
    {
        DisplayName = displayName ?? "";
        ConfigPath = configPath ?? "";
        Exists = exists;
        ProxyId = proxyId ?? "";
        UserToken = userToken ?? "";
        isRunning = running;
    }

    public void MarkRunning()
    {
        isRunning = true;
    }

    public override string ToString()
    {
        List<string> tags = new List<string>();
        if (isRunning)
        {
            tags.Add("当前运行");
        }
        if (!Exists)
        {
            tags.Add("ini 未找到");
        }

        return tags.Count == 0 ? DisplayName : DisplayName + " · " + string.Join(" · ", tags.ToArray());
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

internal sealed class ChainView : Control
{
    private StatusKind frontendNode = StatusKind.Working;
    private StatusKind tunnelNode = StatusKind.Working;
    private StatusKind backendNode = StatusKind.Working;
    private StatusKind driveNode = StatusKind.Working;
    private StatusKind frontendToTunnel = StatusKind.Working;
    private StatusKind tunnelToBackend = StatusKind.Working;
    private StatusKind backendToDrive = StatusKind.Working;
    private string detail = "";

    public ChainView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        ApplyClip();
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

    public void SetState(
        StatusKind frontend,
        StatusKind tunnel,
        StatusKind backend,
        StatusKind drive,
        StatusKind frontendTunnel,
        StatusKind tunnelBackend,
        StatusKind backendDrive,
        string detailText)
    {
        frontendNode = frontend;
        tunnelNode = tunnel;
        backendNode = backend;
        driveNode = drive;
        frontendToTunnel = frontendTunnel;
        tunnelToBackend = tunnelBackend;
        backendToDrive = backendDrive;
        detail = detailText ?? "";
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = BackendControlForm.RoundedRect(rect, 24))
        using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(42, 48, 63), Color.FromArgb(24, 29, 40), 120F))
        using (Pen border = new Pen(Color.FromArgb(68, 255, 255, 255), 1F))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);
        }

        int nodeSize = 62;
        int left = 24;
        int right = Width - 24;
        int span = Math.Max(1, right - left - nodeSize);
        int y = Math.Max(22, (Height - nodeSize) / 2 - 4);
        string[] nodeNames = new[] { "前端", "隧道", "后端", "硬盘" };
        StatusKind[] nodeKinds = new[]
        {
            frontendNode,
            tunnelNode,
            backendNode,
            driveNode
        };

        Point[] centers = new Point[4];
        for (int i = 0; i < 4; i++)
        {
            int x = left + (span * i / 3) + nodeSize / 2;
            centers[i] = new Point(x, y + nodeSize / 2);
        }

        DrawSegment(e.Graphics, centers[0], centers[1], frontendToTunnel, "前端-隧道");
        DrawSegment(e.Graphics, centers[1], centers[2], tunnelToBackend, "隧道-后端");
        DrawSegment(e.Graphics, centers[2], centers[3], backendToDrive, "后端-硬盘");

        for (int i = 0; i < 4; i++)
        {
            DrawNode(e.Graphics, centers[i], nodeSize, nodeNames[i], nodeKinds[i]);
        }

    }

    private void DrawSegment(Graphics graphics, Point start, Point end, StatusKind kind, string label)
    {
        Color accent = StatusPill.Palette(kind);
        using (Pen muted = new Pen(Color.FromArgb(44, 255, 255, 255), 8F))
        using (Pen active = new Pen(Color.FromArgb(172, accent), 4F))
        {
            muted.StartCap = LineCap.Round;
            muted.EndCap = LineCap.Round;
            active.StartCap = LineCap.Round;
            active.EndCap = LineCap.Round;
            graphics.DrawLine(muted, start.X + 30, start.Y, end.X - 30, end.Y);
            graphics.DrawLine(active, start.X + 30, start.Y, end.X - 30, end.Y);
        }

        int dotX = (start.X + end.X) / 2;
        int dotY = start.Y;
        using (SolidBrush glow = new SolidBrush(Color.FromArgb(70, accent)))
        using (SolidBrush dot = new SolidBrush(accent))
        {
            graphics.FillEllipse(glow, dotX - 15, dotY - 15, 30, 30);
            graphics.FillEllipse(dot, dotX - 7, dotY - 7, 14, 14);
        }

        using (SolidBrush text = new SolidBrush(Color.FromArgb(170, 212, 224, 238)))
        using (Font font = new Font("Microsoft YaHei UI", 8.2F, FontStyle.Bold))
        {
            SizeF size = graphics.MeasureString(label, font);
            graphics.DrawString(label, font, text, dotX - size.Width / 2F, dotY + 18);
        }
    }

    private void DrawNode(Graphics graphics, Point center, int size, string label, StatusKind kind)
    {
        Color accent = StatusPill.Palette(kind);
        Rectangle nodeRect = new Rectangle(center.X - size / 2, center.Y - size / 2, size, size);
        using (GraphicsPath path = BackendControlForm.RoundedRect(nodeRect, 22))
        using (LinearGradientBrush brush = new LinearGradientBrush(nodeRect, Color.FromArgb(64, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 135F))
        using (Pen border = new Pen(Color.FromArgb(100, accent), 2F))
        {
            graphics.FillPath(brush, path);
            graphics.DrawPath(border, path);
        }

        using (SolidBrush dot = new SolidBrush(accent))
        using (SolidBrush title = new SolidBrush(Color.White))
        using (Font titleFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold))
        {
            graphics.FillEllipse(dot, center.X - 5, center.Y - 19, 10, 10);
            SizeF sizeText = graphics.MeasureString(label, titleFont);
            graphics.DrawString(label, titleFont, title, center.X - sizeText.Width / 2F, center.Y - 2);
        }
    }

    private static string TrimToWidth(Graphics graphics, string value, Font font, int maxWidth)
    {
        if (graphics.MeasureString(value, font).Width <= maxWidth) return value;
        for (int length = value.Length - 1; length > 0; length--)
        {
            string candidate = value.Substring(0, length) + "...";
            if (graphics.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "...";
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
