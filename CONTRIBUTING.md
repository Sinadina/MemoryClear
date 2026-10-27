# 贡献指南

感谢想帮忙。这个项目很小，只有一份 C# 单文件 + 一个清单，所以规则也很短。

## 环境

只需要 **Windows 10/11** 和系统自带的 **.NET Framework 4.x**。

不需要 Visual Studio，不需要 .NET SDK，不需要 NuGet。编译器是 Windows 自带的老版
`csc.exe`（C# 5），这一点是刻意的：产物是单个 exe，拷到任何一台 Windows 上双击就能跑。

## 编译

```bat
build.cmd
```

或手动：

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:anycpu ^
  /optimize+ /win32manifest:app.manifest /out:MemoryClear.exe ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.Management.dll MemoryClear.cs
```

注意：`csc` 必须在本目录下用**相对文件名**调用，传带空格的绝对路径会报「无法打开源文件」。

## 代码结构（`MemoryClear.cs`，单文件）

| 区域 | 作用 |
|---|---|
| `Native` | 所有 P/Invoke：`NtSetSystemInformation`、`GlobalMemoryStatusEx`、`GetPerformanceInfo`、`EnumProcesses`、令牌提权、进程信息遍历 |
| `MemSnapshot` | 一次内存快照（可用 / 待命 / 已修改 / 提交 / 缓冲池 / 已压缩） |
| `Policy` | 推荐清理级别 与 告警等级判定（纯函数，可用 `--policy-test` 验证） |
| `CleanEngine` | 五个级别的实际执行逻辑 |
| `CleanHelper` / `HelperMode` | 提权助手：命名管道协议、会话内复用、父进程看门狗 |
| `MemComposition` / `StatGrid` / `LevelCard` / `UiProgress` / `WarningStrip` | 自绘控件 |
| `MainForm` | 布局、状态刷新、清理流程编排 |

## 不要破坏的约定

改代码前请先读这几条，它们都是踩过坑换来的：

1. **L4 裁剪工作集之后必须补 `FlushModifiedList` + `PurgeStandbyList`。**
   只裁剪会把页推进待命/已修改列表，表现为「清理完待命缓存反而变大」。
2. **主程序以 `asInvoker` 运行，真正动内核的操作走提权助手。**
   UAC 一次会话只能弹一次：助手要复用，不能每次清理都重新 `runas`。
3. **关闭窗口必须完全退出**，并结束助手的进程；助手自带父进程看门狗，主进程被强杀也不能留孤儿。
4. **不要引入常驻、托盘监控、自启动、系统服务。** 这是产品边界，不是没做。
5. **界面文案不要提及其它软件的名字**（例如"等同 XX 一键"），功能后果用自己的话讲清楚。
6. **不要为了"数字好看"改度量口径。** 差距要如实报告：待命缓存下降 ≠ 可用内存上升。

## 自测

改动策略或清理逻辑后，跑这几条确认没坏：

```bat
MemoryClear.exe --selftest        :: 只读：快照、推荐级别、L4 预演
MemoryClear.exe --policy-test     :: 只读：分级与告警判定表
MemoryClear.exe --cli --level 4 --self-only   :: 安全自测：只裁剪自身
```

界面相关的改动请附一张截图（`----selftest` 之外，实际开窗口截）。

## 提交

- 一个 PR 只做一件事；提交信息用中文或英文都行，说清「改了什么 / 为什么」。
- 如果修的是 issue，PR 描述里带 `Fixes #12`。
- 涉及清理行为的改动，请在 PR 里贴 `--cli` 的实测输出或界面前后数字。

## 发布

打 tag 推上去，GitHub Actions 会自动编译并创建 Release：

```bash
git tag v1.0.1 && git push origin v1.0.1
```
