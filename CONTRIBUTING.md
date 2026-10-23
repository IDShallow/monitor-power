# Contributing

感谢你愿意改进 Monitor Power。这个项目刻意保持**很小**：单 exe、零第三方依赖、
只做「用 DDC/CI 开关物理显示器」这一件事。

## 构建

环境：Windows 10 / 11 + .NET Framework 4.x（系统自带 `csc.exe`）。

```cmd
tools\build.cmd
```

成功会输出 `BUILD OK`，产物在 `dist\monitor-power.exe`。不需要 NuGet，不需要 Visual Studio。

## 代码约束

- **C# 5 语法**：编译器是 .NET Framework 自带的 `csc.exe`。不要用字符串插值 `$""`、
  `?.`、`nameof`、表达式体成员、模式匹配、`out var`。
- **零第三方依赖**：只用 .NET Framework 自带程序集。
- **无遥测 / 无账号 / 无网络**：程序不发起任何网络请求。
- **DDC/CI 调用必须带超时**：对不答应的显示器，`SetVCPFeature` 会在驱动里长时间阻塞；
  所有调用都要放在后台线程里并 `Join(毫秒)`。
- **失败要说清楚**：读不到/写不进时，界面和日志要给出原因，不允许静默失败或假装成功。
- 界面文案保持中文（与现有 UI 一致）。

## 手动验证

提交 PR 前请至少确认：

1. `tools\build.cmd` 通过（`BUILD OK`）；
2. 启动 `dist\monitor-power.exe`：卡片、状态、三种关法各点一次（有双屏的话，确认只影响目标屏）；
3. 拔掉/插上一块屏，等 12 秒或点「刷新」，确认列表自动更新；
4. 有 DDC/CI 不通的屏时，确认日志给出了退化说明而不是报错。

## 提交 PR 前

- 不要提交 `dist/`、日志、个人路径或任何令牌；
- 新功能请同步更新 `README.md` 与 `CHANGELOG.md`。
