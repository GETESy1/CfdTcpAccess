using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CfdTcpAccess
{
    internal static class Program
    {
        private static readonly string CrashLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CfdTcpAccess", "crash.log");

        [STAThread]
        private static int Main(string[] args)
        {
            InstallCrashLogging();

            // 自检模式（不弹界面）：
            //   --selftest       参数拼装 / 端口规划 / 配置读写 / 域名校验
            //   --selftest-live  额外真实拉起一次 cloudflared，验证日志解析、状态机、本机 TCP 监听
            //   --uicheck        屏幕外创建主窗口并导出控件尺寸，检查布局是否被压扁或横向溢出
            if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("--selftest-live", StringComparison.OrdinalIgnoreCase)))
            {
                var live = args.Any(a => a.Equals("--selftest-live", StringComparison.OrdinalIgnoreCase));
                return SelfTest.Run(live);
            }

            ApplicationConfiguration.Initialize();

            if (args.Any(a => a.Equals("--uicheck", StringComparison.OrdinalIgnoreCase)))
            {
                return SelfTest.RunUiCheck();
            }

            Application.Run(new MainForm());
            return 0;
        }

        private static void InstallCrashLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            Application.ThreadException += (_, e) => WriteCrash("WinForms ThreadException", e.Exception);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }

        private static void WriteCrash(string source, Exception? ex)
        {
            var text = new StringBuilder()
                .AppendLine("==================== 崩溃 ====================")
                .AppendLine($"时间    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"来源    : {source}")
                .AppendLine($"命令行  : {Environment.CommandLine}")
                .AppendLine($"异常    : {ex?.GetType().FullName ?? "(非 Exception 对象)"}")
                .AppendLine($"消息    : {ex?.Message}")
                .AppendLine(ex?.StackTrace)
                .ToString();

            try
            {
                var dir = Path.GetDirectoryName(CrashLog);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(CrashLog, text, new UTF8Encoding(false));
            }
            catch
            {
                // 连崩溃日志都写不出去时只能放弃。
            }

            try
            {
                MessageBox.Show(
                    "程序发生未处理的异常，详情已记录到：\n" + CrashLog + "\n\n" + ex,
                    "Cloudflare Tunnel TCP Access 助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // 弹不出提示框时忽略。
            }
        }
    }
}
