using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;

namespace PgrVoice;

// Timestamp 与 Stopwatch.GetTimestamp() 使用同一时钟；ReceivedTimestamp 可区分消息排队延迟。
public readonly record struct RawKeyInput(int VirtualKey, bool IsDown, IntPtr Foreground,
    long Timestamp, long ReceivedTimestamp, IntPtr Device, uint? MessageTime = null);
public readonly record struct ObservedKeyInput(Key Key, IntPtr Foreground,
    long Timestamp, long ReceivedTimestamp, ModifierKeys Modifiers = ModifierKeys.None, uint? MessageTime = null);
public readonly record struct ObservedMouseInput(string Button, IntPtr Foreground, int X, int Y,
    long Timestamp, long ReceivedTimestamp, IntPtr Device = default, uint? MessageTime = null);
public sealed record ObservedMouseGesture(ObservedMouseInput Down, ObservedMouseInput Up, bool Accepted, string Reason);

public sealed record MenuCaptureSnapshot(bool Enabled, int Epoch, IntPtr GameHandle,
    IntPtr PlayerHandle, IntPtr MenuHandle)
{
    public static readonly MenuCaptureSnapshot Disabled = new(false, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    public int FindEpoch(Key key, IntPtr foreground, ModifierKeys modifiers = ModifierKeys.None) =>
        modifiers == ModifierKeys.None && Enabled && Epoch > 0 && foreground != IntPtr.Zero &&
        (foreground == GameHandle || foreground == PlayerHandle || foreground == MenuHandle) &&
        key is Key.Up or Key.Down or Key.Enter or Key.Escape ? Epoch : 0;
}

internal static class RawKeyboardKey
{
    // RAWKEYBOARD 的 Ctrl/Alt/Shift 可以使用通用 VK；必须先区分左右键，
    // 否则放开右 Ctrl 会把仍按住的左 Ctrl 一起清掉。
    public static int Normalize(int virtualKey, int makeCode, int flags) => virtualKey switch
    {
        0x10 => makeCode == 0x36 ? 0xA1 : 0xA0,
        0x11 => (flags & 2) != 0 ? 0xA3 : 0xA2,
        0x12 => (flags & 2) != 0 ? 0xA5 : 0xA4,
        _ => virtualKey
    };
    public static ModifierKeys Modifier(int virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
        0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };
}

internal sealed class RawInputModifierState
{
    readonly object sync = new();
    readonly HashSet<(IntPtr Device, int Key)> held = new();
    static readonly IntPtr StartupDevice = new(-1);
    public void Seed(int key) { lock (sync) held.Add((StartupDevice, key)); }
    public ModifierKeys Current { get { lock (sync) return CurrentLocked(); } }
    ModifierKeys CurrentLocked()
    {
        ModifierKeys result = ModifierKeys.None;
        foreach (var item in held) result |= RawKeyboardKey.Modifier(item.Key);
        return result;
    }
    public ModifierKeys Update(IntPtr device, int key, bool down)
    {
        lock (sync)
        {
            if (RawKeyboardKey.Modifier(key) != ModifierKeys.None)
            {
                held.Remove((StartupDevice, key));
                if (down) held.Add((device, key)); else held.Remove((device, key));
            }
            return CurrentLocked();
        }
    }
    public void RemoveDevice(IntPtr device) { lock (sync) held.RemoveWhere(item => item.Device == device); }
}

public static class InputMessageClock
{
    public static long ToTimestamp(uint messageTime, uint currentTick, long receivedTimestamp)
    {
        // Windows 的毫秒时钟会回绕，按无符号减法再解释短时间差。
        int elapsedMs = unchecked((int)(currentTick - messageTime));
        return receivedTimestamp - (long)(Math.Max(0, elapsedMs) * (Stopwatch.Frequency / 1000.0));
    }
    public static (int X, int Y) DecodePosition(uint position) =>
        (unchecked((short)(position & 0xffff)), unchecked((short)(position >> 16)));
}

// 普通按键只有 Raw Input 一个计数来源；真实按下/释放不受固定防抖时间限制。
internal sealed class RawInputPressGate
{
    readonly object sync = new();
    readonly HashSet<(IntPtr Device, int Key)> held = new();
    public bool Update(IntPtr device, int key, bool down)
    {
        lock (sync)
        {
            if (!down) { held.Remove((device, key)); return false; }
            return held.Add((device, key));
        }
    }
    public void RemoveDevice(IntPtr device) { lock (sync) held.RemoveWhere(item => item.Device == device); }
}

// 此状态只处理菜单 Hook，不与 Raw Input 的按下/释放集合混用。
internal sealed class DedicatedMenuKeyGate
{
    readonly object sync = new();
    readonly HashSet<int> held = new(), captured = new();
    public (bool Swallow, bool Fire) Update(int key, bool down, bool capture, bool injected)
    {
        if (injected) return (false, false);
        lock (sync)
        {
            if (!down) { held.Remove(key); return (captured.Remove(key), false); }
            bool first = held.Add(key);
            if (first && capture) captured.Add(key);
            return (captured.Contains(key), first && captured.Contains(key));
        }
    }
}

// 菜单已经关闭时，仍能认出排队中的同一次 Raw Enter。
// 这里关联该次按下/释放的事件时刻，不按“最近触发了多久”屏蔽新的物理点击。
internal sealed class CapturedMenuInputHistory
{
    sealed class Interval(int key, long start)
    {
        public int Key { get; } = key;
        public long Start { get; } = start;
        public long? End { get; set; }
    }
    readonly object sync = new();
    readonly List<Interval> intervals = new();
    public void Observe(int key, bool down, long timestamp)
    {
        lock (sync)
        {
            Interval? active = intervals.FindLast(item => item.Key == key && item.End == null);
            if (down && active == null) intervals.Add(new(key, timestamp));
            else if (!down && active != null) active.End = timestamp;
            long oldest = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 30;
            intervals.RemoveAll(item => item.End < oldest);
            if (intervals.Count > 128) intervals.RemoveRange(0, intervals.Count - 128);
        }
    }
    public bool Contains(int key, long timestamp)
    {
        // GetMessageTime/KBDLLHOOKSTRUCT.Time 只有毫秒精度；给时钟换算保留 2ms 容差。
        long tolerance = Stopwatch.Frequency * 2 / 1000;
        lock (sync) return intervals.Exists(item => item.Key == key && timestamp >= item.Start - tolerance &&
            (item.End == null || timestamp <= item.End.Value + tolerance));
    }
}
