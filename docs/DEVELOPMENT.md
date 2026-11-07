# 开发维护

项目使用 C# 5 / WinForms / .NET Framework 4.8，沿用 Windows 自带编译器，不依赖 NuGet。
以下命令均从仓库根目录执行。

## 构建与测试

```bat
scripts\build.cmd
tests\run.cmd
```

`scripts/build.cmd` 只编译 `src/`，产物为 `bin/Toolbox.exe`。
`tests/run.cmd core` 编译并运行 `bin/CoreTests.exe`；`tests/run.cmd ui` 编译并运行界面测试及隔离的 ADB 模拟程序。
不带参数执行两组测试。测试报告、模拟设备数据与截图写入 `bin/`。

WSL 中通过 Windows interop 调用同一套脚本：

```bash
./scripts/build-wsl.sh
./scripts/build-wsl.sh test
```

编译器定位由 `scripts/compile.cmd` 统一处理，公共参数放在 `scripts/compiler.rsp`。
源码按目录自动收集，测试程序集通过 `/main` 指定自己的入口。

## 开发诊断

先运行 `tests/run.cmd core`，再按需执行：

| 命令 | 作用 |
| --- | --- |
| `bin\CoreTests.exe --self-test` | 本地 HTTP、代理、IP 解析、LAN、串口和 Socket 测试 |
| `bin\CoreTests.exe --smoke-test` | 打开并关闭主窗口，生成 `bin/preview.png` |
| `bin\CoreTests.exe --check-lan` | 实际 Ping 本机 127.0.0.1 |
| `bin\CoreTests.exe --check-ip` | 实际查询当前直连出口和 Scamalytics，报告省略公网 IP |
| `bin\CoreTests.exe --check-universities` | 实际访问两所大学测速站，各下载两秒 |

这些参数从正式 EXE 移至独立测试入口。日常运行 `Toolbox.exe` 只启动工具箱。
真实 IP / 大学检查会联网，不在自动测试中运行。串口、ADB、抓包使用模拟对象；TCP/UDP 使用本机回环。

## 放置与修改代码

- `src/App/Program.cs`：启动设置和唯一的界面版本常量。`MainForm.cs` 组装页面、协调网络互斥和退出清理。
- `src/Common/`：已有多个功能使用的编码、记录和缓存。功能专用逻辑放回自己的目录。
- `src/Network/`、`src/Serial/`、`src/Adb/`：按功能组织，页面处理布局和交互，通信/查询类处理实际工作。
  较小且紧密相关的模型可以和实现放在同一文件。
- `tests/Core/`：核心行为测试；`tests/UI/`：按功能分开的界面场景，共用 `ToolboxUiTests` 的辅助方法；
  `tests/Fixtures/`：模拟设备与本地测试服务。测试类型不放进 `src/`。
- 使用 `.editorconfig` 约定的四空格缩进、UTF-8 与换行。CMD 使用 CRLF，PowerShell 使用 UTF-8 BOM 以兼容 Windows PowerShell 5。
- 界面文案或功能改变时同步 `docs/使用说明.txt`；构建、测试入口改变时同步本文和工作流。

## 发布

更新 `Program.Version`、README 与使用说明中的版本，然后构建、测试并推送对应的 `vX.Y.Z` 标签。
工作流按新目录构建正式 EXE，分别运行核心和界面测试，再执行 `scripts/package.ps1`。

本地打包示例：

```powershell
.\scripts\package.ps1 -Version 3.3.3
```

完整 ZIP 包含正式 EXE、README、`docs/` 与固定版本的官方 Platform Tools，另附 SHA256。
`CoreTests.exe`、`UiTests.exe`、模拟设备、截图和中间产物留在 `bin/`，不放进 Release，也不提交 Git。
