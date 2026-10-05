using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CfdTcpAccess
{
    /// <summary>一条映射：源域名 --hostname &lt;域名&gt; → 本地 --url localhost:&lt;端口&gt;。</summary>
    public sealed class MappingConfig
    {
        public bool Enabled { get; set; } = true;
        public string Hostname { get; set; } = "";
        public int Port { get; set; } = PortPlanner.DefaultStartPort;
    }

    /// <summary>批量端口规划：从起始端口起依次分配未被占用的端口。</summary>
    public static class PortPlanner
    {
        public const int DefaultStartPort = 5555;

        public static List<int> Assign(int count, int startPort, IEnumerable<int>? reserved = null)
        {
            var used = new HashSet<int>(reserved ?? Enumerable.Empty<int>());
            var result = new List<int>();
            var port = Math.Clamp(startPort, 1, 65535);

            while (result.Count < count && port <= 65535)
            {
                if (used.Add(port))
                {
                    result.Add(port);
                }

                port++;
            }

            return result;
        }
    }

    /// <summary>
    /// 保存在 exe 同目录 settings.json 中的用户配置（若目录不可写则回落到 %APPDATA%）。
    /// </summary>
    public sealed class AppSettings
    {
        public string CloudflaredPath { get; set; } = "";

        /// <summary>域名 → 本地端口 的批量映射表。</summary>
        public List<MappingConfig> Mappings { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static string PrimarySettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

        public static string FallbackSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CfdTcpAccess", "settings.json");

        /// <summary>读取配置；任何异常都退化为默认值，不让 GUI 起不来。</summary>
        public static AppSettings Load(out string path)
        {
            foreach (var candidate in new[] { PrimarySettingsPath, FallbackSettingsPath })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        var json = File.ReadAllText(candidate);
                        var loaded = FromJson(json);
                        path = candidate;
                        return loaded;
                    }
                }
                catch
                {
                    // 忽略损坏的配置文件，继续尝试下一个位置。
                }
            }

            path = PrimarySettingsPath;
            return new AppSettings();
        }

        public void Save()
        {
            var json = ToJson();
            foreach (var target in new[] { PrimarySettingsPath, FallbackSettingsPath })
            {
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(target, json);
                    return;
                }
                catch
                {
                    // 尝试下一个可写位置。
                }
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static AppSettings FromJson(string json) =>
            JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }
}
