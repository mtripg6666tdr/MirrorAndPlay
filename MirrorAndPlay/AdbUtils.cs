using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace MirrorAndPlay
{
    internal static class AdbUtils
    {
        public static async Task<string> RunCommandWithOutputAsync(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "adb.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;

                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                return output.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ADB Error] {ex.Message}");
                return string.Empty;
            }
        }

        public static async Task<bool> RunCommandAsync(string arguments)
        {
            await RunCommandWithOutputAsync(arguments);
            return true;
        }

        /// <summary>
        /// /proc/net/arp から USBテザリング経由（rndis* / ncm* / usb*）のPCのIPを取得
        /// </summary>
        public static async Task<string?> GetPCHostIpFromArpAsync()
        {
            var arpOutput = await RunCommandWithOutputAsync("shell cat /proc/net/arp");
            if (string.IsNullOrWhiteSpace(arpOutput)) return null;

            var lines = arpOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            // 1行目はヘッダーなのでスキップ (i = 1 から)
            for (var i = 1; i < lines.Length; i++)
            {
                // 空白で分割
                // [0] IP address
                // [1] HW type
                // [2] Flags (0x2 = 有効)
                // [3] HW address (MAC)
                // [4] Mask
                // [5] Device (rndis0, ncm0, etc.)
                var parts = lines[i].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;

                var ip = parts[0];
                var flags = parts[2];
                var device = parts[5].ToLowerInvariant();

                // 有効なARPエントリ(Flags: 0x2) かつ USBテザリング系インターフェースを対象にする
                var isTetheringInterface = device.StartsWith("rndis") ||
                                           device.StartsWith("ncm") ||
                                           device.StartsWith("usb");

                if (flags == "0x2" && isTetheringInterface && IPAddress.TryParse(ip, out _))
                {
                    Debug.WriteLine($"[ADB] ARPからPCのIPを検出: {ip} (Device: {device})");
                    return ip;
                }
            }

            return null;
        }

        public static async Task LaunchVlcStreamAsync(int port)
        {
            var targetIp = await GetPCHostIpFromArpAsync();
            if (string.IsNullOrEmpty(targetIp))
            {
                Debug.WriteLine("[ADB] テザリングインターフェースからPCのIPを取得できませんでした。");
                return;
            }

            var streamUrl = $"http://{targetIp}:{port}/live/";
            Debug.WriteLine($"[ADB] 接続先ストリームURL: {streamUrl}");

            var intentArgs = $"shell am start -a android.intent.action.VIEW " +
                                $"-d \"{streamUrl}\" " +
                                $"-n org.videolan.vlc/org.videolan.vlc.gui.video.VideoPlayerActivity";

            await RunCommandAsync(intentArgs);
            Debug.WriteLine("[ADB] VLC再生コマンドを送信しました！");
        }
    }
}