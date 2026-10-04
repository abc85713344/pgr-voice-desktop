using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PgrVoice;

// 借鉴 Android TapFollowGestureGate 的全程轨迹、700ms 长按及 300ms 连点保护。
// 桌面不拦截/补发鼠标：只在同一设备的一对 Down/Up 完成后报告是否适合跟随。
public sealed class MouseTapGate
{
    public const string Dragged = "移动超出轻点范围";
    public const string HeldTooLong = "按住时间超过 700 毫秒";
    public const string MultipleDevices = "轻点过程中使用了另一只鼠标";
    public const string ForegroundChanged = "轻点过程中前台窗口发生变化";
    public const string LateInput = "鼠标输入到达过晚";
    public const string OutOfOrder = "鼠标输入顺序或时间异常";
    public const string RepeatedDown = "未释放前再次收到鼠标按下";
    public const string RapidTap = "连续轻点间隔不足 300 毫秒";
    public const string InputUnavailable = "鼠标观察中断，请重新轻点";
    sealed class Press(ObservedMouseInput down)
    {
        public ObservedMouseInput Down = down;
        public string Reason = "";
    }
    readonly object sync = new();
    readonly Dictionary<IntPtr, Press> presses = new();
    readonly Dictionary<IntPtr, ObservedMouseInput> lastInputs = new();
    readonly double maximumDistanceSquared;
    readonly double maximumHoldMs, rapidTapMs, maximumAgeMs;
    ObservedMouseInput? lastTapUp;
    public MouseTapGate(double maximumDistancePixels = 12, double maximumHoldMs = 700, double rapidTapMs = 300, double maximumAgeMs = 200)
    {
        if (!double.IsFinite(maximumDistancePixels) || maximumDistancePixels <= 0 ||
            !double.IsFinite(maximumHoldMs) || maximumHoldMs <= 0 || !double.IsFinite(rapidTapMs) || rapidTapMs < 0 ||
            !double.IsFinite(maximumAgeMs) || maximumAgeMs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDistancePixels));
        maximumDistanceSquared = maximumDistancePixels * maximumDistancePixels;
        this.maximumHoldMs = maximumHoldMs; this.rapidTapMs = rapidTapMs; this.maximumAgeMs = maximumAgeMs;
    }
    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Elapsed(ObservedMouseInput later, ObservedMouseInput earlier) => later.MessageTime.HasValue && earlier.MessageTime.HasValue
        ? unchecked((int)(later.MessageTime.Value - earlier.MessageTime.Value)) : Milliseconds(later.Timestamp - earlier.Timestamp);
    static void Reject(Press press, string reason) { if (press.Reason.Length == 0) press.Reason = reason; }
    string ValidateTiming(ObservedMouseInput input)
    {
        if (input.Timestamp <= 0 || input.ReceivedTimestamp < input.Timestamp) return OutOfOrder;
        if (Milliseconds(input.ReceivedTimestamp - input.Timestamp) > maximumAgeMs) return LateInput;
        if (lastInputs.TryGetValue(input.Device, out var previous) &&
            (Elapsed(input, previous) < 0 || input.ReceivedTimestamp < previous.ReceivedTimestamp)) return OutOfOrder;
        return "";
    }
    void CheckPoint(Press press, ObservedMouseInput input, string timing)
    {
        if (input.Device != press.Down.Device) { Reject(press, MultipleDevices); return; }
        if (timing.Length != 0) Reject(press, timing);
        double duration = Elapsed(input, press.Down);
        if (duration < 0) Reject(press, OutOfOrder);
        else if (duration > maximumHoldMs) Reject(press, HeldTooLong);
        if (input.Foreground == IntPtr.Zero || input.Foreground != press.Down.Foreground) Reject(press, ForegroundChanged);
        double dx = (double)input.X - press.Down.X, dy = (double)input.Y - press.Down.Y;
        if (dx * dx + dy * dy > maximumDistanceSquared) Reject(press, Dragged);
    }
    public void Begin(ObservedMouseInput input)
    {
        // RAWINPUTHEADER.hDevice 可为零（如精确式触控板），仍需配对完整 Down/Up。
        if (input.Button != "鼠标左键") return;
        lock (sync)
        {
            string timing = ValidateTiming(input);
            if (presses.TryGetValue(input.Device, out var existing)) { Reject(existing, RepeatedDown); lastInputs[input.Device] = input; return; }
            var next = new Press(input);
            if (timing.Length != 0) Reject(next, timing);
            if (input.Foreground == IntPtr.Zero) Reject(next, ForegroundChanged);
            foreach (var other in presses.Values) { Reject(other, MultipleDevices); Reject(next, MultipleDevices); }
            presses[input.Device] = next; lastInputs[input.Device] = input;
        }
    }
    public void Move(ObservedMouseInput input)
    {
        lock (sync)
        {
            string timing = ValidateTiming(input);
            foreach (var press in presses.Values) CheckPoint(press, input, timing);
            lastInputs[input.Device] = input;
        }
    }
    public ObservedMouseGesture? End(ObservedMouseInput input)
    {
        if (input.Button != "鼠标左键") return null;
        lock (sync)
        {
            string timing = ValidateTiming(input);
            foreach (var other in presses.Values) CheckPoint(other, input, timing);
            lastInputs[input.Device] = input;
            if (!presses.Remove(input.Device, out var press)) return null; // 孤立释放没有 Down，不构造手势。
            if (press.Reason.Length == 0)
            {
                if (lastTapUp is { } previous && previous.Foreground == input.Foreground)
                {
                    double interval = Elapsed(input, previous);
                    if (interval < 0) Reject(press, OutOfOrder);
                    else if (interval < rapidTapMs) Reject(press, RapidTap);
                }
                // 连续连点都参与保护；Rejected 会明确交给主窗口停住，绝不静默吞键。
                lastTapUp = input;
            }
            return new(press.Down, input, press.Reason.Length == 0, press.Reason);
        }
    }
    public void ObserveForeground(IntPtr foreground, long timestamp, uint? messageTime = null)
    {
        lock (sync)
        {
            foreach (var press in presses.Values)
            {
                double elapsed = messageTime.HasValue && press.Down.MessageTime.HasValue
                    ? unchecked((int)(messageTime.Value - press.Down.MessageTime.Value)) : Milliseconds(timestamp - press.Down.Timestamp);
                if (elapsed >= 0 && foreground != press.Down.Foreground) Reject(press, ForegroundChanged);
            }
        }
    }
    public void Invalidate(string reason = InputUnavailable) { lock (sync) foreach (var press in presses.Values) Reject(press, reason); }
    public void RemoveDevice(IntPtr device) { lock (sync) { presses.Remove(device); lastInputs.Remove(device); } }
    public void Clear() { lock (sync) { presses.Clear(); lastInputs.Clear(); } }
}
