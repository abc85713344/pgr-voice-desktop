using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PgrVoice;

public sealed record GameTextField(ulong Object, ulong Field, ulong Class, ulong Native, int Instance, string Name);
public sealed record GameTextSample(bool Valid, string Text, bool Changed, string Reason = "", bool Active = false, string Speaker = "");
public enum GameTextKind { Ordinary, Scene3D }

// 只申请查询和读取权限。无游戏内代码、远程调用、写入或持久化地址。
// IL2CPP 布局仅限当前已实测的 64 位客户端；所有对象和字符串每次重新校验。
public sealed class GameTextReader : IDisposable
{
    const uint QueryAndRead = 0x0400 | 0x0010;
    readonly SafeProcessHandle process;
    readonly object sync = new();
    List<GameTextField> fields = new();
    readonly List<GameTextField> speakerFields = new();
    ulong stringClass;
    readonly HashSet<ulong> verifiedClasses = new();
    readonly List<string> bindTrace = new();
    readonly Dictionary<string, int> bodyCandidates = new();
    sealed record TextTransform(ulong Object, ulong Class, ulong Native, int Instance, string Name);
    readonly List<TextTransform> fullScreenMarkers = new();
    readonly HashSet<ulong> verifiedTransformClasses = new();
    string lastText = "", lastSpeaker = "";
    bool disposed;
    bool automaticScene;
    public int ProcessId { get; }
    public int FieldCount => fields.Count;
    public int SpeakerFieldCount => speakerFields.Count;
    public string ConnectionDiagnostics { get; private set; } = "";

