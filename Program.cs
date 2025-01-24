using Newtonsoft.Json;
using PacketDotNet;
using SharpPcap;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Management;
using System.Management.Automation;

namespace DouyinRtmp;

public class Program
{
    private static readonly HashSet<string> _rtmpUrls = new();
    private static string? _server;
    private static string? _code;
    private static readonly object Lock = new();
    private static bool _isCompleted;
    private static readonly CancellationTokenSource _cts = new();
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(3);
    private static IntPtr _winDivertHandle = IntPtr.Zero;

    private static readonly string[] OBS_POSSIBLE_PATHS = new[]
    {
        @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
        @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe",
        @"D:\Program Files\obs-studio\bin\64bit\obs64.exe",
        @"D:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe",
        @"E:\Program Files\obs-studio\bin\64bit\obs64.exe",
        @"E:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe"
    };

    private static readonly string[] SHORTCUT_SEARCH_PATHS = new[]
    {
        @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
        @"C:\Users\Public\Desktop",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
    };

    // 添加系统信息检查
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    private static bool IsLowEndSystem()
    {
        try
        {
            // 检查内存
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                var totalRamGB = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                if (totalRamGB < 8.0)
                {
                    return true;
                }
            }

            // 检查制造商（针对华为电脑）
            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                foreach (var system in searcher.Get())
                {
                    var manufacturer = system["Manufacturer"]?.ToString() ?? "";
                    if (manufacturer.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 如果无法获取制造商信息，忽略这个检查
            }

            return false;
        }
        catch
        {
            // 如果出现任何错误，为安全起见返回 true
            return true;
        }
    }

