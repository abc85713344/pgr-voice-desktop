using System;
using System.Diagnostics;

namespace PgrVoice;

public enum FollowInputSource { None, Keyboard, Mouse, Gamepad }
public enum FollowInputDecision { Accept, Duplicate, Stale }

// 只按时间关联跨来源输入，不能识别 Steam/模拟器映射的真实身份。
// 同来源新按压交给上层处理；上下文、前台、未完成手势和画面检查由调用方管理。
public sealed class FollowInputGate
{
    readonly object sync = new();
    readonly long windowTicks;
    FollowInputSource rememberedSource, activeSource;
    long rememberedTimestamp;

    public FollowInputGate(double windowMs = 220)
    {
        double ticks = windowMs * Stopwatch.Frequency / 1000d;
        if (!double.IsFinite(windowMs) || windowMs < 0 || !double.IsFinite(ticks) || ticks >= long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(windowMs));
        windowTicks = (long)Math.Ceiling(ticks);
    }

    public FollowInputSource ActiveSource { get { lock (sync) return activeSource; } }
    public long RememberedTimestamp { get { lock (sync) return rememberedTimestamp; } }

    public FollowInputDecision TryAccept(FollowInputSource source, long timestamp)
    {
        if (source is not (FollowInputSource.Keyboard or FollowInputSource.Mouse or FollowInputSource.Gamepad) || timestamp <= 0)
            return FollowInputDecision.Stale;
        lock (sync)
        {
            if (rememberedTimestamp != 0)
            {
                // 两个时间戳均为正数；按大小顺序相减，不对可能溢出的有符号差取 Abs。
                long distance = timestamp >= rememberedTimestamp ? timestamp - rememberedTimestamp : rememberedTimestamp - timestamp;
                if (source == rememberedSource && timestamp < rememberedTimestamp) return FollowInputDecision.Stale;
                if (timestamp < rememberedTimestamp && distance > windowTicks) return FollowInputDecision.Stale;
                if (timestamp == rememberedTimestamp || source != rememberedSource && distance <= windowTicks)
                {
                    // 只调整显示来源，不移动关联时间或原接受来源，避免副本续期及污染同源按压。
                    if (source == FollowInputSource.Gamepad) activeSource = FollowInputSource.Gamepad;
                    return FollowInputDecision.Duplicate;
                }
            }
            rememberedSource = activeSource = source;
            rememberedTimestamp = timestamp;
            return FollowInputDecision.Accept;
        }
    }

    // 仅由调用方在有记录的推进按压真正结束时调用，不能用中性轮询不断续期。
    // 释放只延长这次手柄操作的副本关联，不表示新增一次推进。
    public void GamepadReleased(long timestamp)
    {
        lock (sync)
        {
            if (activeSource != FollowInputSource.Gamepad || timestamp <= 0 || timestamp < rememberedTimestamp) return;
            rememberedSource = FollowInputSource.Gamepad;
            rememberedTimestamp = timestamp;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            rememberedSource = activeSource = FollowInputSource.None;
            rememberedTimestamp = 0;
        }
    }
}
