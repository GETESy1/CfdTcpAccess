using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CfdTcpAccess
{
    /// <summary>
    /// 纯逻辑自检（不弹窗）：验证参数拼装、端口规划、配置读写、域名校验、cloudflared 定位。
    /// 加 --selftest-live 还会真实拉起一次 cloudflared，验证日志解析与状态机。
    /// 结果写入当前目录的 selftest-report.txt，并以退出码表示成功与否。
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(bool live)
        {
            var sb = new StringBuilder();
            var exitCode = 0;

            try
            {
                var exe = CloudflaredLocator.Find(AppContext.BaseDirectory);
                sb.AppendLine("locator=" + (exe ?? "<null>"));

                if (exe is not null)
                {
                    sb.AppendLine("version=" + CloudflaredLocator.GetVersion(exe));
                }

                var exePath = exe ?? "cloudflared.exe";

                // ---- 命令行拼装 ----
                var basic = new TunnelOptions { CloudflaredPath = exePath, Hostname = "tcp.example.com", LocalHost = "localhost", LocalPort = 5555 };
                sb.AppendLine("args.basic=" + CloudflaredRunner.BuildCommandLine(basic));

                var other = new TunnelOptions
                {
                    CloudflaredPath = exePath,
                    Hostname = "db.example.com",
                    LocalHost = "localhost",
                    LocalPort = 13306,
                };
                sb.AppendLine("args.port13306=" + CloudflaredRunner.BuildCommandLine(other));

                // ---- 端口规划 ----
                sb.AppendLine("ports.assign3=" + string.Join(",", PortPlanner.Assign(3, 5555)));
                sb.AppendLine("ports.skipUsed=" + string.Join(",", PortPlanner.Assign(3, 5555, new[] { 5556 })));

                // ---- 域名清洗 / 校验 ----
                sb.AppendLine("hostname.normalize=" +
                    HostValidator.Normalize(" https://tcp.example.com:443/path ") + "|" +
                    HostValidator.Normalize("tcp.example.com"));
                sb.AppendLine("hostname.valid=" +
                    HostValidator.IsValidHostname("tcp.example.com") + "," +
                    HostValidator.IsValidHostname("bad host") + "," +
                    HostValidator.IsValidHostname("nope") + "," +
                    HostValidator.IsValidHostname("-bad.example.com"));

                // ---- 配置读写（批量映射）----
                var settings = new AppSettings();
                settings.Mappings.Add(new MappingConfig { Hostname = "tcp.example.com", Port = 5555 });
                settings.Mappings.Add(new MappingConfig { Hostname = "ssh.example.com", Port = 5556, Enabled = false });

                var roundTrip = AppSettings.FromJson(settings.ToJson());
                var ok = roundTrip.Mappings.Count == 2
                         && roundTrip.Mappings[0].Hostname == "tcp.example.com"
                         && roundTrip.Mappings[0].Port == 5555
                         && !roundTrip.Mappings[1].Enabled;
                sb.AppendLine("settings.roundtrip=" + ok);
                if (!ok)
                {
                    exitCode = 1;
                }

                // ---- 排错提示 ----
                sb.AppendLine("hint.403=" + CloudflaredRunner.BuildHint("ERR failed to connect: 403 Forbidden"));
                sb.AppendLine("hint.tunnel=" + CloudflaredRunner.BuildHint("ERR tunnel not found"));

                // ---- 可选：真实拉起 cloudflared ----
                if (live)
                {
                    if (exe is null)
                    {
                        sb.AppendLine("live=SKIPPED (找不到 cloudflared)");
                        exitCode = 3;
                    }
                    else
                    {
                        RunLiveTest(sb, exe);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                exitCode = 2;
            }

            try
            {
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest-report.txt"), sb.ToString());
            }
            catch
            {
                // 无写权限时忽略。
            }

            return exitCode;
        }

        /// <summary>无界面布局自检：真实创建并显示一次主窗口（移到屏幕外），导出各控件尺寸。</summary>
        public static int RunUiCheck()
        {
            var sb = new StringBuilder();
            var exitCode = 0;

            try
            {
                using var form = new MainForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-4000, -4000),
                    ShowInTaskbar = false,
                };

                form.Show();
                Application.DoEvents();
                form.SeedForUiCheck();

                for (var i = 0; i < 40; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(25);
                }

                sb.AppendLine(form.BuildUiDiagnostics());
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                exitCode = 2;
            }

            try
            {
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest-ui-report.txt"), sb.ToString());
            }
            catch
            {
                // 无写权限时忽略。
            }

            return exitCode;
        }

        private static void RunLiveTest(StringBuilder sb, string exe)    {
            const string bogusHost = "selftest-nonexistent-abc123.example.com";
            const int port = 5561;

            using var runner = new CloudflaredRunner();
            using var listenerSeen = new ManualResetEventSlim(false);
            using var readySeen = new ManualResetEventSlim(false);

            var lines = new List<string>();
            var startArgs = "";
            var listeningStateMs = -1;

            runner.LogReceived += (_, e) =>
            {
                lock (lines)
                {
                    if (lines.Count < 25)
                    {
                        lines.Add((e.IsError ? "ERR|" : "OUT|") + e.Text);
                    }
                }

                if (e.Text.Contains("Start Websocket listener", StringComparison.OrdinalIgnoreCase))
                {
                    listenerSeen.Set();
                }
            };

            var started = Stopwatch.StartNew();
            runner.StateChanged += (_, e) =>
            {
                if (e.State == TunnelState.Listening)
                {
                    listeningStateMs = (int)started.ElapsedMilliseconds;
                    readySeen.Set();
                }
            };

            var options = new TunnelOptions
            {
                CloudflaredPath = exe,
                Hostname = bogusHost,
                LocalHost = "localhost",
                LocalPort = port,
            };

            startArgs = CloudflaredRunner.BuildCommandLine(options);
            runner.Start(options);

            listenerSeen.Wait(TimeSpan.FromSeconds(20));
            readySeen.Wait(TimeSpan.FromSeconds(5));
            started.Stop();

            sb.AppendLine("live.args=" + startArgs);
            sb.AppendLine("live.sawListenerLine=" + listenerSeen.IsSet);
            sb.AppendLine("live.endpoint=" + (runner.Endpoint ?? "<null>"));
            sb.AppendLine("live.state=" + runner.State + " (Listening at " + listeningStateMs + " ms)");
            sb.AppendLine("live.runningBeforeStop=" + runner.IsRunning);

            // 真实连一下本机监听端口，验证裸 TCP 转发确实在接收连接。
            var accepted = false;
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                accepted = client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(3));
                if (accepted)
                {
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("live.tcpConnectError=" + ex.Message);
            }

            sb.AppendLine("live.localTcpAccepted=" + accepted);

            runner.Stop();
            Thread.Sleep(1200);

            sb.AppendLine("live.runningAfterStop=" + runner.IsRunning);
            sb.AppendLine("live.log:");
            lock (lines)
            {
                foreach (var line in lines)
                {
                    sb.AppendLine("  " + line);
                }
            }
        }
    }
}
