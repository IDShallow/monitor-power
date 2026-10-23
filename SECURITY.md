# Security Policy

## 报告问题

请使用 GitHub 的 **Private vulnerability reporting / Security advisory**
（仓库 → Security → Report a vulnerability）报告安全问题，不要在公开 Issue 里贴利用细节。

## 这个程序会接触什么

- **只读**：注册表 `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\*\Device Parameters\EDID`
  （用来在没有 DDC/CI 时也能显示正确的显示器型号）。
- **只写显示器硬件**：通过 `dxva2.dll` 的 DDC/CI 接口写 VCP `0xD6`（电源模式）；
  通过 `WM_SYSCOMMAND / SC_MONITORPOWER` 让系统熄灭显示器；通过 `LockWorkStation()` 锁定桌面。
- **本机设置**：`%APPDATA%\monitor-power\board.ini`（看板刷新频率、置顶、显示哪几块屏）。
- **开机自启（仅在用户勾选时）**：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MonitorPower`，
  指向当前 exe；取消勾选即删除。
- **本机文件**：仅在程序崩溃时，在 exe 同目录写 `monitor-power-error.log`（异常堆栈）。

## 网络

- 启动后**静默检查一次更新**，之后只在用户点「检查更新」时联网。
- 请求目标只有：
  - `https://github.com/IDShallow/monitor-power/releases/latest/download/latest.json`（版本清单）
  - 清单里给出的 exe 下载地址（同一个仓库的 Release 资产）
- 下载完成后会校验清单里的 **SHA256**，不匹配直接丢弃。
- 没有任何遥测、账号、统计或第三方接口。

## 设计上的安全边界

- 不需要管理员权限；DDC/CI、锁屏、自启都用当前用户身份完成。
- 不安装服务、不创建快捷方式、不改系统设置（除上面那条 Run 键）。
- 所有 DDC/CI 调用都有超时，避免在不答应的显示器上阻塞。

## 支持范围

只对最新 Release 提供安全修复。
