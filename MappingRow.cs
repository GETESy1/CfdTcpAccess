using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CfdTcpAccess
{
    /// <summary>
    /// 映射表里的一行：左边源域名，右边转换后的本地 TCP 端口。
    /// 实现 INotifyPropertyChanged 以便 DataGridView 实时刷新状态。
    /// </summary>
    public sealed class MappingRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _hostname = "";
        private int _port = PortPlanner.DefaultStartPort;
        private string _status = "未启动";

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        /// <summary>源域名，对应 --hostname。</summary>
        public string Hostname
        {
            get => _hostname;
            set => Set(ref _hostname, value ?? "");
        }

        /// <summary>转换后的本地 TCP 端口，对应 --url localhost:&lt;端口&gt;。</summary>
        public int Port
        {
            get => _port;
            set
            {
                if (Set(ref _port, value))
                {
                    OnPropertyChanged(nameof(LocalAddress));
                }
            }
        }

        public string Status
        {
            get => _status;
            set => Set(ref _status, value ?? "");
        }

        [Browsable(false)]
        public string LocalAddress => $"localhost:{_port}";

        [Browsable(false)]
        public CloudflaredRunner? Runner { get; set; }

        [Browsable(false)]
        public bool IsRunning => Runner?.IsRunning == true;

        public MappingConfig ToConfig() => new() { Enabled = Enabled, Hostname = Hostname, Port = Port };

        public static MappingRow FromConfig(MappingConfig config) => new()
        {
            Enabled = config.Enabled,
            Hostname = config.Hostname ?? "",
            Port = config.Port is >= 1 and <= 65535 ? config.Port : PortPlanner.DefaultStartPort,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