    [DllImport("lib\\WinDivert.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        short layer,
        short priority,
        ulong flags);

    [DllImport("lib\\WinDivert.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertClose(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ShellExecute(
        IntPtr hwnd,
        string lpOperation,
        string lpFile,
        string lpParameters,
        string lpDirectory,
        int nShowCmd);

    public static async Task Main()
    {
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            CleanupWinDivert();
        };

        try
        {
            Console.WriteLine("抖音直播推流码获取工具 v1.0");
            Console.WriteLine("正在初始化...");

            // 验证授权
            if (!await LicenseManager.ValidateLicense())
            {
                Console.WriteLine("\n授权验证失败，程序将退出。");
                Console.WriteLine("按任意键退出...");
                Console.ReadKey();
                return;
            }

            // 验证成功，继续执行主逻辑
            Console.WriteLine("\n授权验证成功，正在启动主程序...\n");

            var config = LoadConfig();
            if (config == null)
            {
                Console.WriteLine("加载配置失败，按任意键退出...");
                Console.ReadKey();
                return;
            }

            var device = await SelectBestNetworkDevice();
            if (device == null)
            {
                Console.WriteLine("错误: 未找到可用的网络接口");
                Console.WriteLine("按任意键退出...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"\n* 选择网络接口: {device.Description}");
            Console.WriteLine("* 正在等待抖音直播数据...");
            Console.WriteLine("* 请打开抖音直播伴侣...\n");

            await ExtractServerAndCode(device);

            if (string.IsNullOrEmpty(_server) || string.IsNullOrEmpty(_code))
            {
                Console.WriteLine("未能获取到服务器地址或推流码");
                Console.WriteLine("按任意键退出...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"* 服务器: {_server}");
            Console.WriteLine($"* 推流码: {_code}");

            UpdateObsConfigs(_server, _code);
            // 等待配置文件写入完成
            await Task.Delay(1000);

            if (!string.IsNullOrEmpty(config.ObsPath))
            {
                await StartObsAndMonitor(config.ObsPath);
            }
            else
            {
                Console.WriteLine("\n警告: 未配置 OBS 程序路径，跳过启动 OBS");
            }

            Console.WriteLine("\n* 全部操作已完成");
            Console.WriteLine("* 程序将持续运行以阻止 MediaSDK_Server.exe 的网络流量");
            Console.WriteLine("* 按 Ctrl+C 退出程序\n");

            // 保持程序运行
            await Task.Delay(-1);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n操作已取消");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n发生错误: {ex.Message}");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
        }
        finally
        {
            CleanupWinDivert();
            _cts.Dispose();
        }
    }

    private static Config? LoadConfig()
    {
        const string CONFIG_FILE = "config.json";
        try
        {
            Config? config;
            if (!File.Exists(CONFIG_FILE))
            {
                Console.WriteLine("* 未找到配置文件，创建默认配置...");
                config = new Config
                {
                    ObsPath = ""
                };
            }
            else
            {
                var json = File.ReadAllText(CONFIG_FILE);
                config = JsonConvert.DeserializeObject<Config>(json);
                if (config == null)
                {
                    throw new Exception("配置文件格式错误");
                }
            }

            // 检查 OBS 路径
            if (string.IsNullOrEmpty(config.ObsPath) || !File.Exists(config.ObsPath))
            {
                Console.WriteLine("* 正在自动查找 OBS 安装路径...");
                var obsPath = FindObsPath();
                if (obsPath != null)
                {
                    Console.WriteLine($"* 已找到 OBS: {obsPath}");
                    config.ObsPath = obsPath;
                    
                    // 保存更新后的配置
                    var jsonSettings = new JsonSerializerSettings 
                    { 
                        Formatting = Formatting.Indented  // 使用缩进格式，便于阅读
                    };
                    File.WriteAllText(CONFIG_FILE, JsonConvert.SerializeObject(config, jsonSettings));
                    Console.WriteLine("* 已更新配置文件");
                }
                else
                {
                    Console.WriteLine("警告: 未能找到 OBS，请确保已安装或手动配置路径");
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载配置文件失败: {ex.Message}");
            return null;
        }
    }

    private static async Task<ILiveDevice?> SelectBestNetworkDevice()
    {
        var devices = CaptureDeviceList.Instance;
        if (!devices.Any())
        {
            Console.WriteLine("未找到网络接口，请确保已安装 Npcap");
            return null;
        }

        // 获取所有活动的网络接口
        var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                        (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .ToList();

        // 过滤掉虚拟接口
        var filteredInterfaces = activeInterfaces
            .Where(ni => !ni.Description.StartsWith("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !ni.Description.StartsWith("VMware", StringComparison.OrdinalIgnoreCase) &&
                        !ni.Description.StartsWith("VirtualBox", StringComparison.OrdinalIgnoreCase) &&
                        !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 找到匹配的活动设备
        var activeDevices = devices
            .Where(d => filteredInterfaces.Any(ni => 
                d.Description.Contains(ni.Description, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 如果没有找到非虚拟接口，使用所有接口
        if (!activeDevices.Any())
        {
            Console.WriteLine("未找到物理网卡，将使用所有可用接口...");
            activeDevices = devices
                .Where(d => activeInterfaces.Any(ai => 
                    d.Description.Contains(ai.Description, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (!activeDevices.Any())
        {
            Console.WriteLine("未找到活动的网络接口");
            return null;
        }

        // 尝试自动选择最佳网卡
        var bestDevice = activeDevices
            .FirstOrDefault(d => d.Description.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Broadcom", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Atheros", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                                d.Description.Contains("WLAN", StringComparison.OrdinalIgnoreCase));

        if (bestDevice != null)
        {
            Console.WriteLine($"\n* 已自动选择网络接口: {bestDevice.Description}");
            return bestDevice;
        }

        // 如果无法自动选择，显示列表让用户选择
        Console.WriteLine("\n未能自动识别最佳网卡，请手动选择网络接口：");
        for (int i = 0; i < activeDevices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {activeDevices[i].Description}");
        }

        while (true)
        {
            Console.Write("\n请输入序号(1-{0}): ", activeDevices.Count);
            if (int.TryParse(Console.ReadLine(), out int choice) && 
                choice >= 1 && choice <= activeDevices.Count)
            {
                return activeDevices[choice - 1];
            }
            Console.WriteLine("无效的输入，请重试");
        }
    }

    private static async Task ExtractServerAndCode(ILiveDevice device)
    {
        try
        {
            device.Open(DeviceModes.Promiscuous);
            device.Filter = "tcp";
            device.OnPacketArrival += Device_OnPacketArrival;
            
            Console.WriteLine("* 开始捕获网络数据包...");
            Console.WriteLine("* 请在抖音直播伴侣中点击【开始直播】按钮");
            device.StartCapture();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeoutCts.CancelAfter(_timeout);

            // 等待直到获取到所需信息或超时
            while (!_isCompleted && 
                   (string.IsNullOrEmpty(_server) || string.IsNullOrEmpty(_code)))
            {
                await Task.Delay(100, timeoutCts.Token);
            }

            // 如果是低配置系统，额外等待一段时间
            if (IsLowEndSystem())
            {
                Console.WriteLine("\n* 检测到系统配置较低，正在等待系统响应...");
                await Task.Delay(3000); // 等待3秒
            }
        }
        finally
        {
            try
            {
                if (device.Started)
                {
                    device.StopCapture();
                }
                device.Close();
            }
            catch
            {
                // 忽略关闭设备时的错误
            }
        }
    }

    private static void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            var tcpPacket = packet.Extract<TcpPacket>();
            if (tcpPacket?.PayloadData == null || tcpPacket.PayloadData.Length == 0) 
                return;

            var payload = System.Text.Encoding.UTF8.GetString(tcpPacket.PayloadData);
            
            // 调试输出
            if (payload.Contains("rtmp://") || payload.Contains("stream-"))
            {
                Console.WriteLine($"发现相关数据包: {payload}");
            }

            lock (Lock)
            {
                if (_isCompleted) return;  // 如果已经完成，不再处理新的数据包

                // 查找所有 RTMP URL，支持 third 和 thirdgame 路径
                var rtmpMatches = Regex.Matches(payload, @"rtmp://[^""'\s\(\)]+/(?:third|thirdgame)\b");
                foreach (Match match in rtmpMatches)
                {
                    var url = match.Value;
                    // 清理 URL，确保格式正确
                    url = url.Split(new[] { "tcUrl", "swfUrl" }, StringSplitOptions.None)[0].Trim();
                    if (!_rtmpUrls.Contains(url))
                    {
                        _rtmpUrls.Add(url);
                        Console.WriteLine($"* 找到推流地址: {url}");
                    }
                }

                // 查找推流码
                if (string.IsNullOrEmpty(_code))
                {
                    var streamMatches = Regex.Matches(payload, @"stream-[^""'\s\(\)\}]+");
                    if (streamMatches.Count > 0)
                    {
                        _code = streamMatches[0].Value;
                        Console.WriteLine($"* 找到推流码: {_code}");
                    }
                }

                // 如果已经有了服务器和推流码，标记为完成
                if (!string.IsNullOrEmpty(_server) && !string.IsNullOrEmpty(_code))
                {
                    return;  // 已经有了信息，不需要再处理
                }

                // 如果只有一个 RTMP URL，直接使用它
                if (_rtmpUrls.Count == 1 && !string.IsNullOrEmpty(_code))
                {
                    _server = _rtmpUrls.First();
                    _isCompleted = true;
                    Console.WriteLine("\n* 成功获取推流信息！");
                }
                // 如果有多个 RTMP URL，让用户选择
                else if (_rtmpUrls.Count > 1 && !string.IsNullOrEmpty(_code))
                {
                    Console.WriteLine("\n* 发现多个推流地址，请手动选择并修改配置：");
                    int index = 1;
                    foreach (var url in _rtmpUrls)
                    {
                        Console.WriteLine($"{index++}. {url}");
                    }
                    // 默认使用第一个地址
                    _server = _rtmpUrls.First();
                    _isCompleted = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理数据包时出错: {ex.Message}");
        }
    }

    private static async Task StartBlockingMediaSDK()
    {
        try
        {
            // 先结束现有的 MediaSDK_Server 进程
            var processes = Process.GetProcessesByName("MediaSDK_Server");
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    Console.WriteLine("* 已终止现有的 MediaSDK_Server 进程");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"终止进程失败: {ex.Message}");
                }
            }

            // 等待进程重新启动
            Console.WriteLine("* 等待 MediaSDK_Server 重新启动...");
            await Task.Delay(2000); // 等待2秒，让进程有时间重启

            // 创建过滤规则，只针对 MediaSDK_Server 的 RTMP 流量
            string filter = "process.name == \"MediaSDK_Server.exe\" and " +
                          "outbound and " +  // 只过滤出站流量
                          "(" +
                          $"tcp.DstPort == 1935 or " +  // 标准 RTMP 端口
                          $"tcp.DstPort == 443 or " +   // RTMPS 端口
                          $"tcp.DstPort == 80" +        // 可能的 HTTP 端口
                          ")";

            // WINDIVERT_FLAG_SNIFF (1) | WINDIVERT_FLAG_DROP (2) = 3
            _winDivertHandle = WinDivertOpen(filter, 0, -1000, 3); 

            if (_winDivertHandle == IntPtr.Zero)
            {
                Console.WriteLine("警告: 无法创建网络过滤器，请确保以管理员权限运行");
                return;
            }

            Console.WriteLine("* 已开始阻止 MediaSDK_Server.exe 的推流流量");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动网络过滤时出错: {ex.Message}");
        }
    }

    private static void CleanupWinDivert()
    {
        if (_winDivertHandle != IntPtr.Zero)
        {
            WinDivertClose(_winDivertHandle);
            _winDivertHandle = IntPtr.Zero;
            Console.WriteLine("\n* 已停止阻止 MediaSDK_Server.exe 的网络流量");
        }
    }

    private static string? FindObsPath()
    {
        // 1. 首先检查常见安装路径
        foreach (var path in OBS_POSSIBLE_PATHS)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 2. 搜索快捷方式
        foreach (var searchPath in SHORTCUT_SEARCH_PATHS)
        {
            if (!Directory.Exists(searchPath)) continue;

            try
            {
                var files = Directory.GetFiles(searchPath, "*.lnk", SearchOption.AllDirectories)
                    .Where(f => f.Contains("obs", StringComparison.OrdinalIgnoreCase) || 
                               f.Contains("studio", StringComparison.OrdinalIgnoreCase));

                foreach (var shortcut in files)
                {
                    try
                    {
                        // 尝试直接通过快捷方式找到目标文件
                        var shortcutTarget = ResolveShortcutTarget(shortcut);
                        if (!string.IsNullOrEmpty(shortcutTarget) && 
                            File.Exists(shortcutTarget) && 
                            shortcutTarget.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            return shortcutTarget;
                        }
                    }
                    catch
                    {
                        // 忽略单个快捷方式的解析错误
                        continue;
                    }
                }
            }
            catch
            {
                // 忽略单个目录的搜索错误
                continue;
            }
        }

        // 3. 搜索所有可用磁盘
        try
        {
            Console.WriteLine("* 正在深度搜索系统中的 OBS...");
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                Console.WriteLine($"* 正在搜索磁盘 {drive.Name}...");
                var searchPatterns = new[] { "obs64.exe", "obs-studio" };

                foreach (var pattern in searchPatterns)
                {
                    try
                    {
                        var files = Directory.GetFiles(drive.RootDirectory.FullName, pattern, 
                            new System.IO.EnumerationOptions 
                            { 
                                IgnoreInaccessible = true,
                                RecurseSubdirectories = true,
                                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                            });

                        foreach (var file in files)
                        {
                            if (file.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"* 在 {file} 找到 OBS");
                                return file;
                            }
                            else if (file.Contains("obs-studio", StringComparison.OrdinalIgnoreCase))
                            {
                                // 如果找到 obs-studio 目录，检查其中的 bin\64bit\obs64.exe
                                var obsExePath = Path.Combine(Path.GetDirectoryName(file)!, "bin", "64bit", "obs64.exe");
                                if (File.Exists(obsExePath))
                                {
                                    Console.WriteLine($"* 在 {obsExePath} 找到 OBS");
                                    return obsExePath;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 忽略单个搜索错误
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"搜索系统时出错: {ex.Message}");
        }

        Console.WriteLine("\n未能找到 OBS Studio，请确认是否已安装？");
        Console.WriteLine("您可以从 https://obsproject.com/ 下载安装");
        return null;
    }

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            // 使用 PowerShell 命令解析快捷方式
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"(New-Object -ComObject WScript.Shell).CreateShortcut('{shortcutPath}').TargetPath\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task StartObsAndMonitor(string? configuredObsPath)
    {
        string? obsPath = configuredObsPath;
        
        // 如果配置的路径无效，尝试自动查找
        if (string.IsNullOrEmpty(obsPath) || !File.Exists(obsPath))
        {
            Console.WriteLine("* 正在自动查找 OBS 安装路径...");
            obsPath = FindObsPath();
            
            if (obsPath == null)
            {
                Console.WriteLine("错误: 未能找到 OBS 安装路径");
                Console.WriteLine("请确保已安装 OBS Studio，或在配置文件中指定正确的路径");
                return;
            }
            else
            {
                Console.WriteLine($"* 已找到 OBS: {obsPath}");
            }
        }

        string? obsDirectory = Path.GetDirectoryName(obsPath);
        if (string.IsNullOrEmpty(obsDirectory))
        {
            Console.WriteLine($"错误: 无法获取 OBS 程序目录: {obsPath}");
            return;
        }

        var obsProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = obsPath,
                UseShellExecute = true,
                WorkingDirectory = obsDirectory,
                Arguments = "--startstreaming"
            }
        };

        try
        {
            obsProcess.Start();
            Console.WriteLine("正在等待 OBS 启动...");
            Console.WriteLine("* OBS 将在启动后自动开始推流");

            // 等待 OBS 进程完全启动
            bool obsStarted = false;
            for (int i = 0; i < 30 && !obsStarted; i++)
            {
                var obsProcesses = Process.GetProcessesByName("obs64");
                if (obsProcesses.Length > 0)
                {
                    obsStarted = true;
                    Console.WriteLine("* OBS 已成功启动，准备处理 MediaSDK_Server");

                    if (IsLowEndSystem())
                    {
                        Console.WriteLine("* 正在等待系统响应...");
                        await Task.Delay(3000);
                    }

                    await StartBlockingMediaSDK();
                    break;
                }
                await Task.Delay(1000);
            }

            if (!obsStarted)
            {
                Console.WriteLine("等待 OBS 启动超时");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动 OBS 失败: {ex.Message}");
        }
    }

    private class ObsConfig
    {
        public string type { get; set; } = "rtmp_custom";
        public ObsSettings settings { get; set; } = new();
    }

    private class ObsSettings
    {
        public bool bwtest { get; set; } = false;
        public string key { get; set; } = string.Empty;
        public string server { get; set; } = string.Empty;
        public bool use_auth { get; set; } = false;
    }

    private static List<string> FindAllServiceJsonFiles()
    {
        var result = new List<string>();
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var obsProfilesPath = Path.Combine(appDataPath, "obs-studio", "basic", "profiles");
        
        if (!Directory.Exists(obsProfilesPath))
            return result;

        foreach (var dir in Directory.GetDirectories(obsProfilesPath))
        {
            var serviceJsonPath = Path.Combine(dir, "service.json");
            if (File.Exists(serviceJsonPath))
            {
                result.Add(serviceJsonPath);
            }
        }

        return result;
    }

    private static void UpdateObsConfigs(string server, string key)
    {
        var configFiles = FindAllServiceJsonFiles();
        if (configFiles.Count == 0)
        {
            Console.WriteLine("未找到 OBS 配置文件！");
            return;
        }

        foreach (var configFile in configFiles)
        {
            try
            {
                var json = File.ReadAllText(configFile);
                var config = JsonConvert.DeserializeObject<ObsConfig>(json);
                
                // 检查是否为旧版格式（通过检查 JSON 结构）
                var isOldVersion = json.Contains("\"type\":\"rtmp_custom\"") && 
                                 !json.Contains("\"settings\":{\"server\"");

                if (config == null)
                {
                    config = new ObsConfig();
                }

                config.settings.server = server;
                config.settings.key = key;
                config.settings.bwtest = false;
                config.settings.use_auth = false;

                var newJson = JsonConvert.SerializeObject(config, Formatting.Indented);
                
                // 如果是旧版格式，调整 JSON 结构
                if (isOldVersion)
                {
                    var oldFormatObj = new
                    {
                        settings = new
                        {
                            bwtest = false,
                            key = key,
                            server = server,
                            use_auth = false
                        },
                        type = "rtmp_custom"
                    };
                    newJson = JsonConvert.SerializeObject(oldFormatObj, Formatting.Indented);
                }

                File.WriteAllText(configFile, newJson);
                Console.WriteLine($"已更新 OBS 配置文件: {configFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新配置文件 {configFile} 失败: {ex.Message}");
            }
        }
    }
} 