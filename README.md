# CfdTcpAccess — Cloudflare Tunnel TCP 访问助手
方便的转换 Cloudflare Tunnel 的 TCP 协议

---

## 为什么会有这个工具

你是否在与你的好友游玩一些使用TCP协议的游戏时(说的就是某个M开头的游戏)明明已经在服务端配置好了Cloudflare Tunnel却无法连接上的经历，**这就是因为Cloudflare Tunnel 暴露出来的是 Cloudflare 边缘上的一个主机名**，客户端和边缘之间走的是 **WebSocket**（承载在 HTTP/2 或 QUIC 之上），不是裸 TCP。所以原生客户端直连隧道域名一定失败：

```
   原生客户端（mstsc / ssh / 数据库客户端）
        │  ① 连 localhost:5555
        ▼
   cloudflared（本机普通 TCP 监听）          ← 本工具负责拉起、守护、排错
        │  ② 封装成 WebSocket
        ▼
   Cloudflare 边缘 ──► Tunnel ──► 源站 TCP 服务（③ 数据到达）
```

**本工具只是把下面这条命令做成了GUI工具**：

```
cloudflared access tcp --hostname tcp.example.com --url localhost:5555
```

因为 `access tcp` 一次只能服务一个 hostname，所以**映射表里每一行对应一个独立的 cloudflared 进程**

---

## 使用方法

1. 把 `CfdTcpAccess.exe` 和 `cloudflared.exe` 放进同一个文件夹，双击运行 —— 程序启动时会静默定位 cloudflared 并填好路径

   放在别处也没关系：点「浏览…」手选，或把 exe **直接拖进路径框**。填好后版本行会显示 `cloudflared version X.X.X`

2. **填映射表**：
   - 「＋ 添加映射」新增一行，端口自动取下一个空闲端口（从 **5555** 起）
   - 左列填要做 TCP 转换的域名，如 `tcp.example.com`。粘贴 `https://tcp.example.com:443/xxx` 这种也会被自动清洗成纯域名
   - 右列填本地端口

3. **启动**：点「▶ 启动全部」。每行状态变成 `已就绪  localhost:5555` 后即可使用。

   - 启动前会跳过：未勾选启用、已在运行、未填域名、端口与其它行重复、端口已被本机其它程序占用的行 —— 原因都会写进日志
   - 就绪判定：优先解析 cloudflared 自己的日志行 `INF Start Websocket listener host=localhost:5555`；解析不到就用 TCP 连接探测兜底

4. **用原生客户端连 `localhost:该行端口`**（不要连隧道域名）

### 配置的保存 / 导出 / 导入

| 行为 | 位置 |
|---|---|
| 退出程序自动保存 | `CfdTcpAccess.exe` 同目录的 `settings.json`（该目录不可写时回落到 `%AppData%\CfdTcpAccess\settings.json`） |
| 「保存配置到默认位置」 | 同上，随时手动存一次 |
| 「导出配置到文件…」 | 导出成你指定的独立 json，方便备份 / 换机 |
| 「从文件导入配置…」 | 从该 json 恢复；导入前会提示并**先停掉正在运行的隧道**，然后刷新界面并落盘 |

配置内容包括：cloudflared 路径、整张映射表（启用状态 / 域名 / 端口）

---
## 项目结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口；崩溃日志写 `%AppData%\CfdTcpAccess\crash.log`；`--selftest / --selftest-live / --uicheck` 三种自检模式 |
| `MainForm.cs` | 主界面**逻辑**：映射表事件、启动/停止、状态汇总、日志、配置导入导出 |
| `MainForm.Designer.cs` | 界面**搭建**：控件声明 + `InitializeComponent()`（纯代码构建，未用 WinForms 设计器） |
| `MainForm.resx` | 窗体资源文件 |
| `MappingRow.cs` | 映射表一行（启用 / 源域名 / 端口 / 状态），实现 `INotifyPropertyChanged` 让表格实时刷新 |
| `CloudflaredRunner.cs` | 单个 `cloudflared access tcp` 子进程：拼参数、抓 stdout/stderr、解析监听地址判定就绪、退出后按关键词给中文排错提示 |
| `CloudflaredLocator.cs` | 定位 cloudflared（多级查找）与读取版本号 |
| `AppSettings.cs` | 配置模型 `AppSettings` / `MappingConfig` + 端口规划 `PortPlanner` |
| `HostValidator.cs` | 域名清洗（去 `https://`、端口、路径）与格式校验 |
| `SelfTest.cs` | 三种自检的实现 |
| `app.manifest` | 以 `asInvoker` 运行（**UTF-8 无 BOM，首行直接是 `<assembly>`，无 XML 声明**） |
| `ico/app.ico` | 程序图标（Cloudflare 橙底 + 双向箭头：TCP ⇄ WebSocket） |
| `Properties/PublishProfiles/FolderProfile.pubxml` | 单文件发布配置 |
| `CfdTcpAccess.slnx` | 解决方案文件 |
| `cloudflared-windows-amd64.exe` | 随目录放置的 cloudflared（程序会自动认到它） |

---
## 本工具完全由 DeepSeek-V4.1-Flash 制作

