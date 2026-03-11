using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Options;

namespace ResourceGuard;

public class Worker(ILogger<Worker> logger, IOptions<ResourceGuardOptions> options) : BackgroundService
{
    private DateTime _lastMemoryNotification = DateTime.MinValue;
    private DateTime _lastCpuNotification = DateTime.MinValue;
    private Thread? _uiThread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _dispatcherReady = new();

    private long _prevIdleTime;
    private long _prevKernelTime;
    private long _prevUserTime;
    private bool _hasPreviousSample;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;

        _uiThread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcherReady.Set();
            Dispatcher.Run();
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();
        _dispatcherReady.Wait();

        // Take initial CPU sample
        GetSystemTimes(out _prevIdleTime, out _prevKernelTime, out _prevUserTime);
        _hasPreviousSample = true;

        logger.LogInformation(
            "ResourceGuard started — memory: {MemThreshold}%, cpu: {CpuThreshold}%, polling: {Interval}s, cooldown: {Cooldown}m",
            config.MemoryThresholdPercent, config.CpuThresholdPercent,
            config.PollingIntervalSeconds, config.CooldownMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckResources(config);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking resources");
            }

            await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), stoppingToken);
        }

        _dispatcher?.InvokeShutdown();
    }

    private int GetCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return -1;

        if (!_hasPreviousSample)
        {
            _prevIdleTime = idleTime;
            _prevKernelTime = kernelTime;
            _prevUserTime = userTime;
            _hasPreviousSample = true;
            return -1;
        }

        var idleDiff = idleTime - _prevIdleTime;
        var kernelDiff = kernelTime - _prevKernelTime;
        var userDiff = userTime - _prevUserTime;
        var totalSystem = kernelDiff + userDiff;

        _prevIdleTime = idleTime;
        _prevKernelTime = kernelTime;
        _prevUserTime = userTime;

        if (totalSystem == 0) return 0;
        return (int)((totalSystem - idleDiff) * 100 / totalSystem);
    }

    private void CheckResources(ResourceGuardOptions config)
    {
        // Memory check
        var memStatus = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        uint memPercent = 0;
        double totalGb = 0, usedGb = 0;
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            memPercent = memStatus.MemoryLoad;
            totalGb = memStatus.TotalPhys / (1024.0 * 1024 * 1024);
            var availGb = memStatus.AvailPhys / (1024.0 * 1024 * 1024);
            usedGb = totalGb - availGb;
        }

        // CPU check
        var cpuPercent = GetCpuUsagePercent();

        var memAlert = memPercent >= config.MemoryThresholdPercent;
        var cpuAlert = cpuPercent >= config.CpuThresholdPercent;

        if (memAlert || cpuAlert)
        {
            logger.LogWarning("CPU: {Cpu}% | Memory: {Mem}% ({Used:F1}/{Total:F1} GB)",
                cpuPercent, memPercent, usedGb, totalGb);
        }
        else
        {
            logger.LogDebug("CPU: {Cpu}% | Memory: {Mem}% ({Used:F1}/{Total:F1} GB)",
                cpuPercent, memPercent, usedGb, totalGb);
        }

        var now = DateTime.Now;
        var cooldown = TimeSpan.FromMinutes(config.CooldownMinutes);

        if (memAlert && now - _lastMemoryNotification >= cooldown)
        {
            SendNotification("Memory", $"⚠ Memory at {memPercent}%",
                $"{usedGb:F1} / {totalGb:F1} GB", Colors.Orange);
            _lastMemoryNotification = now;
        }

        if (cpuAlert && now - _lastCpuNotification >= cooldown)
        {
            SendNotification("CPU", $"🔥 CPU at {cpuPercent}%",
                $"Sustained high usage detected", Colors.OrangeRed);
            _lastCpuNotification = now;
        }
    }

    private void SendNotification(string type, string title, string subtitle, Color accentColor)
    {
        var topProcesses = Process.GetProcesses()
            .OrderByDescending(p => p.WorkingSet64)
            .Take(3)
            .Select(p =>
            {
                var name = p.ProcessName;
                var mb = p.WorkingSet64 / (1024.0 * 1024);
                return $"{name}: {mb:F0} MB";
            })
            .ToList();

        var body = $"{subtitle}\n{string.Join("  •  ", topProcesses)}";
        var context = $"{type}|{title}|{subtitle}|{string.Join("|", topProcesses)}";

        _dispatcher?.BeginInvoke(() => ShowPopup(title, body, accentColor, context));
        System.Media.SystemSounds.Exclamation.Play();
        logger.LogInformation("{Type} notification sent", type);
    }

    private static void ShowPopup(string title, string body, Color accentColor, string context)
    {
        var screen = SystemParameters.WorkArea;

        var window = new Window
        {
            Title = "ResourceGuard",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = Brushes.White,
            Width = 370,
            SizeToContent = SizeToContent.Height,
            Topmost = true,
            ShowInTaskbar = false,
            Left = screen.Right - 380,
            Top = screen.Bottom - 10,
            Opacity = 0
        };

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(accentColor),
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    new TextBlock
                    {
                        Text = body,
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "💡 Click for Copilot recommendations",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                        Margin = new Thickness(0, 8, 0, 0)
                    }
                }
            }
        };

        window.Content = border;
        window.Cursor = System.Windows.Input.Cursors.Hand;

        window.Loaded += (_, _) =>
        {
            window.Top = screen.Bottom - window.ActualHeight - 16;
            window.Opacity = 0.95;
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };

        window.MouseDown += (_, _) =>
        {
            timer.Stop();
            window.Close();
            LaunchCopilot(context);
        };

        window.Show();
        timer.Start();
    }

    private static void LaunchCopilot(string context)
    {
        var parts = context.Split('|');
        var type = parts[0];
        var topProcs = string.Join(", ", parts.Skip(3));

        var prompt = type switch
        {
            "Memory" => $"My Windows system memory is critically high. {parts[1]}. {parts[2]}. " +
                        $"Top processes by memory: {topProcs}. " +
                        "What specific processes should I close or what actions should I take to reduce memory usage?",
            "CPU" => $"My Windows system CPU is critically high. {parts[1]}. {parts[2]}. " +
                     $"Top processes by memory: {topProcs}. " +
                     "What could be causing high CPU and what actions should I take to reduce it?",
            _ => $"My system resources are critically high. {parts[1]}. {parts[2]}. " +
                 $"Top processes: {topProcs}. What should I do?"
        };

        var escaped = prompt.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k copilot -p \"{escaped}\"",
            UseShellExecute = true
        };
        Process.Start(psi);
    }
}

public class ResourceGuardOptions
{
    public int MemoryThresholdPercent { get; set; } = 85;
    public int CpuThresholdPercent { get; set; } = 90;
    public int PollingIntervalSeconds { get; set; } = 30;
    public int CooldownMinutes { get; set; } = 5;
}
