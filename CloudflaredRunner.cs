using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CfdTcpAccess
{
    public enum TunnelState
    {
        Idle,
        Starting,
        Listening,
        Stopped,
        Failed,
    }

    public sealed class TunnelOptions
    {
        public string CloudflaredPath { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string LocalHost { get; set; } = "localhost";
        public int LocalPort { get; set; } = 5555;
    }

    public sealed class LogLineEventArgs : EventArgs
    {
        public string Text { get; init; } = "";
        public bool IsError { get; init; }
    }

    public sealed class StateChangedEventArgs : EventArgs
    {
        public TunnelState State { get; init; }
        public string Message { get; init; } = "";
        public string? Endpoint { get; init; }
        public string? Hint { get; init; }
    }

    /// <summary>
    /// 包装 "cloudflared access tcp" 子进程：拼参数、抓日志、判断监听就绪、退出排错。
    /// </summary>
    public sealed class CloudflaredRunner : IDisposable
    {
        private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);

        // cloudflared 实际输出示例：
        //   2026-10-05T18:51:05Z INF Start Websocket listener host=localhost:5555
        // 老版本可能是 "Start listening on 127.0.0.1:5555"。
        private static readonly Regex ListenRegex = new(
            @"(?:(?:Start|Started)\s+Websocket\s+listener\s+(?:host|addr)=|Start listening on|listening on)\s*(?<addr>(?:[0-9]{1,3}\.){3}[0-9]{1,3}:\d{1,5}|\[[0-9a-fA-F:]+\]:\d{1,5}|[A-Za-z0-9][A-Za-z0-9\.\-]*:\d{1,5})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly object _sync = new();
        private readonly StringBuilder _tail = new();

        private Process? _proc;
        private CancellationTokenSource? _probeCts;
        private volatile bool _stopRequested;

        public TunnelState State { get; private set; } = TunnelState.Idle;
        public string? Endpoint { get; private set; }
        public TunnelOptions? Options { get; private set; }

        public event EventHandler<LogLineEventArgs>? LogReceived;
        public event EventHandler<StateChangedEventArgs>? StateChanged;

        public bool IsRunning
        {
            get
            {
                var p = _proc;
                if (p is null)
                {
                    return false;
                }

                try
                {
                    return !p.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>构造展示用的命令行文本。</summary>
        public static string BuildCommandLine(TunnelOptions o) =>
            string.Join(" ", BuildArguments(o).Select(QuoteIfNeeded));

        /// <summary>
        /// 构造传给 cloudflared 的参数列表（用 ArgumentList，避免引号/转义问题）。
        /// 结果就是：access tcp --hostname &lt;域名&gt; --url localhost:&lt;端口&gt;
        /// </summary>
        public static IReadOnlyList<string> BuildArguments(TunnelOptions o)
        {
            var args = new List<string> { "access", "tcp" };

            args.Add("--hostname");
            args.Add(o.Hostname);

            args.Add("--url");
            args.Add($"{o.LocalHost}:{o.LocalPort}");

            return args;
        }

        public static string QuoteIfNeeded(string value) =>
            value.Length > 0 && value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;

        public void Start(TunnelOptions options)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("cloudflared 已经在运行中，请先停止。");
            }

            Options = options;
            Endpoint = null;
            _stopRequested = false;
            lock (_sync)
            {
                _tail.Clear();
            }

            var psi = new ProcessStartInfo
            {
                FileName = options.CloudflaredPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var workDir = Path.GetDirectoryName(options.CloudflaredPath);
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            {
                psi.WorkingDirectory = workDir;
            }

            foreach (var arg in BuildArguments(options))
            {
                psi.ArgumentList.Add(arg);
            }

            Emit($"> {QuoteIfNeeded(options.CloudflaredPath)} {BuildCommandLine(options)}", false);
            SetState(TunnelState.Starting, "正在启动 cloudflared…", null, null);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnLine(e.Data, false);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
            proc.Exited += (_, _) => OnExited(proc);

            _proc = proc;
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            StartReadinessProbe(options);
        }

        public void Stop()
        {
            var proc = _proc;
            if (proc is null)
            {
                return;
            }

            _stopRequested = true;
            try
            {
                _probeCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Emit("停止 cloudflared 时出错：" + ex.Message, true);
            }
            finally
            {
                // 始终在这里释放，避免与 Exited 回调互相争抢同一个 Process 实例。
                try
                {
                    proc.Dispose();
                }
                catch
                {
                    // ignore
                }

                if (ReferenceEquals(_proc, proc))
                {
                    _proc = null;
                }
            }

            // Exited 回调可能因为 _proc 已被清空而被跳过，这里兜底把状态落到“已停止”。
            SetState(TunnelState.Stopped, "已停止。", null, null);
        }

        private void StartReadinessProbe(TunnelOptions options)
        {
            try
            {
                _probeCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            var cts = new CancellationTokenSource();
            _probeCts = cts;

            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline && IsRunning)
                {
                    // 日志里已经报出监听地址就不用探测了。
                    if (Endpoint is not null)
                    {
                        return;
                    }

                    try
                    {
                        using var client = new TcpClient();
                        await client.ConnectAsync(ProbeHost(options.LocalHost), options.LocalPort, cts.Token);
                        Endpoint ??= $"{options.LocalHost}:{options.LocalPort}";
                        SetState(TunnelState.Listening,
                            "本地监听已就绪，可连接该地址。", Endpoint, null);
                        return;
                    }
                    catch
                    {
                        // 还没起来，稍后重试。
                    }

                    try
                    {
                        await Task.Delay(400, cts.Token);
                    }
                    catch
                    {
                        return;
                    }
                }
            }, cts.Token);
        }

        private static string ProbeHost(string localHost) =>
            localHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : localHost;

        private void MarkListening(string endpoint)
        {
            Endpoint = endpoint;
            if (State is TunnelState.Starting or TunnelState.Idle)
            {
                SetState(TunnelState.Listening, "本地监听已就绪，可连接该地址。", endpoint, null);
            }
        }

        private void OnLine(string? data, bool isError)
        {
            if (data is null)
            {
                return;
            }

            var text = AnsiRegex.Replace(data, "").TrimEnd();
            if (text.Length == 0)
            {
                return;
            }

            lock (_sync)
            {
                _tail.AppendLine(text);
                if (_tail.Length > 20000)
                {
                    _tail.Remove(0, _tail.Length - 15000);
                }
            }

            var match = ListenRegex.Match(text);
            if (match.Success)
            {
                MarkListening(match.Groups["addr"].Value.Trim());
            }

            Emit(text, isError);
        }

        private void OnExited(Process proc)
        {
            if (!ReferenceEquals(_proc, proc))
            {
                return;
            }

            int exitCode;
            try
            {
                exitCode = proc.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }

            var stoppedByUser = _stopRequested;
            _stopRequested = false;

            try
            {
                _probeCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            if (stoppedByUser)
            {
                SetState(TunnelState.Stopped, "已停止。", null, null);
            }
            else
            {
                string log;
                lock (_sync)
                {
                    log = _tail.ToString();
                }

                var hint = BuildHint(log);
                SetState(TunnelState.Failed, $"cloudflared 已退出（退出码 {exitCode}）。", null, hint);
            }

            // 不在这里 Dispose/清空 _proc：Stop() 或 Dispose() 可能正在使用该实例，统一由它们释放。
        }

        /// <summary>根据日志内容给出中文排错提示。</summary>
        public static string? BuildHint(string log)
        {
            if (string.IsNullOrWhiteSpace(log))
            {
                return null;
            }

            foreach (var (keyword, hint) in Hints)
            {
                if (log.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return hint;
                }
            }

            // 通用兜底
            if (log.Contains("ERR", StringComparison.Ordinal))
            {
                return "cloudflared 报告了错误，请查看上方 ERR 行；常见原因是域名写错、隧道未运行或 Access 策略未放行。";
            }

            return "cloudflared 意外退出且没有明显错误行，请用“详细日志”模式重试以获取更多信息。";
        }

        private static readonly (string Keyword, string Hint)[] Hints =
        {
            ("403", "被 Cloudflare Access 拒绝（403）。请确认该域名已在 Access 应用策略中放行你的身份，或在浏览器完成一次性登录（首次运行会自动打开浏览器）。"),
            ("Unauthorized", "未授权。请先在同一台机器执行 `cloudflared tunnel login` 完成浏览器授权后重试。"),
            ("login", "需要登录。cloudflared 会弹出浏览器要求授权，请完成授权后重试。"),
            ("tunnel not found", "找不到该隧道。域名对应的 Tunnel 可能已被删除，或 DNS 记录没有指向 tunnel。"),
            ("no such host", "域名无法解析。请检查域名拼写，以及该主机名的 DNS CNAME 是否已指向 <TUNNEL-ID>.cfargotunnel.com。"),
            ("failed to connect to origin", "边缘能连上隧道，但隧道连不上源站。请检查源站 IP/端口是否可从运行 cloudflared 的那台机器访问。"),
            ("connection refused", "连接被拒绝：本地端口可能已被占用，或对端服务未监听。"),
            ("address already in use", "本地监听端口已被占用，请换一个端口。"),
            ("context deadline exceeded", "连接超时。请检查本机网络/代理是否能访问 Cloudflare 边缘（region1.v2.argotunnel.com:7844 等）。"),
            ("websocket", "WebSocket 握手失败，通常与网络代理、TLS 拦截或 Access 策略有关。"),
            ("cert.pem", "缺少 cloudflared 授权文件（%USERPROFILE%\\.cloudflared\\cert.pem），请先执行 `cloudflared tunnel login`。"),
        };

        private void Emit(string text, bool isError) =>
            LogReceived?.Invoke(this, new LogLineEventArgs { Text = text, IsError = isError });

        private void SetState(TunnelState state, string message, string? endpoint, string? hint)
        {
            State = state;
            StateChanged?.Invoke(this, new StateChangedEventArgs
            {
                State = state,
                Message = message,
                Endpoint = endpoint,
                Hint = hint,
            });
        }

        public void Dispose()
        {
            Stop();
            try
            {
                _probeCts?.Dispose();
            }
            catch
            {
                // ignore
            }

            _probeCts = null;
            _proc = null;
        }
    }
}