    [StructLayout(LayoutKind.Sequential)]
    struct Region
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtect, Alignment1;
        public ulong Size;
        public uint State, Protect, Type, Alignment2;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(SafeProcessHandle process, ulong address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] static extern nuint VirtualQueryEx(SafeProcessHandle process, ulong address, out Region info, nuint size);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    public static int WindowProcessId(IntPtr window) { GetWindowThreadProcessId(window, out int pid); return pid; }
    public GameTextReader(int pid)
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("文本追踪需要 64 位播放器。");
        ProcessId = pid; process = OpenProcess(QueryAndRead, false, pid);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); // 必须在释放句柄或其他系统调用之前保存。
            process.Dispose(); throw new Win32Exception(error, "无法以只读方式连接游戏进程。");
        }
    }
    byte[] Read(ulong address, int count)
    {
        if (address < 0x10000 || address > 0x00007fffffffffff || count <= 0 || count > 1024 * 1024 + 16384) return Array.Empty<byte>();
        var data = new byte[count];
        return ReadProcessMemory(process, address, data, (nuint)count, out var read) && read == (nuint)count ? data : Array.Empty<byte>();
    }
    ulong Ptr(ulong address) { var b = Read(address, 8); return b.Length == 8 ? BitConverter.ToUInt64(b) : 0; }
    string Name(ulong address)
    {
        var data = Read(address, 128); int n = Array.IndexOf(data, (byte)0);
        if (n < 0 || n > 100) return "";
        return Encoding.ASCII.GetString(data, 0, n);
    }
    bool ClassIs(ulong klass, string name, string space) => klass >= 0x10000 && klass % 8 == 0 &&
        Name(Ptr(klass + 16)) == name && Name(Ptr(klass + 24)) == space;
    int? IntAt(ulong address) { var b = Read(address, 4); return b.Length == 4 ? BitConverter.ToInt32(b) : null; }
    ulong Method(ulong klass, string name)
    {
        ulong methods = Ptr(klass + 0x98);
        for (ulong i = 0; i < 512; i++)
        {
            ulong method = Ptr(methods + i * 8);
            if (method == 0 || Ptr(method + 0x20) != klass) break;
            if (Name(Ptr(method + 0x18)) == name) return Ptr(method);
        }
        return 0;
    }
    byte[] IcallCode(ulong method)
    {
        // 仅解析只读机器码中的已缓存函数指针，从不执行或修改游戏代码。
        var wrapper = Read(method, 32);
        for (int i = 0; i + 7 <= wrapper.Length; i++)
            if (wrapper[i] == 0x48 && wrapper[i + 1] == 0x8b && wrapper[i + 2] == 0x05)
                return Read(Ptr((ulong)((long)method + i + 7 + BitConverter.ToInt32(wrapper, i + 3))), 0x180);
        return Array.Empty<byte>();
    }
    void VerifyLayout(ulong klass, ulong textOffset)
    {
        if (verifiedClasses.Contains(klass)) return;
        bool textField = false, canvasField = false, activeGetter = false, gameObjectGetter = false;
        for (ulong current = klass, depth = 0; current != 0 && depth < 16; current = Ptr(current + 0x58), depth++)
        {
            ulong field = Ptr(current + 0x80);
            for (ulong i = 0; i < 512; i++)
            {
                ulong entry = field + i * 32;
                if (Ptr(entry + 16) != current) break;
                if (Name(Ptr(entry)) == "m_Text" && IntAt(entry + 24) == (int)textOffset) textField = true;
                if (Name(Ptr(entry)) == "m_Canvas" && IntAt(entry + 24) == 0x60) canvasField = true;
            }
            if (ClassIs(current, "Behaviour", "UnityEngine"))
            {
                var code = IcallCode(Method(current, "get_isActiveAndEnabled"));
                activeGetter = code.AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x41, 0x10 }) >= 0 &&
                    code.AsSpan().IndexOf(new byte[] { 0x80, 0x78, 0x39, 0x00 }) >= 0 &&
                    code.AsSpan().IndexOf(new byte[] { 0x0f, 0x95, 0xc0 }) >= 0;
            }
            if (ClassIs(current, "Component", "UnityEngine"))
                gameObjectGetter = IcallCode(Method(current, "get_gameObject")).AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x52, 0x30 }) >= 0;
        }
        if (!textField || !canvasField || !activeGetter || !gameObjectGetter)
            throw new InvalidOperationException("当前游戏的文字或界面状态布局尚未适配，已停止连接；不会使用旧偏移猜测。");
        verifiedClasses.Add(klass);
    }
    GameTextField? Component(ulong obj, GameTextKind kind, bool speaker = false)
    {
        void Count(string reason)
        {
            if (!speaker) bodyCandidates[reason] = bodyCandidates.GetValueOrDefault(reason) + 1;
        }
        ulong klass = Ptr(obj), offset = kind == GameTextKind.Ordinary && !speaker ? 0xf0UL : 0xe8UL;
        if (!(offset == 0xf0 ? ClassIs(klass, "XUiRichTextCustomRender", "XUiComponent") : ClassIs(klass, "Text", "UnityEngine.UI"))) return null;
        ulong native = Ptr(obj + 0x10);
        if (native == 0 || Ptr(native + 0x28) != obj || IntAt(native + 8) is not int instance) { Count("非存活组件引用"); return null; }
        string name = Name(Ptr(Ptr(native + 0x30) + 0x60));
        Count("存活控件:" + ShortText(name));
        if (speaker)
        {
            if (name != (kind == GameTextKind.Ordinary ? "TxtName" : "TxtRoleName")) return null;
        }
        else
        {
            if (name.Length == 0 || (kind == GameTextKind.Ordinary && name != "TxtWords")) return null;
            if (name is "TxtPress" or "TxtNormal" or "TxtDisable" or "TxtName" or "TxtRoleName") return null;
        }
        VerifyLayout(klass, offset);
        ulong text = Ptr(obj + offset), textClass = Ptr(text);
        if (!ClassIs(textClass, "String", "System")) { Count("正文字段不是有效字符串"); return null; }
        stringClass = textClass;
        Count("通过正文校验");
        return new(obj, obj + offset, klass, native, instance, name);
    }
    void DiscoverSpeakers(GameTextKind kind, CancellationToken token)
    {
        foreach (ulong obj in FindUiTextObjects(token))
            if (Component(obj, kind, speaker: true) is { } component) speakerFields.Add(component);
    }
    ulong[] FindUiTextObjects(CancellationToken token)
    {
        var namespaces = Find(new[] { Encoding.ASCII.GetBytes("UnityEngine.UI\0") }, token);
        if (namespaces.Count == 0) return Array.Empty<ulong>();
        var classes = Find(namespaces.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true)
            .Where(p => p % 8 == 0 && ClassIs(p - 24, "Text", "UnityEngine.UI")).Select(p => p - 24).Distinct().ToArray();
        if (classes.Length == 0) return Array.Empty<ulong>();
        return Find(classes.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true).Where(p => p % 8 == 0).ToArray();
    }
    ulong Canvas(GameTextField field)
    {
        ulong canvas = Ptr(field.Object + 0x60);
        return ClassIs(Ptr(canvas), "Canvas", "UnityEngine") && Ptr(Ptr(canvas + 0x10) + 0x28) == canvas ? canvas : 0;
    }
    string NativeName(ulong native) => Name(Ptr(Ptr(native + 0x30) + 0x60));
    bool IsFullScreen(GameTextField field) => NativeName(Ptr(Canvas(field) + 0x10)) == "PanelFullScreenDialog";
    bool IsAutomaticSceneBody(GameTextField field)
    {
        ulong canvas = Canvas(field);
        bool dormant = Read(field.Native + 0x39, 1).AsSpan().SequenceEqual(new byte[] { 0 });
        // 已实测的营地/剧情角色对白。其他 Text（任务、选项、说明）不纳入自动探针。
        if (field.Name == "TxtRoleTalk") return canvas != 0 || dormant;
        if (field.Name != "TxtDesc" || (canvas != 0 && NativeName(Ptr(canvas + 16)) != "UiGuideFight")) return false;
        // TxtDesc 是通用名称：战斗通讯还须逐层核对实际归属，不能只凭名字或正文匹配。
        if (!HasField(field.Class, "m_RectTransform", 0x50)) return false;
        ulong rect = Ptr(field.Object + 0x50), klass = Ptr(rect), parentClass = Ptr(klass + 0x58);
        if (!verifiedTransformClasses.Contains(klass))
        {
            if (!ClassIs(klass, "RectTransform", "UnityEngine") || !ClassIs(parentClass, "Transform", "UnityEngine")) return false;
            var code = IcallCode(Method(parentClass, "get_parent"));
            if (code.AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x41, 0x10 }) < 0 ||
                code.AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x90, 0x90, 0, 0, 0 }) < 0) return false;
            verifiedTransformClasses.Add(klass);
        }
        var transform = Transform(rect);
        if (transform == null || !SameTransform(transform) || Ptr(transform.Native + 0x30) != Ptr(field.Native + 0x30)) return false;
        ulong ancestor = transform.Native;
        foreach (string name in new[] { "PanelDescription", "PanelConversation", "SafeAreaContentPane", "UiGuideFight" })
        {
            ancestor = Ptr(ancestor + 0x90);
            if (NativeName(ancestor) != name) return false;
        }
        return (canvas == 0 ? dormant : Ptr(ancestor + 0x30) == Ptr(Ptr(canvas + 16) + 0x30)) && SameTransform(transform);
    }
    bool HasField(ulong klass, string name, int offset)
    {
        for (int depth = 0; klass != 0 && depth < 16; klass = Ptr(klass + 0x58), depth++)
        {
            ulong fields = Ptr(klass + 0x80);
            for (ulong i = 0; i < 512; i++)
            {
                ulong entry = fields + i * 32;
                if (Ptr(entry + 16) != klass) break;
                if (Name(Ptr(entry)) == name && IntAt(entry + 24) == offset) return true;
            }
        }
        return false;
    }
    bool SameTransform(TextTransform t) => Ptr(t.Object) == t.Class && Ptr(t.Object + 16) == t.Native &&
        Ptr(t.Native + 0x28) == t.Object && IntAt(t.Native + 8) == t.Instance && NativeName(t.Native) == t.Name;
    TextTransform? Transform(ulong obj)
    {
        ulong klass = Ptr(obj), native = Ptr(obj + 16);
        if (!verifiedTransformClasses.Contains(klass) || native == 0 || Ptr(native + 0x28) != obj || IntAt(native + 8) is not int instance) return null;
        return new(obj, klass, native, instance, NativeName(native));
    }
    void DiscoverFullScreenMarkers(CancellationToken token)
    {
        var bodies = fields.Where(IsFullScreen).ToArray();
        if (bodies.Length == 0) return;
        foreach (var body in bodies)
        {
            if (!HasField(body.Class, "m_RectTransform", 0x50)) throw new InvalidOperationException("全屏旁白的行位置布局尚未适配，请导出连接诊断。");
            ulong klass = Ptr(Ptr(body.Object + 0x50));
            if (verifiedTransformClasses.Contains(klass)) continue;
            ulong parentClass = Ptr(klass + 0x58);
            if (!ClassIs(klass, "RectTransform", "UnityEngine") || !ClassIs(parentClass, "Transform", "UnityEngine"))
                throw new InvalidOperationException("全屏旁白的行对象尚未就绪，请保持当前句后重试。");
            var code = IcallCode(Method(parentClass, "get_parent"));
            if (code.AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x41, 0x10 }) < 0 ||
                code.AsSpan().IndexOf(new byte[] { 0x48, 0x8b, 0x90, 0x90, 0, 0, 0 }) < 0)
                throw new InvalidOperationException("全屏旁白的父子关系布局尚未适配，请导出连接诊断。");
            verifiedTransformClasses.Add(klass);
        }
        foreach (ulong obj in Find(verifiedTransformClasses.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true).Where(p => p % 8 == 0))
            if (Transform(obj) is { Name: "ImgNext" } marker) fullScreenMarkers.Add(marker);
        bindTrace.Add($"全屏当前行标记={fullScreenMarkers.Count}");
    }
    bool IsCurrentFullScreenRow(GameTextField field)
    {
        // 游戏的 ShowOneContent 将 ImgNext 挂到当前行；打字期间箭头会隐藏，但归属不变。
        // 不按正文内容、字体颜色或内存地址排序猜测当前行。
        var transform = Transform(Ptr(field.Object + 0x50));
        if (transform == null || transform.Name != "TxtWords" || Ptr(transform.Native + 0x30) != Ptr(field.Native + 0x30)) return false;
        ulong row = Ptr(transform.Native + 0x90), contents = Ptr(row + 0x90);
        ulong canvasNative = Ptr(Canvas(field) + 0x10);
        if (NativeName(row) != "GridSingleDialog(Clone)" || NativeName(contents) != "PanleContents" ||
            Ptr(Ptr(contents + 0x90) + 0x30) != Ptr(canvasNative + 0x30)) return false;
        var markers = fullScreenMarkers.Where(m => SameTransform(m) && Ptr(m.Native + 0x90) == row).ToArray();
        return markers.Length == 1 && SameTransform(transform) && Ptr(transform.Native + 0x90) == row &&
            SameTransform(markers[0]) && Ptr(markers[0].Native + 0x90) == row;
    }
    string ReadSpeaker(IEnumerable<GameTextField> activeBodies)
    {
        var canvases = activeBodies.Select(Canvas).Distinct().ToArray();
        if (canvases.Length != 1 || canvases[0] == 0) return "";
        // 名字只从同一对白画布取；多个不同活动名字时不猜测，不借用其他界面角色。
        var names = speakerFields.Select(f => (Field: f, Value: ReadField(f)))
            .Where(s => s.Value.Valid && s.Value.Active && Canvas(s.Field) == canvases[0])
            .Select(s => s.Value.Text.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
        return names.Length == 1 ? names[0] : "";
    }
    (bool Valid, bool Active, string Text) ReadField(GameTextField f)
    {
        bool SameObject() => Ptr(f.Object) == f.Class && Ptr(f.Object + 16) == f.Native &&
            Ptr(f.Native + 0x28) == f.Object && IntAt(f.Native + 8) == f.Instance && Name(Ptr(Ptr(f.Native + 0x30) + 0x60)) == f.Name;
        if (!SameObject()) return (false, false, "");
        var before = Read(f.Native + 0x39, 1);
        if (before.Length != 1 || before[0] > 1) return (false, false, "");
        if (before[0] == 0) return (true, false, "");
        ulong pointer = Ptr(f.Field); string? text = StringAt(pointer);
        if (text == null) return (false, false, "");
        if (!SameObject() || Ptr(f.Field) != pointer || !Read(f.Native + 0x39, 1).AsSpan().SequenceEqual(before))
            return (true, false, "");
        return (true, true, text);
    }
    string? StringAt(ulong address)
    {
        if (address == 0 || Ptr(address) != stringClass) return null;
        var length = Read(address + 16, 4);
        if (length.Length != 4) return null;
        uint n = BitConverter.ToUInt32(length);
        if (n > 6000) return null;
        if (n == 0) return "";
        var text = Read(address + 20, checked((int)n * 2));
        if (text.Length != n * 2) return null;
        // 字符串在读取途中被替换或回收时丢弃整次样本。
        if (Ptr(address) != stringClass || !Read(address + 16, 4).AsSpan().SequenceEqual(length)) return null;
        return Encoding.Unicode.GetString(text);
    }
    List<ulong> Find(byte[][] needles, CancellationToken token, bool alignedPointers = false)
    {
        var hits = new HashSet<ulong>(); var clock = Stopwatch.StartNew();
        // 元数据可能有大量相同名字的副本；指针集合一次扫描，避免每个副本重扫整片内存。
        var pointerSet = alignedPointers && needles.Length > 8 ? needles.Select(n => BitConverter.ToUInt64(n)).ToHashSet() : null;
        int overlap = needles.Max(n => n.Length) - 1;
        const int chunk = 1024 * 1024;
        byte[] buffer = new byte[chunk + overlap];
        ulong address = 0;
        while (VirtualQueryEx(process, address, out var region, (nuint)Marshal.SizeOf<Region>()) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("定位超过 30 秒，请保持当前台词后重试。");
            ulong end = region.Base + region.Size;
            if (end <= address || end > 0x0000800000000000) break;
            address = end;
            if (region.State != 0x1000 || (region.Protect & 0x101) != 0 || (region.Protect & 0xee) == 0) continue;
            for (ulong position = region.Base; position < end; position += chunk)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("定位超时，请保持当前台词后重试。");
                int size = (int)Math.Min((ulong)buffer.Length, end - position);
                if (!ReadProcessMemory(process, position, buffer, (nuint)size, out var count) || count == 0) continue;
                if (pointerSet != null)
                {
                    int start = (int)((8 - position % 8) % 8);
                    var words = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(start, Math.Min(chunk, (int)count) - start));
                    for (int i = 0; i < words.Length; i++)
                        if (pointerSet.Contains(words[i]))
                        {
                            hits.Add(position + (ulong)(start + i * 8));
                            if (hits.Count > 20000) throw new InvalidOperationException("文字组件引用过多，暂时无法唯一定位。");
                        }
                    continue;
                }
                foreach (var needle in needles)
                {
                    var data = buffer.AsSpan(0, (int)count); int from = 0;
                    while (from <= data.Length - needle.Length)
                    {
                        int at = data[from..].IndexOf(needle);
                        if (at < 0) break;
                        int offset = from + at;
                        if (offset < chunk) hits.Add(position + (ulong)offset);
                        if (hits.Count > 20000) throw new InvalidOperationException("这句匹配过多，请换一条更完整的正文。");
                        from = offset + 1;
                    }
                }
            }
        }
        return hits.ToList();
    }
    public void Bind(string anchor, GameTextKind kind, CancellationToken token, bool allowWaiting = false)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            fields.Clear(); speakerFields.Clear(); fullScreenMarkers.Clear(); lastText = lastSpeaker = "";
            automaticScene = kind == GameTextKind.Scene3D && anchor.Length == 0;
            bindTrace.Clear(); bodyCandidates.Clear(); ConnectionDiagnostics = "";
            bool connected = false;
            try
            {
                token.ThrowIfCancellationRequested();
                BindCore(anchor, kind, token, allowWaiting); connected = true;
                bindTrace.Add("连接成功");
            }
            catch (Exception ex) { bindTrace.Add("连接结束：" + ex.Message); throw; }
            finally
            {
                RecordConnectionDiagnostics(kind);
                if (!connected) { fields.Clear(); speakerFields.Clear(); }
            }
        }
    }
    void BindCore(string anchor, GameTextKind kind, CancellationToken token, bool allowWaiting)
    {
            if (automaticScene)
            {
                var objects = FindUiTextObjects(token);
                foreach (ulong obj in objects)
                {
                    if (Component(obj, kind, speaker: true) is { } speaker) speakerFields.Add(speaker);
                    if (Component(obj, kind) is { } body && IsAutomaticSceneBody(body)) fields.Add(body);
                }
                fields = fields.DistinctBy(f => f.Object).ToList();
                bindTrace.Add($"自动识别 3D 对白框={fields.Count}");
                if (fields.Count == 0) throw new InvalidOperationException("等待 3D 对白框出现；暂未找到兼容的通讯或角色对白。");
                var sample = Sample();
                if (!sample.Active && !allowWaiting) throw new InvalidOperationException(sample.Reason);
                return;
            }
            if (anchor.Length == 0 && kind == GameTextKind.Ordinary)
            {
                var names = Find(new[] { Encoding.ASCII.GetBytes("XUiRichTextCustomRender\0") }, token);
                bindTrace.Add($"组件名称命中={names.Count}");
                if (names.Count == 0) throw new InvalidOperationException("尚未找到普通对白组件，请先进入普通剧情。");
                var classes = Find(names.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true).Where(p => p % 8 == 0 &&
                    ClassIs(p - 16, "XUiRichTextCustomRender", "XUiComponent")).Select(p => p - 16).Distinct().ToArray();
                if (classes.Length == 0) throw new InvalidOperationException("尚未找到普通对白类型，请先进入普通剧情。");
                bindTrace.Add($"正文类型={classes.Length}");
                foreach (ulong obj in Find(classes.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true).Where(p => p % 8 == 0))
                    if (Component(obj, kind) is { } component) fields.Add(component);
                fields = fields.DistinctBy(f => f.Object).ToList();
                if (fields.Count == 0) throw new InvalidOperationException("找到了对白类型，但没有识别到正文控件。请在设置中导出诊断，检查游戏版本适配。");
                DiscoverSpeakers(kind, token);
                DiscoverFullScreenMarkers(token);
                var sample = Sample();
                if (!sample.Active && !allowWaiting) throw new InvalidOperationException(sample.Reason + " 请保持当前画面后重试；仍失败可在设置中导出诊断。");
                return;
            }
            if (anchor.Length < 6 || anchor.Length > 3000) throw new ArgumentException("请用至少 6 个字的完整游戏正文连接。");
            var strings = new HashSet<ulong>();
            foreach (var hit in Find(new[] { Encoding.Unicode.GetBytes(anchor) }, token))
            {
                ulong address = hit - 20, klass = Ptr(address);
                if (!ClassIs(klass, "String", "System")) continue;
                stringClass = klass;
                if (StringAt(address) == anchor) strings.Add(address);
            }
            if (strings.Count == 0) throw new InvalidOperationException("没有读到这句完整正文。请让游戏停在该句，并核对标点。");
            bindTrace.Add($"当前句字符串={strings.Count}");
            var found = new List<GameTextField>();
            foreach (ulong field in Find(strings.Select(BitConverter.GetBytes).ToArray(), token, alignedPointers: true))
            {
                if (field % 8 != 0) continue;
                foreach (ulong offset in kind == GameTextKind.Ordinary ? new ulong[] { 0xf0 } : new ulong[] { 0xe8 })
                {
                    if (Component(field - offset, kind) is { } component && ReadField(component) is { Active: true, Text: var value } && value == anchor)
                        found.Add(component);
                }
            }
            fields = found.DistinctBy(f => f.Object).ToList(); lastText = anchor;
            if (fields.Count == 0) throw new InvalidOperationException("读到了文本资源，但没有找到已启用的对白正文组件，请显示该句后重试。");
            DiscoverSpeakers(kind, token);
            if (kind == GameTextKind.Ordinary) DiscoverFullScreenMarkers(token);
    }
    static string ShortText(string value)
    {
        string clean = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return clean.Length > 100 ? clean[..100] + "…" : clean;
    }
    void RecordConnectionDiagnostics(GameTextKind kind)
    {
        try
        {
            var samples = fields.Select(ReadField).ToArray();
            var active = samples.Where(s => s.Valid && s.Active).ToArray();
            var texts = active.Select(s => s.Text).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
            var report = new StringBuilder($"PID={ProcessId}；模式={kind}\n");
            foreach (string entry in bindTrace) report.AppendLine(entry);
            report.AppendLine($"正文控件={fields.Count}；有效={samples.Count(s => s.Valid)}；启用={active.Length}；空正文={active.Count(s => string.IsNullOrWhiteSpace(s.Text))}；不同非空正文={texts.Length}；角色名控件={speakerFields.Count}");
            foreach (var entry in bodyCandidates.Take(24)) report.AppendLine($"{entry.Key}={entry.Value}");
            // 仅记录候选正文的短片段，不导出内存、地址或其他文本资源。
            foreach (string text in texts.Take(5)) report.AppendLine("启用正文片段：" + ShortText(text));
            ConnectionDiagnostics = report.ToString();
            CoreDiagnostics.Write("game-text-scan", ConnectionDiagnostics.ReplaceLineEndings(" | "));
        }
        catch (Exception ex) { ConnectionDiagnostics = "连接诊断收集失败：" + ex.Message; }
    }
    // 只接受启用的正文组件。隐藏、过渡和歧义都不提供可播放文本，也不丢弃可恢复的连接。
    public GameTextSample Sample()
    {
        lock (sync)
        {
            if (disposed || !GetExitCodeProcess(process, out uint code) || code != 259)
                return new(false, "", false, "游戏进程已结束，请重新连接。");
            var samples = fields.Select(f => (Field: f, Value: automaticScene && !IsAutomaticSceneBody(f) ? (Valid: false, Active: false, Text: "") : ReadField(f))).ToArray();
            if (!samples.Any(s => s.Value.Valid)) return new(false, "", false, "对白组件已经失效，请重新连接当前句。");
            var active = samples.Where(s => s.Value.Valid && s.Value.Active).ToArray();
            if (active.Length == 0) return new(true, "", false, "正文控件尚未启用，等待显示。");
            active = active.Where(s => !IsFullScreen(s.Field) || IsCurrentFullScreenRow(s.Field)).ToArray();
            if (active.Length == 0) return new(true, "", false, "全屏旁白正在切换，等待当前行；长时间无文字时请重新连接。");
            // 空的备用对白框不构成另一句正文，也不能影响当前说话人的画布选择。
            // 保留这些字段的绑定；之后出现文字时仍参与冲突检测。
            active = active.Where(s => !string.IsNullOrWhiteSpace(s.Value.Text)).ToArray();
            var values = active.Select(s => s.Value.Text).Distinct().ToArray();
            if (values.Length == 0) return new(true, "", false, "正文控件已启用，但文字为空，等待下一句。");
            if (values.Length > 1) return new(true, "", false, "有多个已启用的不同正文，等待明确当前句。");
            string speaker = ReadSpeaker(active.Select(s => s.Field));
            return new(true, values[0], values[0] != lastText || speaker != lastSpeaker, Active: true, Speaker: speaker);
        }
    }
    public bool Accept(string text, string? speaker = null)
    {
        lock (sync)
        {
            if (Sample() is not { Valid: true, Active: true } current || current.Text != text || (speaker != null && current.Speaker != speaker)) return false;
            lastText = text; lastSpeaker = current.Speaker; return true;
        }
    }
    public void Dispose() { lock (sync) { if (!disposed) { disposed = true; process.Dispose(); } } }
}
