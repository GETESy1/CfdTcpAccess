using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CfdTcpAccess
{
    /// <summary>负责找到 cloudflared.exe 并读取版本号。</summary>
    public static class CloudflaredLocator
    {
        private static readonly string[] ExeNames = { "cloudflared.exe", "cloudflared-windows-amd64.exe" };

        /// <summary>按优先级探测 cloudflared 可执行文件，找不到返回 null。</summary>
        public static string? Find(string? preferred = null)
        {
            foreach (var candidate in EnumerateCandidates(preferred))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // 非法路径直接跳过。
                }
            }

            return null;
        }

        private static IEnumerable<string?> EnumerateCandidates(string? preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                yield return preferred.Trim().Trim('"');
            }

            var baseDir = AppContext.BaseDirectory;
            foreach (var name in ExeNames)
            {
                yield return Path.Combine(baseDir, name);
                yield return Path.Combine(Environment.CurrentDirectory, name);
            }

            // PATH 环境变量
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in ExeNames)
                {
                    string combined;
                    try
                    {
                        combined = Path.Combine(dir.Trim(), name);
                    }
                    catch
                    {
                        continue;
                    }

                    yield return combined;
                }
            }

            // 常见安装位置
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (var root in new[] { programFiles, programFilesX86, localAppData, userProfile })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                foreach (var name in ExeNames)
                {
                    yield return Path.Combine(root, "cloudflared", name);
                    yield return Path.Combine(root, name);
                }
            }
        }

        /// <summary>调用 --version，返回 "cloudflared version x.y.z ..." 或错误说明。</summary>
        public static string GetVersion(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--version");

                // 显式指定工作目录为 cloudflared 自己所在目录，避免把 cloudflared 可能落下的
                // 附属文件（如空的 config.yml）写进本程序当前所在目录。
                var workDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
                {
                    psi.WorkingDirectory = workDir;
                }

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    return "无法启动该程序。";
                }

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(5000))
                {
                    try { proc.Kill(true); } catch { /* ignore */ }
                    return "读取版本超时。";
                }

                var text = (stdout + " " + stderr).Trim();
                return text.Length == 0 ? $"退出码 {proc.ExitCode}（无输出）" : CollapseWhitespace(text);
            }
            catch (Exception ex)
            {
                return "读取失败：" + ex.Message;
            }
        }

        private static string CollapseWhitespace(string text) =>
            Regex.Replace(text.Replace("\r", " ").Replace("\n", " "), "\\s{2,}", " ").Trim();
    }
}
