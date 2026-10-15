using System.Runtime.InteropServices;
using DeskLite.Win32;

namespace DeskLite.Core;

/// <summary>由右键菜单或全局热键下发的命令</summary>
public enum ShellCommand
{
    /// <summary>一键整理桌面</summary>
    Sort = 1,
    /// <summary>打开桌面搜索（可带初始关键字）</summary>
    Search = 2,
    /// <summary>把指定文件/文件夹送入对应格子</summary>
    SendTo = 3,
    /// <summary>优雅退出（先还原桌面原生图标再结束进程）—— 用于升级 / 重启，避免强杀导致图标一直隐藏</summary>
    Quit = 4,
}

/// <summary>
/// 进程间命令总线（隐藏消息窗口）。
///
/// 解决的问题：右键菜单注册的命令是「启动一个新进程」（如 <c>DeskLite.exe --cmd sort</c>），
/// 但主程序是单实例常驻的，新进程必须把意图转交给已在运行的实例。
/// 这里用 <see cref="Native.RegisterWindowMessage"/> 注册一个全局唯一消息，再
/// <c>PostMessage(HWND_BROADCAST, ...)</c> 广播；已运行实例的隐藏窗口收到后执行。
/// 带参数的 <see cref="ShellCommand.SendTo"/> 通过临时文件中转（广播无法跨进程传指针）。
///
/// 同时承载全局热键（<c>RegisterHotKey</c> → <c>WM_HOTKEY</c>）。
/// </summary>
public sealed class CmdBus : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x0D5C;
    private static readonly string TempArgFile = Path.Combine(Path.GetTempPath(), "DeskLite_cmd.tmp");

    /// <summary>自定义广播消息（进程内唯一，跨进程按字符串注册也一致）</summary>
    private static readonly int WM_DESKLITE_CMD = Native.RegisterWindowMessage("DeskLite_ShellCommand_9F2A41");

    private bool _hotkeyRegistered;
    private bool _disposed;

    /// <summary>收到右键菜单命令</summary>
    public event Action<ShellCommand, string?>? CommandReceived;

    /// <summary>全局热键被按下</summary>
    public event Action? HotkeyPressed;

    public bool HotkeyActive => _hotkeyRegistered;
    public string? HotkeyText { get; private set; }

    public CmdBus()
    {
        // 建一个不可见的顶层窗口用于接收广播与热键消息
        CreateHandle(new CreateParams
        {
            Caption = "DeskLite_CmdBus",
            X = 0, Y = 0, Width = 0, Height = 0,
            Style = 0,                       // 无 WS_VISIBLE，创建即隐藏
            ExStyle = 0x00000080,            // WS_EX_TOOLWINDOW，不进 Alt+Tab
            Parent = IntPtr.Zero,
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DESKLITE_CMD)
        {
            var cmd = (ShellCommand)m.WParam.ToInt32();
            string? arg = null;

            if (cmd == ShellCommand.SendTo)
            {
                try
                {
                    if (File.Exists(TempArgFile))
                    {
                        arg = File.ReadAllText(TempArgFile);
                        File.Delete(TempArgFile);
                    }
                }
                catch { arg = null; }
            }

            try { CommandReceived?.Invoke(cmd, arg); } catch { }
            return;
        }

        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            try { HotkeyPressed?.Invoke(); } catch { }
            return;
        }

        base.WndProc(ref m);
    }

    // ───────── 发送端 ─────────

    /// <summary>
    /// 向已运行的实例广播命令。带参数时先落盘到临时文件。
    /// 返回是否成功投递（不代表对方一定在处理）。
    /// </summary>
    public static bool Broadcast(ShellCommand cmd, string? arg = null)
    {
        try
        {
            if (cmd == ShellCommand.SendTo && !string.IsNullOrEmpty(arg))
                File.WriteAllText(TempArgFile, arg);

            return Native.PostMessage(Native.HWND_BROADCAST, WM_DESKLITE_CMD,
                new IntPtr((int)cmd), IntPtr.Zero);
        }
        catch { return false; }
    }

    // ───────── 全局热键 ─────────

    /// <summary>注册全局热键，形如 "Ctrl+Alt+S"。返回是否成功。</summary>
    public bool Register(string? spec)
    {
        Unregister();
        HotkeyText = null;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        if (!TryParse(spec, out uint mods, out uint vk)) return false;

        _hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, mods, vk);
        if (_hotkeyRegistered) HotkeyText = spec.Trim();
        return _hotkeyRegistered;
    }

    public void Unregister()
    {
        if (!_hotkeyRegistered) return;
        try { Native.UnregisterHotKey(Handle, HotkeyId); } catch { }
        _hotkeyRegistered = false;
    }

    /// <summary>解析 "Ctrl+Alt+S" 这类描述为 Win32 修饰键 + 虚拟键码</summary>
    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var raw in parts)
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; continue;
                case "alt":               modifiers |= Native.MOD_ALT;     continue;
                case "shift":             modifiers |= Native.MOD_SHIFT;   continue;
                case "win" or "windows":  modifiers |= Native.MOD_WIN;     continue;
            }

            // 最后一段是主键
            if (Enum.TryParse<Keys>(p, ignoreCase: true, out var key))
                vk = (uint)key;
            else if (p.Length == 1)
                vk = char.ToUpperInvariant(p[0]);
            else
                return false;
        }

        return modifiers != 0 && vk != 0;
    }

    // ───────── 生命周期 ─────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unregister();
        try { DestroyHandle(); } catch { }
    }
}
