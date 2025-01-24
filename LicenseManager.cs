using System.Text.Json;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;

namespace DouyinRtmp;

public class LicenseManager
{
    private const string LICENSE_FILE = "config.ini";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string lpDefault,
        StringBuilder lpReturnedString,
        uint nSize,
        string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WritePrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string lpString,
        string lpFileName);

    public class LicenseResponse
    {
        public string code { get; set; } = string.Empty;
        public string msg { get; set; } = string.Empty;
        public string? expiry { get; set; }
        public int? remainingDays { get; set; }
    }

    private static void EnsureConfigFile()
    {
        var fullPath = Path.GetFullPath(LICENSE_FILE);
        Console.WriteLine($"检查配置文件: {fullPath}");

        if (!File.Exists(fullPath))
        {
            Console.WriteLine("配置文件不存在，正在创建...");
            try
            {
                // 使用Windows API写入配置
                WritePrivateProfileString("License", "AuthUrl", 
                    "http://127.0.0.1:3000/api/v1/app/key?mode=Ui5OjtoI02MY8g2-kIaL", 
                    fullPath);

                Console.WriteLine($"配置文件已创建: {fullPath}");
                // 等待文件系统
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"创建配置文件时出错: {ex.Message}");
            }
        }
    }

    private static string ReadIniValue(string section, string key, string defaultValue = "")
    {
        EnsureConfigFile();
        var fullPath = Path.GetFullPath(LICENSE_FILE);

        StringBuilder sb = new StringBuilder(500);
        uint result = GetPrivateProfileString(section, key, defaultValue, sb, (uint)sb.Capacity, fullPath);
        
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Exception("读取配置文件失败");
        }

        return sb.ToString();
    }

    public static async Task<bool> ValidateLicense()
    {
        while (true)
        {
            try
            {
                string authUrl = ReadIniValue("License", "AuthUrl");
                if (string.IsNullOrEmpty(authUrl))
                {
                    ShowNotification("授权验证错误", "配置文件格式错误");
                    return false;
                }

                using var client = new HttpClient();
                var response = await client.GetAsync(authUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                        response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
                        response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
                    {
                        ShowNotification("授权验证失败", "服务器正在维护中，深表歉意，稍后会给予补偿");
                    }
                    else
                    {
                        ShowNotification("授权验证失败", "无法连接到授权服务器");
                    }
                    return false;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var licenseResponse = JsonSerializer.Deserialize<LicenseResponse>(jsonContent);

                if (licenseResponse == null)
                {
                    ShowNotification("授权验证失败", "授权服务响应异常");
                    return false;
                }

                bool isValid = licenseResponse.code.ToLower() == "true";

                if (isValid)
                {
                    string notificationMessage = $"{licenseResponse.msg} 可用天数{licenseResponse.remainingDays}天";
                    ShowNotification("授权验证", notificationMessage);
                    return true;
                }
                else
                {
                    ShowNotification("授权验证失败", licenseResponse.msg);
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                ShowNotification("授权验证失败", "服务器正在维护中，深表歉意，稍后会给予补偿");
                return false;
            }
            catch (Exception)
            {
                ShowNotification("授权验证失败", "验证过程出现异常");
                return false;
            }
        }
    }

    private static void ShowNotification(string title, string message)
    {
        try
        {
            if (Application.OpenForms.Count == 0)
            {
                Thread thread = new Thread(() =>
                {
                    using var notifyIcon = new NotifyIcon
                    {
                        Visible = true,
                        Icon = SystemIcons.Information
                    };

                    notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
                    Thread.Sleep(3000);
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            else
            {
                var notifyIcon = new NotifyIcon
                {
                    Visible = true,
                    Icon = SystemIcons.Information
                };
                notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
                Thread.Sleep(3000);
                notifyIcon.Dispose();
            }
        }
        catch
        {
            // 通知失败时，不显示任何信息
        }
    }
} 