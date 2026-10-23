# Security Policy

## 报告问题

请使用 GitHub 的 **Private vulnerability reporting / Security advisory**
（仓库 → Security → Report a vulnerability）报告安全问题，不要在公开 Issue 里贴利用细节。

## 这个程序会接触什么

- **只读**：注册表 `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\*\Device Parameters\EDID`
  （用来在没有 DDC/CI 时也能显示正确的显示器型号）。
- **只写显示器硬件**：通过 `dxva2.dll` 的 DDC/CI 接口写 VCP `0xD6`（电源模式）；
  通过 `WM_SYSCOMMAND / SC_MONITORPOWER` 让系统熄灭显示器；通过 `LockWorkStation()` 锁定桌面。
- **本机文件**：仅在程序崩溃时，在 exe 同目录写 `monitor-power-error.log`（异常堆栈）。

## 设计上的安全边界

- **无遥测、无账号、无云端**、**不发起任何网络请求**。
- 不需要管理员权限；DDC/CI 与锁屏都以当前用户身份完成。
- 不写注册表、不安装服务、不创建开机项、不改系统设置。
- 所有 DDC/CI 调用都有超时，避免在不答应的显示器上阻塞。

## 支持范围

只对最新 Release 提供安全修复。
