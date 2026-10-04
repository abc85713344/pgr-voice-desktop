using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PgrVoice;

public sealed record DialogueObservation(DialogueFrameVerdict Verdict, string Reason);

// 单后台循环维护按键前基线，所有截图留在内存。配置或窗口变化使旧任务失效。
public sealed class DialogueFrameMonitor : IDisposable
{
    readonly object sync = new();
    readonly CancellationTokenSource stop = new();
    readonly Func<IntPtr, DialogueRegion, CapturedDialogueFrame> capture;
    readonly Func<IntPtr, bool> isForeground;
    readonly Task loop;
    IntPtr window;
    DialogueRegion region = new();
    long version;
    CapturedDialogueFrame? baseline;
    Pending? pending;
    string? lastCaptureFailure;
    bool disposed;
    sealed class Pending
    {
        public required CapturedDialogueFrame Before;
        public long? SampleTimestamp;
        public Task<bool>? InputConfirmation;
        public required long Version;
        public readonly List<DialogueFrameMask> Frames = new();
        public readonly TaskCompletionSource<DialogueObservation> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    static readonly double[] SampleTimes = { 80, 180, 350, 650 };
    public DialogueFrameMonitor(Func<IntPtr, DialogueRegion, CapturedDialogueFrame>? capture = null,
        Func<IntPtr, bool>? isForeground = null)
    {
        this.capture = capture ?? DialogueFrameCapture.Capture;
        this.isForeground = isForeground ?? (handle => Native.IsWindow(handle) && !Native.IsIconic(handle) && Native.GetForegroundWindow() == handle);
        loop = Task.Run(Run);
    }
    public void Configure(IntPtr handle, DialogueRegion area)
    {
        lock (sync)
        {
            if (disposed) return;
            if (window == handle && region == area) return;
            Invalidate("跟随状态已改变"); lastCaptureFailure = null; window = handle; region = area;
        }
    }
    public void Reset()
    { lock (sync) { if (!disposed) { Invalidate("已重新建立跟随位置"); lastCaptureFailure = null; } } }
    void Invalidate(string reason)
    {
        version++; baseline = null;
        pending?.Done.TrySetResult(new(DialogueFrameVerdict.Uncertain, reason)); pending = null;
    }
    public Task<DialogueObservation> Observe(long timestamp, Task<bool>? inputConfirmation = null)
    {
        lock (sync)
        {
            if (disposed) return Task.FromResult(new DialogueObservation(DialogueFrameVerdict.Uncertain, "检测已停止"));
            if (pending != null) { Invalidate("连续快按，无法确认推进了几句"); return Task.FromResult(new DialogueObservation(DialogueFrameVerdict.Uncertain, "连续快按，无法确认推进了几句")); }
            double lag = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            double baselineAge = baseline == null ? double.PositiveInfinity : Stopwatch.GetElapsedTime(baseline.Timestamp, timestamp).TotalMilliseconds;
            // Timestamp 是截图开始时刻；截图结束必须早于输入，避免把跨过按键的画面当成前帧。
            // 多留 1ms 余量覆盖 CaptureMs 测量到返回之间的微小时间差。
            if (window == IntPtr.Zero || lag < 0 || lag > 200 || baseline == null ||
                !double.IsFinite(baseline.CaptureMs) || baseline.CaptureMs < 0 ||
                baselineAge <= baseline.CaptureMs + 1 || baselineAge > 200)
                return Task.FromResult(new DialogueObservation(DialogueFrameVerdict.Uncertain,
                    baseline == null && lastCaptureFailure != null ? lastCaptureFailure : "缺少及时的按键前画面，请核对当前句"));
            if (!CheckForeground(window, version))
                return Task.FromResult(new DialogueObservation(DialogueFrameVerdict.Uncertain, "游戏不在前台"));
            // 鼠标在按下时冻结前帧，但只有完整轻点通过后才开始后帧采样。
            // 键盘没有松开确认，仍以原始输入时刻为采样起点。
            pending = new Pending { Before = baseline, SampleTimestamp = inputConfirmation == null ? timestamp : null,
                InputConfirmation = inputConfirmation, Version = version };
            return pending.Done.Task;
        }
    }
    bool Current(IntPtr handle, long revision, Pending? expected)
    { lock (sync) return !disposed && revision == version && handle == window && pending == expected; }
    bool CheckForeground(IntPtr handle, long revision)
    {
        bool foreground;
        try { foreground = isForeground(handle); } catch { foreground = false; }
        if (foreground) return true;
        lock (sync)
        {
            if (revision == version && handle == window)
            {
                lastCaptureFailure = "游戏不在前台，已停止本次判断";
                Invalidate(lastCaptureFailure);
            }
        }
        return false;
    }
    async Task<bool> WaitForSample(IntPtr handle, long revision, Pending active)
    {
        while (Current(handle, revision, active))
        {
            if (!CheckForeground(handle, revision)) return false;
            if (active.SampleTimestamp == null)
            {
                var confirmation = active.InputConfirmation!;
                if (!confirmation.IsCompleted)
                {
                    await Task.Delay(20, stop.Token);
                    continue;
                }
                bool accepted = confirmation.IsCompletedSuccessfully && confirmation.Result;
                // 读取异常，使失败的外部确认任务也按取消处理，不留下未观察异常。
                if (confirmation.IsFaulted) _ = confirmation.Exception;
                lock (sync)
                {
                    if (!Current(handle, revision, active)) return false;
                    if (!accepted) { Invalidate("本次轻点未确认，已取消判断"); return false; }
                    active.SampleTimestamp = Stopwatch.GetTimestamp();
                }
            }
            double wait = SampleTimes[active.Frames.Count] - Stopwatch.GetElapsedTime(active.SampleTimestamp.Value).TotalMilliseconds;
            if (wait <= 0) return true;
            // 只检查窗口状态，不增加截图；短暂切出再切回也使本次输入失效。
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(20, wait)), stop.Token);
        }
        return false;
    }
    async Task WaitForBaselineInterval(IntPtr handle, long revision)
    {
        for (int step = 0; step < 6; step++)
        {
            await Task.Delay(20, stop.Token);
            // 输入到达时立即结束空闲等待，使前台检查不会被整段 120ms 等待挡住。
            lock (sync) if (pending != null || revision != version || handle != window || disposed) return;
        }
    }
    async Task Run()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IntPtr handle; DialogueRegion area; long revision; Pending? active;
                lock (sync) { handle = window; area = region; revision = version; active = pending; }
                if (handle == IntPtr.Zero) { await Task.Delay(100, stop.Token); continue; }
                if (active != null)
                {
                    if (!await WaitForSample(handle, revision, active)) continue;
                }
                else if (!CheckForeground(handle, revision)) { await Task.Delay(120, stop.Token); continue; }
                try
                {
                    var frame = capture(handle, area);
                    if (!CheckForeground(handle, revision)) continue;
                    lock (sync)
                    {
                        if (revision != version || handle != window) continue;
                        // Observe 可能在截图期间开始，不能拿按键后的帧覆盖按键前基线。
                        if (pending != active) continue;
                        if (active == null) { baseline = frame; lastCaptureFailure = null; }
                        else
                        {
                            double elapsed = Stopwatch.GetElapsedTime(active.SampleTimestamp!.Value, frame.Timestamp).TotalMilliseconds;
                            if (frame.Geometry != active.Before.Geometry || elapsed - SampleTimes[active.Frames.Count] > 180 || frame.CaptureMs > 180)
                            {
                                lastCaptureFailure = "窗口移动或采样迟到，请核对当前句";
                                Invalidate(lastCaptureFailure);
                            }
                            else
                            {
                                active.Frames.Add(frame.Mask with { ElapsedMs = elapsed });
                                if (active.Frames.Count == SampleTimes.Length)
                                {
                                    var result = DialogueFrameAnalysis.Analyze(active.Before.Mask with { ElapsedMs = 0 }, active.Frames);
                                    baseline = frame; lastCaptureFailure = null; pending = null;
                                    active.Done.TrySetResult(new(result.Verdict, result.Reason));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        if (revision == version)
                        {
                            lastCaptureFailure = ex.Message; Invalidate(ex.Message);
                            // 此次失败自身导致的版本变化仍按空闲频率重试，避免持续遮挡时忙循环。
                            revision = version;
                        }
                    }
                }
                if (active == null) await WaitForBaselineInterval(handle, revision);
                else await Task.Delay(1, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; window = IntPtr.Zero; Invalidate("检测已停止");
        }
        stop.Cancel();
        // 不在 UI 上等待截图驱动；任务退出后释放取消源。
        _ = loop.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
