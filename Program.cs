using DeskLite.Core;

namespace DeskLite;

/// <summary>
/// 程序入口。
///
/// 这个类只负责一件事：**决定怎么启动**。
///   · 带 <c>--xxx</c> 参数 → 交给 <see cref="Cli.Diagnostics"/> 跑对应的自检工具，跑完即退；
///   · 不带参数 → 正常常驻运行（单实例守卫 → 桌面自愈 → 建托盘与格子）。
///
/// 启动顺序上有两处刻意的安排：
///   1. <see cref="CrashGuard.Init"/> 必须**最先**执行 —— 全局异常处理器要在任何窗口建立之前装好，
///      否则启动阶段的异常仍然会直接结束进程。
///   2. <see cref="CrashGuard.RecoverIfNeeded"/> 必须在接管桌面**之前**执行 ——
///      上次若崩溃退出，桌面可能还停在「原生图标被隐藏」的状态，先救回来再谈别的。
/// </summary>
internal static class Program
{
    /// <summary>演示 / 无人值守模式：抑制交互式弹窗（由各诊断命令设置）</summary>
    internal static bool Silent;

    /// <summary>单实例互斥体名（右键菜单命令需要据此转交给已运行实例）</summary>
    internal const string MutexName = @"Global\DeskLite_SingleInstance_9F2A41";

    [STAThread]
    private static void Main(string[] args)
    {
        // 命令行自检工具优先：它们不需要（也不该）走正常启动流程
        if (Cli.Diagnostics.TryRun(args)) return;

        RunInteractive();
    }

    /// <summary>正常运行路径：单实例 → 桌面自愈 → 消息循环</summary>
    private static void RunInteractive()
    {
        CrashGuard.Init();

        // 单实例：避免重复启动产生多套格子
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("「桌面整理 · 精简版」已在运行，请在任务栏托盘区查看。",
                "DeskLite", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // 上次若是崩溃 / 强杀退出，桌面原生图标可能还处于隐藏状态
            CrashGuard.RecoverIfNeeded();

            ApplicationConfiguration.Initialize();
            Application.Run(new UI.MainContext());
        }
        catch (Exception ex)
        {
            // 兜底：启动阶段就抛异常的话，主窗口循环根本没跑起来，
            // 这里除了记日志，还要保证桌面是被还原的（不能把图标留在隐藏状态）。
            CrashGuard.Log(ex, "Program.Main（启动失败）");
            CrashGuard.EmergencyRestoreDesktop();
            CrashGuard.MarkDesktopShown();

            if (!Silent)
                MessageBox.Show(
                    "程序启动失败，桌面原生图标已还原。\n\n详情见日志：\n" + CrashGuard.LogPath,
                    "桌面整理 · 精简版", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
