# Codex Usage Tray（Windows）

一个本地 Windows 托盘程序，用来显示 Codex 已记录的剩余用量：5 小时窗口和 7 天窗口各自的剩余比例与重置时间。

它**只读取**`%USERPROFILE%\.codex\sessions\**\*.jsonl` 中由 Codex 写入的 `rate_limits` 事件，不调用非公开接口，不读取 `auth.json`，也不上传任何数据。

> Windows 10/11 不支持把任意第三方文字固定嵌入任务栏本体；本程序显示在任务栏右侧的通知区域（系统托盘）。悬停会显示两个数字，例如“5小时 58% / 7天 91%”；右键可看两段窗口的剩余比例和重置时间。图标绿色表示两个窗口均剩余 30% 以上，橙色表示至少一个窗口剩余 11–30%，红色表示至少一个窗口只剩 10% 或更少。

## 安装与运行

要求：Windows，.NET 8 SDK（若使用 `-SelfContained`，运行机器不需要 .NET）。在插件根目录运行：

```powershell
.\scripts\build.ps1 -SelfContained
.\scripts\install.ps1 -Startup
```

如果系统策略禁止执行本地 PowerShell 脚本，可仅对当前命令使用 `Bypass`（不会修改系统策略）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -SelfContained
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Startup
```

首次运行前，先在 Codex 中完成一次对话；这样本地才会有可读取的用量快照。双击托盘图标可立即刷新，右键菜单可以退出。卸载：

```powershell
.\scripts\uninstall.ps1
```

更新时重新执行安装命令即可；脚本会自动结束旧的托盘进程、替换文件并重新启动。

## 数据与限制

- 刷新频率为 30 秒；读取最新 12 个会话文件末尾最多 1 MiB，避免扫描整份历史记录。
- 若 Codex 改变本地日志字段、清理了日志，或当前账号暂未产生快照，图标会变灰并显示原因。
- 显示的是 Codex 最后一次写入的服务端用量状态，不是根据本地 token 估算。
