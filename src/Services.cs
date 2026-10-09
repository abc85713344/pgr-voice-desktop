using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace PgrVoice;
public sealed class AudioService : IDisposable
{
    readonly object audioGate = new();
    readonly AudioDeviceNotifications notifications;
    NAudio.CoreAudioApi.MMDeviceEnumerator? deviceObserver;
    NAudio.CoreAudioApi.MMDevice? activeDevice;
    WasapiOut? output;
    AudioFileReader? reader;
    CompletionTrackingProvider? playbackSource;
    readonly Timer positionTimer;
    long currentRequestId;
    double positionSeconds, durationSeconds;
    float volume = .8f;
    int volumeVersion, volumeApplyQueued;
    bool disposed;
    string selectedDeviceId = "", currentDeviceName = "跟随系统默认设备";
    string? activeDeviceId, lastError;
    // 界面只读取已发布的状态，不等待音频设备打开、停止或释放。
    sealed record PlaybackSnapshot(bool Playing, string SelectedDeviceId, string CurrentDeviceName, string? LastError, double PositionSeconds, double DurationSeconds);
    PlaybackSnapshot snapshot = new(false, "", "跟随系统默认设备", null, 0, 0);
    public event Action<string>? PlaybackFailed;
    public event Action<long, string>? PlaybackRequestFailed;
    public event Action<long>? PlaybackCompleted;
    public event Action? DevicesChanged;
    public event Action? StateChanged;
    public bool Playing => Volatile.Read(ref snapshot).Playing;
    public string SelectedDeviceId => Volatile.Read(ref snapshot).SelectedDeviceId;
    public string CurrentDeviceName => Volatile.Read(ref snapshot).CurrentDeviceName;
    public string? LastError => Volatile.Read(ref snapshot).LastError;
    public double PositionSeconds => Volatile.Read(ref snapshot).PositionSeconds;
    public double DurationSeconds => Volatile.Read(ref snapshot).DurationSeconds;
    public float Volume
    {
        get => Volatile.Read(ref volume);
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref volume, Math.Clamp(value, 0, 1));
            Interlocked.Increment(ref volumeVersion);
            if (Monitor.TryEnter(audioGate))
            {
                try { if (!disposed && reader != null) reader.Volume = Volatile.Read(ref volume); }
                finally { Monitor.Exit(audioGate); }
            }
            else QueueVolumeApply();
        }
    }
    void QueueVolumeApply()
    {
        if (Interlocked.CompareExchange(ref volumeApplyQueued, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                int appliedVersion;
                lock (audioGate)
                {
                    appliedVersion = Volatile.Read(ref volumeVersion);
                    if (!disposed && reader != null) reader.Volume = Volatile.Read(ref volume);
                }
                Interlocked.Exchange(ref volumeApplyQueued, 0);
                // 调整恰好发生在写入和撤销排队标记之间时，再应用最新值。
                if (appliedVersion == Volatile.Read(ref volumeVersion) || Interlocked.CompareExchange(ref volumeApplyQueued, 1, 0) != 0) return;
            }
        });
    }
    // 除构造期间外，状态变更与发布都在 audioGate 内完成。
    void PublishSnapshot() => Volatile.Write(ref snapshot, new(output?.PlaybackState == PlaybackState.Playing, selectedDeviceId, currentDeviceName, lastError, positionSeconds, durationSeconds));
    public AudioService()
    {
        positionTimer = new Timer(UpdatePosition, null, 100, 100);
        notifications = new AudioDeviceNotifications(OnDevicesChanged);
        try
        {
            deviceObserver = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            deviceObserver.RegisterEndpointNotificationCallback(notifications);
        }
        catch (Exception ex) { lastError = "无法监听声音设备：" + ex.Message; deviceObserver?.Dispose(); deviceObserver = null; PublishSnapshot(); }
    }
    public IReadOnlyList<AudioDeviceOption> GetDevices()
    {
        var result = new List<AudioDeviceOption> { new("", "跟随系统默认设备") };
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        catch (Exception ex) { lock (audioGate) { lastError = "无法读取声音设备：" + ex.Message; PublishSnapshot(); } }
        string selected = SelectedDeviceId;
        if (selected.Length > 0 && !result.Any(d => d.Id == selected)) result.Add(new(selected, "已选择的设备暂不可用", false));
        return result;
    }
    public void SelectDevice(string? id)
    {
        string selected = id ?? "";
        lock (audioGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (selected == selectedDeviceId) return;
            StopCore(); selectedDeviceId = selected; lastError = null;
            currentDeviceName = selected.Length == 0 ? "跟随系统默认设备" : "已选择声音设备";
            PublishSnapshot();
        }
        var device = GetDevices().First(d => d.Id == selected);
        lock (audioGate) { currentDeviceName = device.Name; if (!device.Available) lastError = "已选择的声音设备不可用，请重新连接或选择其他设备。"; PublishSnapshot(); }
        StateChanged?.Invoke();
    }
    public void Play(string file, long requestId = 0, double startSeconds = 0, Func<bool>? shouldPlay = null)
    {
        string? failure = null;
        try
        {
            lock (audioGate)
            {
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (shouldPlay != null && !shouldPlay()) return;
                    StopCore(); currentRequestId = requestId; positionSeconds = durationSeconds = 0; lastError = null; PublishSnapshot();
                    if (!File.Exists(file)) throw new FileNotFoundException("这一句音频文件缺失", file);
                    reader = new AudioFileReader(file) { Volume = volume };
                    if (reader.Length == 0 || reader.TotalTime <= TimeSpan.Zero) throw new InvalidDataException("这一句音频没有可播放的内容");
                    durationSeconds = reader.TotalTime.TotalSeconds;
                    if (!double.IsFinite(startSeconds)) throw new ArgumentOutOfRangeException(nameof(startSeconds));
                    positionSeconds = Math.Clamp(startSeconds, 0, Math.Max(0, durationSeconds - .1));
                    reader.CurrentTime = TimeSpan.FromSeconds(positionSeconds);
                    playbackSource = new CompletionTrackingProvider(reader);
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    activeDevice = selectedDeviceId.Length == 0
                        ? enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia)
                        : enumerator.GetDevice(selectedDeviceId);
                    if (activeDevice.State != NAudio.CoreAudioApi.DeviceState.Active) throw new InvalidOperationException("选中的声音设备不可用，请重新连接或选择其他设备。");
                    activeDeviceId = activeDevice.ID; currentDeviceName = activeDevice.FriendlyName;
                    output = new WasapiOut(activeDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
                    output.PlaybackStopped += OnPlaybackStopped;
                    output.Init(playbackSource);
                    // 打开设备期间可能已经换句。发声前再核对，跳过过期请求。
                    if (shouldPlay != null && !shouldPlay()) { StopCore(); PublishSnapshot(); return; }
                    output.Play(); PublishSnapshot();
                }
                catch (Exception ex)
                {
                    // 失败清理与本次播放共用一段锁，不能误停止随后开始的新播放。
                    StopCore(); failure = lastError = "无法播放配音：" + ex.Message; PublishSnapshot(); throw;
                }
            }
        }
        catch
        {
            if (failure != null) PlaybackRequestFailed?.Invoke(requestId, failure);
            StateChanged?.Invoke(); throw;
        }
        StateChanged?.Invoke();
    }
    void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        // 释放放到独立任务中，避免在音频线程上等待自身结束。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? error = null;
            long request;
            bool completed;
            lock (audioGate)
            {
                if (disposed || !ReferenceEquals(output, sender)) return;
                // 同一输出在 Seek 后可能已经恢复播放，迟到的停止通知不能终止它。
                if (output?.PlaybackState != PlaybackState.Stopped) return;
                request = currentRequestId;
                completed = args.Exception == null && playbackSource?.Completed == true;
                if (args.Exception != null) error = lastError = "配音播放中断，请检查声音设备后重播当前句：" + args.Exception.Message;
                else if (!completed) error = lastError = "配音尚未播完就已停止，请重播当前句。";
                if (completed && reader != null) positionSeconds = reader.TotalTime.TotalSeconds;
                StopCore();
            }
            if (error != null) { PlaybackRequestFailed?.Invoke(request, error); PlaybackFailed?.Invoke(error); }
            if (completed) PlaybackCompleted?.Invoke(request);
            StateChanged?.Invoke();
        });
    }
    void OnDevicesChanged(string? disconnectedId, bool defaultChanged)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? error = null;
            long request = 0;
            bool interrupted = false;
            lock (audioGate)
            {
                if (disposed) return;
                bool disconnected = disconnectedId != null && (disconnectedId == activeDeviceId || disconnectedId == selectedDeviceId);
                bool changedWhilePlaying = defaultChanged && selectedDeviceId.Length == 0 && output != null;
                if (disconnected || changedWhilePlaying)
                {
                    request = currentRequestId; interrupted = output != null;
                    StopCore();
                    string nextError = disconnected ? "声音设备已断开。重新连接或选择其他设备后，可重播当前句。" : "系统默认声音设备已改变。已停止当前配音，请确认后重播。";
                    if (lastError != nextError) error = lastError = nextError;
                    PublishSnapshot();
                }
            }
            if (error != null) { if (interrupted) PlaybackRequestFailed?.Invoke(request, error); PlaybackFailed?.Invoke(error); }
            DevicesChanged?.Invoke(); StateChanged?.Invoke();
        });
    }
    void StopCore()
    {
        CapturePosition();
        var oldOutput = output; output = null;
        PublishSnapshot();
        if (oldOutput != null)
        {
            oldOutput.PlaybackStopped -= OnPlaybackStopped;
            try { oldOutput.Stop(); } catch { }
            try { oldOutput.Dispose(); } catch { }
        }
        var oldReader = reader; reader = null; playbackSource = null;
        var oldDevice = activeDevice; activeDevice = null; activeDeviceId = null;
        try { oldReader?.Dispose(); } catch { }
        try { oldDevice?.Dispose(); } catch { }
    }
    public void Stop() { lock (audioGate) StopCore(); StateChanged?.Invoke(); }
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (audioGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (reader == null || output == null || playbackSource == null) return;
            double target = Math.Clamp(seconds, 0, reader.TotalTime.TotalSeconds);
            // 主动跳到末尾属于定位操作，不能冒充自然完成而点击游戏或跳下一句。
            if (target >= reader.TotalTime.TotalSeconds)
            {
                StopCore(); positionSeconds = target; PublishSnapshot();
            }
            else
            {
                playbackSource.Seek(target); positionSeconds = target;
                if (output.PlaybackState == PlaybackState.Stopped) output.Play();
                PublishSnapshot();
            }
        }
        StateChanged?.Invoke();
    }
    void CapturePosition()
    {
        if (reader == null) return;
        try { positionSeconds = Math.Clamp(reader.CurrentTime.TotalSeconds, 0, reader.TotalTime.TotalSeconds); }
        catch (ObjectDisposedException) { }
    }
    void UpdatePosition(object? state)
    {
        // 设备打开/关闭可能很慢。后台采样跳过被占用的锁，界面始终只读快照。
        if (!Monitor.TryEnter(audioGate)) return;
        try
        {
            if (disposed || reader == null) return;
            CapturePosition(); PublishSnapshot();
        }
        finally { Monitor.Exit(audioGate); }
    }
    public void Dispose()
    {
        positionTimer.Dispose();
        lock (audioGate) { if (disposed) return; disposed = true; StopCore(); }
        if (deviceObserver != null)
        {
            try { deviceObserver.UnregisterEndpointNotificationCallback(notifications); } catch { }
            deviceObserver.Dispose(); deviceObserver = null;
        }
    }

    // EOF 只描述输入读取结束；须等当前输出的 PlaybackStopped 才能认定实际播放结束。
    sealed class CompletionTrackingProvider : IWaveProvider
    {
        readonly AudioFileReader source;
        readonly object sourceGate = new();
        int completed;
        bool readAudio;
        public CompletionTrackingProvider(AudioFileReader source) => this.source = source;
        public WaveFormat WaveFormat => source.WaveFormat;
        public bool Completed => Volatile.Read(ref completed) != 0;
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (sourceGate)
            {
                int read = source.Read(buffer, offset, count);
                if (read > 0) readAudio = true;
                else if (count > 0 && readAudio) Volatile.Write(ref completed, 1);
                return read;
            }
        }
        public void Seek(double seconds)
        {
            lock (sourceGate)
            {
                source.CurrentTime = TimeSpan.FromSeconds(seconds);
                readAudio = false; Volatile.Write(ref completed, 0);
            }
        }
    }
}
public sealed class Preferences
{
    public int ProgressSchema { get; set; } = 1;
    public List<Visit> Visits { get; set; } = new();
    public int VisitPosition { get; set; } = -1;
    public HashSet<string> Facts { get; set; } = new();
    public HashSet<string> Heard { get; set; } = new();
    public Dictionary<string,int> MenuSelections { get; set; } = new();
    public string PackFile { get; set; } = "";
    public string PackId { get; set; } = "";
    public string? NodeId { get; set; }
    public Dictionary<string, string> Choices { get; set; } = new();
    public double Volume { get; set; } = 80;
    public Dictionary<string, int> SpeakerVolumes { get; set; } = new();
    public bool SmartListeningResume { get; set; } = true;
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 100;
    public double PanelWidth { get; set; } = 580;
    public double PanelHeight { get; set; } = 760;
    public bool PanelSizeCustomized { get; set; }
    public bool OcrEnabled { get; set; }
    public bool DialogueGuardEnabled { get; set; }
    public DialogueRegion DialogueRegion { get; set; } = new();
    public bool ShowArtwork { get; set; } = true;
    public double ReadingScale { get; set; } = 1;
    public string CompactMode { get; set; } = "ball";
    public bool CompactControlsEnabled { get; set; } = true;
    public string OutputDeviceId { get; set; } = "";
    public string GameProcess { get; set; } = "";
    public string GameExecutablePath { get; set; } = "";
    public bool GamepadEnabled { get; set; } = true;
    public bool GamepadFollowEnabled { get; set; }
    public string GamepadDeviceName { get; set; } = "";
    public string GamepadGlyphStyle { get; set; } = "auto";
    public string GamepadAdvanceButton { get; set; } = "South";
    public string GamepadModifier { get; set; } = "Back";
    public Dictionary<string, string> GamepadBindings { get; set; } = new()
    {
        ["panel"]="Start", ["pause"]="East", ["replay"]="West", ["ocr"]="North",
        ["previous"]="DPadLeft", ["manualNext"]="DPadRight", ["interactions"]="DPadUp",
        ["history"]="DPadDown", ["automatic"]="RightShoulder", ["original"]="None"
    };
    // 「下一句」点击热区：只有点在这个框里才跟着推进，游戏里点选项/技能不会误跟随。
    public bool ClickZoneEnabled { get; set; }
    public bool MouseFollowEnabled { get; set; } = true;
    public double AutomaticDelaySeconds { get; set; } = 1;
    public bool TextAutoAdvanceEnabled { get; set; }
    public double TextAutoDelaySeconds { get; set; } = 1.5;
    public double ClickZoneLeft { get; set; } = -1;   // 小于 0 表示还没定位过
    public double ClickZoneTop { get; set; } = -1;
    public Dictionary<string, string> Keys { get; set; } = new()
    {
        ["next"]="Space", ["previous"]="PageUp", ["manualNext"]="PageDown", ["replay"]="F5", ["panel"]="F6", ["pause"]="F7", ["original"]="F8", ["ocr"]="F9", ["catalog"]="F4", ["reselect"]="F3", ["history"]="F2", ["interactions"]="F1"
    };
}
public sealed class OcrResponse
{
    public string RequestId { get; set; } = "";
    public string? Error { get; set; }
    public List<OcrBlock> Blocks { get; set; } = new();
    public double ElapsedMs { get; set; }
    public string Variant { get; set; } = "";
}
public sealed class OcrService : IDisposable
{
    Process? worker;
    readonly SemaphoreSlim mutex = new(1, 1);
    readonly Timer idleTimer;
    DateTime lastUsed = DateTime.MinValue;
    public bool Running => worker is { HasExited: false };
    public string WorkerFile { get; }
    public OcrService(string file) { WorkerFile = file; idleTimer = new Timer(_ => { if (DateTime.UtcNow - lastUsed > TimeSpan.FromMinutes(2) && mutex.CurrentCount == 1) Stop(); }, null, 10000, 10000); }
    public async Task<OcrResponse> Recognize(string file, double[]? region, string variant, CancellationToken token)
    {
        await mutex.WaitAsync(token);
        try
        {
            lastUsed = DateTime.UtcNow;
            if (!Running)
            {
                if (!File.Exists(WorkerFile)) throw new FileNotFoundException("未安装 OCR 组件。将组件解压到程序的 ocr 文件夹后重试。");
                var info = new ProcessStartInfo(WorkerFile) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(WorkerFile)! };
                worker = Process.Start(info) ?? throw new InvalidOperationException("无法启动 OCR");
                worker.ErrorDataReceived += (_, args) => { if (args.Data != null) Log.Write("ocr", args.Data); };
                worker.BeginErrorReadLine();
            }
            string requestId = Guid.NewGuid().ToString("N");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { requestId, imagePath = file, region, variant }, Json.Options).Replace("\r", "").Replace("\n", ""));
            await worker.StandardInput.FlushAsync();
            var line = await worker.StandardOutput.ReadLineAsync(timeout.Token);
            if (line == null) throw new IOException("OCR 组件意外退出");
            var response = JsonSerializer.Deserialize<OcrResponse>(line, Json.Options) ?? throw new IOException("OCR 返回格式无效");
            if (response.RequestId != requestId) throw new IOException("已丢弃过期的识别结果");
            if (response.Error != null) throw new IOException(response.Error);
            lastUsed = DateTime.UtcNow;
            return response;
        }
        catch { Stop(); throw; }
        finally { mutex.Release(); }
    }
    public void Stop()
    {
        var process = Interlocked.Exchange(ref worker, null);
        if (process != null) { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); }
    }
    public void Dispose() { idleTimer.Dispose(); Stop(); }
}
public static class Log
{
    public static string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgrStoryVoice");
    static readonly object sync = new();
    public static void Write(string category, string message)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "player.log"), $"{DateTime.Now:O}\t{category}\t{message}\n");
            }
        }
        catch { }
    }
}
