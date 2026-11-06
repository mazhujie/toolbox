# Toolbox

Windows 网络工具箱、串口助手、TCP/UDP 调试助手与 ADB 工具，使用 C# / WinForms 和 .NET Framework 4.8。
当前版本：3.3.0。[下载完整便携 ZIP](https://github.com/mazhujie/toolbox/releases/latest)，解压后运行 `Toolbox.exe`。
便携包包含官方 Android Platform Tools；实时抓包需单独安装 Npcap。

## 功能

- 下载测速：国内/海外测速源，直连、Windows 系统代理和手动 HTTP 代理。
- 出口 IP：并排查询直连与代理出口，默认系统代理；风险分仅取自 Scamalytics。
- 串口助手：参数设置、文本/HEX 收发、定时发送、接收日志及多条勾选发送。
- 网络调试：局域网 Ping 扫描及延迟点阵、TCP/UDP 收发、实时抓包与 PCAP 保存。
- ADB 工具：独立大类，包含 Shell 命令和文件传输两页，共用设备选择与 ADB 路径。

串口连接设置与收发位于同一页，高级参数可折叠。切换到网络页不会断开串口。
“清空记录”同时重置日志、待显示的接收数据和 RX/TX 累计字节数；后续收发从零计数，连接与发送任务继续运行。
网络操作按钮位于左侧，IP 查询的连接方式与下载测速独立。

## ADB 工具

首次点击“刷新设备”，选择在线且已授权的设备。支持 USB 设备和已开启网络调试的设备；
网络连接填写 `IP:端口`，需要无线配对的设备应先按 Android 官方流程完成配对。
多设备使用明确的 transport ID（不可用时使用序列号）定位，任务运行时锁定设备选择。
所选设备消失后不会自动切到另一台；未授权和离线设备不能执行命令或传输。

- **Shell 命令**：支持多行命令、Ctrl+Enter 执行、实时输出、错误及退出码、停止、输出保存和最近 30 条命令历史。
  每次执行创建独立 shell，`cd` 等状态不跨次保留。需要 `top` / `vi` 或持续会话时，点击“打开交互终端”。
- **文件传输**：本地文件/目录推送到设备，设备文件/目录拉取到指定本地目录；支持浏览和拖入本地来源。
  设备路径需以 `/` 开头，传输日志显示 ADB 原始进度与结果。同名目标遵循 ADB 的覆盖规则，取消可能留下部分文件。
- **板端浏览**：点击“设备目标”或“设备来源”旁的“浏览…”，查看所选设备的目录。
  目录排在文件前面并显示类型；支持双击进入、上一级、根目录、隐藏项和当前目录名称筛选。
  地址栏回车前往，补全候选来自当前已读取目录。推送选择接收目录；拉取可选择文件或整个目录。
  “使用当前目录”可选择空目录；选择只填入路径，点击传输按钮后才开始收发。
- 两页共用一个内置任务，切页继续运行，退出时停止并等待本工具启动的 ADB 客户端结束。
  不执行 `kill-server`；停止客户端不保证设备端进程结束，独立交互终端需自行退出。
- 输出仅保留最近约 256K 字符，命令历史与设备列表仅存在当前窗口内，不自动写盘。
  ADB 路径在成功刷新后保存到 `%LOCALAPPDATA%\Toolbox\adb-path.txt`。

板端浏览使用所选设备的普通 Shell 权限，只读取一层目录，适用于提供兼容 POSIX Shell 的 Android/Linux 板卡。
每次最多列出前 1,000 项，超出或含不支持名称时明确提示；筛选和补全只覆盖已列出的项。
目录权限不足、断连或输出不完整时会报错并禁用选择，不会显示成空目录；关闭浏览窗口会取消读取。
有效链接区分为目录链接和文件链接，无效链接与特殊文件不可选择。含控制字符或无法按 UTF-8 解码的名称不支持选择。

### 新电脑没有 ADB

Release 便携 ZIP 已附带 Platform Tools 37.0.1，完整解压即可使用 ADB。
仅运行自行编译的 EXE 时，从 [Android 官方下载 Platform Tools](https://developer.android.com/tools/releases/platform-tools)，
完整解压并保留同目录 DLL。可以在界面选择 `adb.exe`，或与程序一起放置：

```text
Toolbox.exe
platform-tools/
  adb.exe
  AdbWinApi.dll
  AdbWinUsbApi.dll
  ...其余官方文件
```

程序依次查找已记住的路径、EXE 旁的 `platform-tools` / `adb.exe`、Android SDK 和 PATH。
支持识别 Scoop 的 ADB shim，实际运行原始程序以保证取消有效。
缺少 ADB 时显示安装提示和官方下载链接，不会影响其他大类。
带 `platform-tools` 的便携目录应整体复制到新电脑；USB 调试授权及设备所需驱动仍需配置。
Git 源码不包含 Platform Tools 二进制；发布脚本从官方地址下载并验证 SHA256 后放入 Release ZIP。
工具箱运行时不自动下载或安装驱动。

参数与使用方式依据 [Android ADB 文档](https://developer.android.com/tools/adb) 和
[AOSP 命令参考](https://android.googlesource.com/platform/packages/modules/adb/+/HEAD/docs/user/adb.1.md)。

## 网络调试

左侧“网络调试”包含“TCP / UDP 收发”、“局域网扫描”和“实时抓包”三个子页。
“局域网扫描”按 IPv4 网段 Ping，以绿、黄、红、灰点显示延迟及未响应状态。
“网络服务”包含下载测速和出口 IP / 风险查询。

- **UDP**：选择本地 IPv4 和端口，打开后填写目标 IPv4/主机名及端口进行收发，支持 IPv4 广播地址。
- **TCP 客户端**：连接目标服务器，可指定本地地址；本地端口 `0` 自动分配。
- **TCP 服务端**：监听本地端口，最多接入 64 个客户端；可选择一个客户端或全部客户端发送，也可断开选中连接。

支持 UTF-8 / ASCII / GBK、文本/HEX、日志/纯接收显示、自动换行、暂停显示、定时发送、
常用转义符 `\\`、`\r`、`\n`、`\t`、`\0`，以及 CRLF/LF/CR 结尾。
HEX 模式发送原始字节，不添加文本结尾。文件可载入发送区作为 HEX，点击发送后才发出。
“清空记录 / 计数”会同步清空日志、待显示记录和 RX/TX 计数。

切换大类继续保持连接；关闭连接或退出程序释放 Socket。暂停显示不会停止接收或计数。
定时发送固定启动时的内容和目标，写入尚未完成时跳过时钟，不积压并发发送任务。
TCP 群发逐连接写入，某个目标失败时停止本轮剩余目标，已经成功发送的数据不会撤回。

当前使用普通 IPv4 Socket，按系统路由通信，不使用测速页的 HTTP 代理，不包含 TLS 或代理握手。
`0.0.0.0` 表示监听所有本机 IPv4 地址。TCP 是字节流，单次读取不等于一条完整消息。
UDP 单次最多 65,507 字节，TCP 单次最多 65,536 字节，文件载入也使用对应上限。
日志保留最近约 256 KB / 4,000 条；接收显示队列溢出时提示丢弃记录，字节计数仍累计。
TCP/UDP 收发页提供日志导出，不包含接收数据持续写入文件、自动校验码或协议解析。

### 实时抓包

先从 [Npcap 官网](https://npcap.com/#download) 安装驱动，再点击“刷新网卡”，选择网卡并“开始抓包”。
程序启动和切页不会自动抓包；缺少驱动或权限不足时显示具体错误。若 Npcap 安装时限制仅管理员使用，
请以管理员身份运行工具箱。Npcap 不包含在便携包中，其他工具不依赖它。

- 支持 BPF 过滤，例如 `tcp`、`udp port 53`、`host 192.168.1.20`；留空表示所选网卡全部流量。
  混杂模式默认关闭；回环网卡用于本机进程间流量。
- 实时显示时间、源/目标地址、协议、线上的原始长度和摘要，解析 Ethernet/VLAN、IPv4/IPv6、TCP、UDP、ICMP 和 ARP。
  选中数据包显示 HEX / ASCII，最多预览前 4,096 字节；不做 TCP 流重组、应用层分析或 TLS 解密。
- 每次抓包自动生成独立 PCAP 文件，默认保存在“文档/ToolboxCaptures”。可修改目录，停止后可另存。
  每包最多捕获 65,535 字节，超过时标明捕获长度；文件保存每个成功读取且未超过文件上限的数据包。
- 默认最多 60 秒 / 64 MiB，可设 1–3,600 秒 / 1–512 MiB，任一达到即停止。
  列表只保留最近 1,000 包且总数据不超过 8 MiB；较早包仍在 PCAP 中，界面队列和历史均有上限。
- 暂停显示仅冻结列表，捕获、计数和文件写入继续；切换页面也继续抓包。
  停止或退出时等待本次抓包结束并关闭文件，不操作其他程序的抓包会话。
- “缓冲丢包”来自 Npcap 统计，不可用时显示“—”；0 不保证链路上没有丢包。
  原始 PCAP 可在 Wireshark 中继续分析。流量范围取决于所选网卡和系统路由，HTTP 代理设置不控制抓包。

原生接口依据 [Npcap 开发文档](https://npcap.com/guide/npcap-devguide.html)。

## 多条发送

在发送区选择“多条发送”，输入内容并勾选需要发送的行。
每条可独立选择 HEX；文本条目使用页面的编码与结尾设置，HEX 不添加文本结尾。

- “发送选中条目”：按列表顺序发送一轮，条目之间使用设置的发送间隔。
- “开始循环发送”：按间隔逐条发送选中项，到末尾后从第一条继续。
- “停止发送”或“停止循环发送”：停止后续条目，已开始的串口写入仍会完成。
- 新增、删除、全选与全不选只编辑列表，不会自动发送。

发送前会校验全部选中条目；输入无效时整轮不发送。最多 1,000 条，每条最多
65,536 字节，选中内容合计最多 1 MB。条目在当前窗口内保留，关闭后不自动保存。

## 构建与运行

需要 Windows 10/11、.NET Framework 4.8。普通 Linux 不能直接运行此 WinForms 程序。

Windows 下运行：

```bat
build.cmd
bin\Toolbox.exe
```

源码也可放在 WSL 的 `~/work/toolbox`，通过 Windows interop 构建：

```bash
./build-wsl.sh
```

脚本使用 Windows 自带的 .NET Framework C# 编译器；无需安装 NuGet 包。
构建结果在 `bin/`。`pushd` 支持从 WSL 的 UNC 路径构建。

### 打包与自动发布

构建后，在 Windows PowerShell 中运行：

```powershell
.\package.ps1 -Version 3.3.0
```

生成 `bin/Toolbox-3.3.0-portable.zip` 和对应 `.zip.sha256` 校验文件。
ZIP 包含 EXE、README、使用说明及完整官方 Platform Tools（保留 DLL 和 NOTICE）。
脚本固定官方 SDK 版本与 SHA256；不包含测试输出、抓包文件、用户设置或 Npcap 驱动。

推送版本标签会触发 `.github/workflows/release.yml`：

```bash
git tag v3.3.0
git push origin v3.3.0
```

Action 在 Windows 上构建、运行核心与界面测试、打包，再将 **完整便携 ZIP 和 SHA256** 上传 GitHub Release。
也可在 Actions 中手动指定已存在的版本标签；已有 Release 不会被覆盖，修订版本请使用新标签。
预发行标签（如 `v3.4.0-beta.1`）标记为 prerelease。测试报告保留 14 天。
源码树仅提交代码、文档和脚本，发布产物存放在 Release 附件中。

## 验证

```bat
bin\Toolbox.exe --self-test
tests\run.cmd
```

WSL 下运行界面测试：

```bash
./build-wsl.sh test
```

核心测试覆盖下载、代理、IP 风险解析、LAN 扫描、串口编码与真实 TCP/UDP 回环通信。
界面测试使用模拟串口和本机回环 Socket，覆盖连接/断开、参数设置、单条/多条发送、
TCP 定向/群发、暂停显示、循环、取消和错误清理；不会打开真实 COM 端口。
ADB 测试使用单独编译的模拟设备程序，通过真实子进程验证参数传递、实时 stdout/stderr、
中文及空格路径、文件/目录往返、失败退出码、取消与退出释放，不接触真实 Android 设备。
目录浏览测试还覆盖文件/目录类型、链接、隐藏项、筛选、补全候选、权限失败、路径回填和关闭时取消读取。
抓包测试使用模拟来源，验证协议解析、10,000 组畸形数据、PCAP 格式与上限、取消、异常清理、
实时列表、暂停、界面裁剪后文件完整性和退出释放，无需安装 Npcap，也不打开真实网卡。
开发机未检测到 Npcap，因此本版尚未完成真实网卡抓包验证。
测试报告、测试程序和预览图全部写到 `bin/`，不提交到仓库。
真实串口设备的兼容性仍需按具体驱动和设备参数验证。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `NetSpeed.cs` | 程序入口、下载测速与主窗体 |
| `ToolShell.cs` | 左侧导航和操作按钮 |
| `IpInfo.cs` | 公网出口与 Scamalytics 查询 |
| `LanScan.cs` | IPv4 Ping 扫描与点阵显示 |
| `SerialAssistant.cs` | 串口通信、发送列表与界面 |
| `Communication.cs` | 串口和网络调试共用的编码、收发记录与有界日志 |
| `SocketSession.cs` | UDP / TCP 客户端 / TCP 服务端通信与连接生命周期 |
| `SocketPage.cs` | 独立网络调试页面 |
| `SocketTests.cs` | Windows 本机回环通信测试 |
| `PacketCapture.cs` | Npcap 接口、捕获任务与 PCAP 文件 |
| `PacketDecoder.cs` | 常见链路/IP 协议摘要和 HEX 预览 |
| `CapturePage.cs` | 实时抓包、列表与保存设置 |
| `AdbClient.cs` | ADB 定位、进程执行、设备解析和路径参数 |
| `AdbWorkspace.cs` | ADB 共用设备设置、Shell 与文件传输页面 |
| `AdbBrowser.cs` | 板端目录读取、文件类型解析和路径选择窗口 |
| `tests/UiTests.cs` | 串口、Socket、ADB 和抓包界面集成测试 |
| `tests/AdbTests.cs` / `tests/FakeAdb.cs` | ADB 子进程与隔离的模拟设备测试 |
| `tests/CaptureTests.cs` | 模拟抓包来源、解析和 PCAP 测试 |
| `package.ps1` | 完整便携 ZIP 和校验文件打包 |
| `.github/workflows/release.yml` | Windows 构建、验证和 Release 发布 |

详细用法见 [使用说明](使用说明.txt)。仓库仅包含源码、文档和构建脚本；
二进制、编译中间产物、测试输出与本机设置已由 `.gitignore` 排除。
