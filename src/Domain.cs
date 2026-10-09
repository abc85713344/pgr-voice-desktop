using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PgrVoice;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("文件内容为空");
    public static T ReadWithBackup<T>(string path, out bool recovered)
    {
        recovered = false;
        try { return Read<T>(path); }
        catch when (File.Exists(path + ".bak")) { var value = Read<T>(path + ".bak"); recovered = true; return value; }
    }
    public static void Save<T>(string path, T value, bool preservePrevious = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        if (preservePrevious && File.Exists(path))
        {
            bool valid = false;
            try { using var document = JsonDocument.Parse(File.ReadAllText(path)); valid = true; } catch (JsonException) { }
            if (valid) { File.Copy(path, path + ".bak.tmp", true); File.Move(path + ".bak.tmp", path + ".bak", true); }
        }
        File.Move(temp, path, true);
    }
}
public sealed class Pack
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public PackDelivery? Delivery { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsDraft => Delivery?.Status == "draft";
    [System.Text.Json.Serialization.JsonIgnore] public string DraftSummary => !IsDraft ? "" :
        $"抽检草稿 · 未逐句核验 · 可听 {Nodes.Count(n => n.Kind == "line" && n.Audio != null)} 句 · 待补 {Nodes.Count(n => n.Kind == "line" && n.Audio == null && n.AudioStatus != "not-spoken")} 句";
    public List<Chapter> Chapters { get; set; } = new();
    public List<Node> Nodes { get; set; } = new();
    public Dictionary<string,string> Migrations { get; set; } = new();
    // 由逐项核对的数据更新器写入；仅允许准确列出的旧导航图继续逐点校验。
    public List<string> CompatibleNavigationFingerprints { get; set; } = new();
    public List<NavigationResumeRepair> NavigationResumeRepairs { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public string Root { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, Node> ById { get; private set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public FixedVoicePackage? FixedVoices { get; private set; }
    string fixedVoiceNotice = "角色固定声线：等待配音包，未开启";
    [System.Text.Json.Serialization.JsonIgnore] public string FixedVoiceStatus => FixedVoices?.Status ?? fixedVoiceNotice;
    public static Pack Load(string file)
    {
        var pack = Json.Read<Pack>(file);
        pack.Root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        pack.Validate();
        pack.PrepareFixedVoices();
        return pack;
    }
    public void PrepareFixedVoices()
    {
        FixedVoices = null;
        fixedVoiceNotice = "角色固定声线：等待配音包，未开启";
        string manifest = Path.Combine(Root, FixedVoicePackage.RelativeManifest.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifest)) return;
        try { FixedVoices = FixedVoicePackage.Load(this, manifest); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { fixedVoiceNotice = "角色声线包尚不可用，继续使用原配音"; }
    }
    public void Validate()
    {
        if (SchemaVersion is not (1 or 2 or 3) || string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("不支持的配音包或缺少编号");
        if (CompatibleNavigationFingerprints == null || CompatibleNavigationFingerprints.Any(f => f == null || f.Length != 64 || !f.All(Uri.IsHexDigit))) throw new InvalidDataException("导航兼容指纹无效");
        if (Nodes.Select(n => n.Id).Distinct().Count() != Nodes.Count || Nodes.Any(n => string.IsNullOrEmpty(n.Id))) throw new InvalidDataException("台词编号重复或为空");
        ById = Nodes.ToDictionary(n => n.Id);
        var sections = Chapters.SelectMany(c => c.Sections).ToList();
        if(sections.Count==0 || !Nodes.Any(n=>n.Kind=="line"))throw new InvalidDataException("这个配音包没有读入台词。请在整理器选择具体章节目录，或用新版按章节编号导入。");
        if (sections.Select(s => s.Id).Distinct().Count() != sections.Count) throw new InvalidDataException("小节编号重复");
        foreach (var s in sections) if (!ById.ContainsKey(s.StartId)) throw new InvalidDataException("小节入口不存在：" + s.Title);
        foreach (var n in Nodes)
        {
            if (!new[] { "line", "choice", "merge", "gap", "end", "return" }.Contains(n.Kind)) throw new InvalidDataException("未知剧情节点类型");
            if (n.MenuNavigationEvidence == null || n.MenuNavigationEvidence.Count > 0 && (n.Kind != "choice" || n.MenuType is not ("interaction" or "topics") || n.MenuNavigationEvidence.Any(string.IsNullOrWhiteSpace))) throw new InvalidDataException("互动菜单导航证据无效");
            if (!sections.Any(s => s.Id == n.SectionId)) throw new InvalidDataException("节点所属小节不存在");
            if (n.NextId != null && !ById.ContainsKey(n.NextId)) throw new InvalidDataException("剧情指向不存在：" + n.Id);
            if (n.Audio != null) ResolveAudio(n);
            foreach (var o in n.Options)
            {
                if(SchemaVersion==3 && n.Archived)continue;
                if (SchemaVersion == 3)
                {
                    if (!ById.ContainsKey(o.TargetId)) throw new InvalidDataException("分支入口不存在");
                    if (o.ExitVerified && !o.BodyVerified) throw new InvalidDataException("出口核实不能代替正文核实");
                    if (o.BodyVerified && (o.BodyEvidence.Count == 0 || !o.SegmentIds.Contains(o.TargetId) || o.SegmentIds.Distinct().Count()!=o.SegmentIds.Count || o.SegmentIds.Any(id=>!ById.TryGetValue(id,out var s) || s.PathId!=o.PathId || s.SectionId!=n.SectionId))) throw new InvalidDataException("已核对段落范围或证据不完整："+o.Label);
                    if (o.BodyVerified && !o.ExitVerified && (o.BoundaryId==null || !ById.TryGetValue(o.BoundaryId,out var b) || b.Kind!="gap" || b.PathId!=o.PathId || b.SectionId!=n.SectionId)) throw new InvalidDataException("段落缺少待续接边界");
                    if (o.ExitVerified && (o.ExitEvidence.Count==0 || o.ReturnId==null || !ById.ContainsKey(o.ReturnId))) throw new InvalidDataException("出口缺少证据或目标");
                    continue;
                }
                if (SchemaVersion == 2 && o.Verified && (o.ReturnId == null || !ById.ContainsKey(o.ReturnId) || o.Evidence.Count == 0)) throw new InvalidDataException("路线缺少返回节点或核对证据");
                if (!ById.ContainsKey(o.TargetId)) throw new InvalidDataException("分支入口不存在");
                if (o.Verified && (o.MergeId == null || !ById.TryGetValue(o.MergeId, out var m) || m.Kind != "merge")) throw new InvalidDataException("已核实分支缺少明确汇合点");
            }
            if(n.ResumeMenuIds.Any(id=>!ById.TryGetValue(id,out var m)||m.Kind!="choice"||m.SectionId!=n.SectionId))throw new InvalidDataException("续接菜单不存在或跨小节");
        }
        if(SchemaVersion==3 && Nodes.Where(n=>!n.Archived).SelectMany(n=>n.Options).GroupBy(o=>o.PathId).Any(g=>string.IsNullOrEmpty(g.Key)||g.Count()>1))throw new InvalidDataException("路线编号重复或为空");
        NavigationResumeRepair.Validate(this);
    }
    public string? ResolveAudio(Node n)
    {
        if (FixedVoices?.Resolve(n) is string fixedAudio) return fixedAudio;
        if (string.IsNullOrWhiteSpace(n.Audio)) return null;
        // 配音包来自 Windows，统一两种分隔符，不能让安卓把反斜线当成文件名。
        var relative = n.Audio.Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Contains(':') || Path.IsPathRooted(relative)) throw new InvalidDataException("音频路径必须相对于配音包");
        var root = Path.GetFullPath(Root);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException("音频路径越过配音包目录");
        return path;
    }
    public string AudioNotice(Node node)
    {
        if (node.Kind != "line") return "";
        var path = ResolveAudio(node);
        if (path != null) return File.Exists(path) ? "" : "音频文件缺失";
        return node.AudioStatus switch
        {
            "draft-missing" => "缺句 · " + (node.AudioReason ?? "正在补配"),
            "not-found" => "未找到对应配音",
            "not-spoken" => "无可朗读正文",
            "generation-failed" => "补配未通过核验",
            _ => "配音待核对"
        };
    }
    public bool TryRefreshDraftAudio(Pack updated, out string reason)
    {
        reason = "";
        if (!IsDraft || !updated.IsDraft || Id != updated.Id ||
            !String.Equals(Root, updated.Root, StringComparison.OrdinalIgnoreCase))
        { reason = "更新不属于当前草稿。"; return false; }
        if (PlaybackEngine.NavigationFingerprint(this) != PlaybackEngine.NavigationFingerprint(updated) ||
            Nodes.Any(n => !updated.ById.TryGetValue(n.Id, out var other) || n.Text != other.Text || n.Speaker != other.Speaker))
        { reason = "剧情正文或分支有更新，请重新打开草稿确认当前位置。"; return false; }
        if (updated.Nodes.Any(n => n.Audio != null && !File.Exists(updated.ResolveAudio(n))))
        { reason = "新的草稿音频尚未完整发布，继续保留当前版本。"; return false; }
        // 只替换音频字段；沿用当前对象和导航状态，缺句补齐不会推进、重播或暂停。
        foreach (var node in Nodes)
        {
            var other = updated.ById[node.Id];
            node.Audio = other.Audio; node.AudioStatus = other.AudioStatus; node.AudioReason = other.AudioReason;
        }
        Delivery = updated.Delivery;
        return true;
    }
}
public sealed class PackDelivery
{
    public string Status { get; set; } = "";
    public string Label { get; set; } = "";
    public string Revision { get; set; } = "";
    public string SortOrder { get; set; } = "";
    public int AudioCount { get; set; }
    public int MissingCount { get; set; }
    public int SpokenCount { get; set; }
    public int SampledCount { get; set; }
    public int SamplePassedCount { get; set; }
}
public sealed class Chapter
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<Section> Sections { get; set; } = new();
    public override string ToString() => Title;
}
public sealed class Section
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string StartId { get; set; } = "";
    public override string ToString() => Title;
}
public sealed class Node
{
    public List<string> ResumeMenuIds { get; set; } = new();
    public bool Archived { get; set; }
    public string MenuType { get; set; } = "exclusive";
    public List<string> MenuNavigationEvidence { get; set; } = new();
    public string? CompleteRoute { get; set; }
    public List<string> SetFacts { get; set; } = new();
    public string Id { get; set; } = "";
    public string SectionId { get; set; } = "";
    public string Kind { get; set; } = "line";
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Audio { get; set; }
    public string? AudioStatus { get; set; }
    public string? AudioReason { get; set; }
    public string? RawText { get; set; }
    public string? RenderSource { get; set; }
    public string PathId { get; set; } = "";
    public string? NextId { get; set; }
    public List<ChoiceOption> Options { get; set; } = new();
    public string? Source { get; set; }
}
public sealed class ChoiceOption
{
    public bool BodyVerified { get; set; }
    public bool ExitVerified { get; set; }
    public List<string> SegmentIds { get; set; } = new();
    public string? BoundaryId { get; set; }
    public List<string> BodyEvidence { get; set; } = new();
    public List<string> ExitEvidence { get; set; } = new();
    public string? ReturnId { get; set; }
    public string Preview { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ConditionNote { get; set; } = "";
    public List<string> Requires { get; set; } = new();
    public List<string> Excludes { get; set; } = new();
    public List<string> Evidence { get; set; } = new();
    public List<string> LineIds { get; set; } = new();
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string PathId { get; set; } = "";
    public string? MergeId { get; set; }
    public bool Verified { get; set; }
    public override string ToString() => Label + (BodyVerified ? ExitVerified?"":"  · 段尾需选择" : Verified ? "" : "  · 正文待核对");
}
public enum RunMode { Ready, Following, Paused, Choice, Merge, Gap, Original, End }
public sealed class KeyPressGate
{
    // 同一次物理按键会同时被低层钩子与 Raw Input 上报。只按“按下/释放”去重不够：
    // 播放音频会阻塞界面线程，释放事件可能先于另一来源的按下事件被处理，
    // 于是同一次按键被算成两次按下，键盘推进因此跳句。
    // 这里再按来源记录触发时刻，让两个来源在短时间内互相抑制。
    const long CrossSourceWindowMs = 800;
    readonly HashSet<int> down = new();
    readonly Dictionary<int, long> hookFired = new();
    readonly Dictionary<int, long> rawFired = new();
    public bool Update(int key, bool pressed, bool injected = false, bool fromRaw = false)
    {
        if (injected) return false;
        if (!pressed) { down.Remove(key); return false; }
        if (!down.Add(key)) return false;
        long now = Environment.TickCount64;
        var other = fromRaw ? hookFired : rawFired;
        if (other.TryGetValue(key, out long last) && now - last < CrossSourceWindowMs)
        {
            CoreDiagnostics.Write("keyboard", $"忽略同一次按键的重复上报：键={key}，另一来源 {now - last} 毫秒前已处理。");
            return false;
        }
        if (fromRaw) rawFired[key] = now; else hookFired[key] = now;
        return true;
    }
}
public sealed class MenuKeyGate
{
    readonly HashSet<int> down=new(), captured=new();
    public (bool Swallow,bool Fire) Update(int key,bool pressed,bool capture,bool injected=false)
    {
        if(injected)return(false,false);
        if(!pressed){down.Remove(key);return(captured.Remove(key),false);}
        bool first=down.Add(key);
        if(first && capture)captured.Add(key);
        return(captured.Contains(key),first && captured.Contains(key));
    }
}
public sealed record Visit(string NodeId, Dictionary<string, string> Choices, HashSet<string>? Facts = null, HashSet<string>? Heard = null, string? PendingMenu = null, bool SingleLine = false, string? ReviewRoute = null, int ChoiceCursor = 0, HashSet<string>? ObservedMenus = null);
public sealed partial class PlaybackEngine
{
    public Pack Pack { get; }
    public string? CurrentId { get; private set; }
    public Node? Current => CurrentId != null && Pack.ById.TryGetValue(CurrentId, out var n) ? n : null;
    public RunMode Mode { get; private set; } = RunMode.Ready;
    public Dictionary<string, string> Choices { get; private set; } = new();
    public HashSet<string> Facts { get; private set; } = new();
    public HashSet<string> Heard { get; private set; } = new();
    public string? PendingMenu { get; private set; }
    public string Notice { get; private set; } = "";
    public string? ReviewRoute { get; private set; }
    public bool MenuWaiting => Mode is RunMode.Choice or RunMode.Gap;
    public List<Node> ResumeMenus => Current?.ResumeMenuIds.Select(id=>Pack.ById[id]).Where(Allowed).ToList() ?? new();
    bool singleLine;
    // 软件的经历可能落后于游戏。选项始终可见，玩家确认当前画面后才进入。
    public List<ChoiceOption> AvailableOptions => Current?.Options.ToList() ?? new();
    public bool ConditionsKnown(ChoiceOption option) => option.Requires.All(Facts.Contains) && !option.Excludes.Any(Facts.Contains);
    public bool CanLocate(Node n) => !n.Archived && (Allowed(n) || (ReviewRoute != null && Pack.Nodes.SelectMany(x=>x.Options).Any(o=>o.Id==ReviewRoute && o.LineIds.Contains(n.Id) && (Pack.SchemaVersion<3 || n.PathId==o.PathId))));
    public List<Visit> History { get; } = new();
    int historyPosition = -1;
    public event Action<Node?>? PlayRequested;
    public event Action? StopRequested;
    public event Action? Changed;
    public PlaybackEngine(Pack pack) { Pack = pack; }
    public bool Allowed(Node n)
    {
        if(n.Archived) return false;
        if(n.Kind=="choice" && ObservedMenus.Contains(n.Id))return true;
        bool PathAllowed(string path, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (!seen.Add(path)) return false;
            var owner = Pack.Nodes.FirstOrDefault(c => !c.Archived && c.Kind == "choice" && c.Options.Any(o => o.PathId == path && (Pack.SchemaVersion==3?o.BodyVerified:o.Verified)));
            if(Pack.SchemaVersion==3 && owner!=null && owner.PathId.Length>0 && !ObservedMenus.Contains(owner.Id))
            {
                var parent=Pack.Nodes.Where(x=>!x.Archived).SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==owner.PathId);
                if(parent==null || !parent.SegmentIds.Contains(owner.Id))return false;
            }
            return owner != null && Choices.TryGetValue(owner.Id, out var selected) && selected == path && (ObservedMenus.Contains(owner.Id) || PathAllowed(owner.PathId, seen));
        }
        if(!PathAllowed(n.PathId, new()))return false;
        if(Pack.SchemaVersion==3 && n.PathId.Length>0)
        {
            var option=Pack.Nodes.SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==n.PathId);
            return option!=null && (option.SegmentIds.Contains(n.Id)||option.BoundaryId==n.Id);
        }
        return true;
    }
    public void Restore(string? id, Dictionary<string, string>? choices = null, HashSet<string>? facts = null, HashSet<string>? heard = null, int savedSchema = 1)
    {
        ResetNavigation();
        CurrentId=null;History.Clear();historyPosition=-1;PendingMenu=null;ReviewRoute=null;singleLine=false;
        string? SafeMenu(string nodeId)
        {
            var current=Pack.ById.GetValueOrDefault(nodeId);var seen=new HashSet<string>();
            while(current!=null && current.PathId.Length>0 && seen.Add(current.Id))
                current=Pack.Nodes.FirstOrDefault(n=>!n.Archived && n.Options.Any(o=>o.PathId==current.PathId));
            return current?.Id;
        }
        Choices = choices != null ? new(choices) : new();
        Facts = facts != null ? new(facts) : new(); Heard = heard != null ? new(heard) : new();
        if (id != null && Pack.Migrations.TryGetValue(id,out var migrated)) { id=SafeMenu(migrated); Choices.Clear(); Facts.Clear(); }
        if (savedSchema < Pack.SchemaVersion && Pack.SchemaVersion >= 2 && id != null && Pack.ById.TryGetValue(id,out var old) && old.PathId.Length>0)
        {
            id=SafeMenu(old.Id);
            Choices.Clear(); Facts.Clear();
        }
        if (id != null && Pack.ById.TryGetValue(id, out var n) && Allowed(n)) CurrentId = id;
        Mode = RunMode.Ready;
        Changed?.Invoke();
    }
    public int HistoryPosition => historyPosition;
    public void RestoreHistory(List<Visit>? visits,int position)
    {
        if(visits==null || visits.Count==0 || position<0 || position>=visits.Count)return;
        // 旧存档没有选择事件，不能由台词记录推测出曾经作过的选择。
        var safe = visits.Select(v => CloneVisit(v) with { ChoiceCursor = 0 }).ToList();
        if(safe.Any(v=>!ValidateVisit(v, 0)))return;
        History.Clear();History.AddRange(safe);historyPosition=position;
    }
    public void Commit(string id)
    {
        if (!Pack.ById.TryGetValue(id, out var node) || !Allowed(node)) throw new InvalidOperationException("请先确认对应分支");
        BeginCorrection();
        StopRequested?.Invoke();
        singleLine=false;ReviewRoute=null;
        AdvanceTo(node);
    }
    public void CommitSingle(string id)
    {
        if (!Pack.ById.TryGetValue(id,out var n) || n.Kind!="line" || !CanLocate(n)) throw new InvalidOperationException("请选择当前待核对路线中的一句");
        BeginCorrection();
        StopRequested?.Invoke(); singleLine=true;CurrentId=id;Mode=RunMode.Following;
        Record(n);PlayRequested?.Invoke(n);Changed?.Invoke();
    }
    void Record(Node node)
    {
        DiscardFuture();
        History.Add(new(node.Id,new(Choices),new(Facts),new(Heard),PendingMenu,singleLine,ReviewRoute,choiceCursor,new(ObservedMenus)));historyPosition=History.Count-1;
    }
    void AdvanceTo(Node node)
    {
        var traversed=new HashSet<string>();
        while(node.Kind=="return")
        {
            if(!Allowed(node))
            {
                var limited=Pack.Nodes.Where(x=>!x.Archived).SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==node.PathId && o.BodyVerified && !o.ExitVerified && o.BoundaryId!=null);
                if(Pack.SchemaVersion==3 && limited!=null && Allowed(Pack.ById[limited.BoundaryId!])) {node=Pack.ById[limited.BoundaryId!];break;}
                Mode=RunMode.Gap;Notice="此返回连接尚未核实，请定位当前台词。";Changed?.Invoke();return;
            }
            if(!traversed.Add(node.Id) || node.NextId==null) {Mode=RunMode.Gap;Changed?.Invoke();return;}
            if(node.CompleteRoute!=null) Heard.Add(node.CompleteRoute);
            var route=Pack.Nodes.SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==node.PathId);
            if(Pack.SchemaVersion<3 || route?.ExitVerified==true)Facts.UnionWith(node.SetFacts);
            node=Pack.ById[node.NextId];
        }
        if (!Allowed(node))
        {
            var blocked=Pack.Nodes.SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==node.PathId && o.BodyVerified && !o.ExitVerified && o.BoundaryId!=null);
            if(Pack.SchemaVersion==3 && blocked!=null && Allowed(Pack.ById[blocked.BoundaryId!]))node=Pack.ById[blocked.BoundaryId!];
            else { Mode=RunMode.Gap;Notice="此连接尚未核实，请定位当前台词。";Changed?.Invoke();return; }
        }
        if(node.Kind=="choice" && Current?.Kind=="line" && Current.PathId==node.PathId && node.PathId.Length>0)Heard.Add(node.PathId);
        if(node.Kind=="gap" && Current?.Kind=="line" && Current.PathId==node.PathId && node.PathId.Length>0)Heard.Add(node.PathId);
        CurrentId = node.Id;
        Mode = node.Kind switch { "choice" => RunMode.Choice, "merge" => RunMode.Merge, "gap" => RunMode.Gap, "end" => RunMode.End, _ => RunMode.Following };
        if (node.Kind == "line")
        {
            Record(node);
            PlayRequested?.Invoke(node);
        }
        if(Mode==RunMode.Choice) {PendingMenu=node.Id;ReviewRoute=null;Notice="请先在游戏中选择，再确认相同路线。";}
        if(Mode==RunMode.Gap) {ReviewRoute=Pack.Nodes.SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==node.PathId)?.Id;Notice="已核对段落已结束，请按游戏当前画面选择续接位置。";}
        Changed?.Invoke();
    }
    public void Next(bool manual = false)
    {
        ClearCorrection();
        if (Current == null || Mode is RunMode.Original or RunMode.Choice or RunMode.Gap or RunMode.End or RunMode.Ready) return;
        if (!manual && Mode == RunMode.Paused) return;
        StopRequested?.Invoke();
        if(singleLine) {singleLine=false; if(PendingMenu!=null) AdvanceTo(Pack.ById[PendingMenu]);else {Mode=RunMode.Gap;Changed?.Invoke();}return;}
        var option=Pack.Nodes.SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==Current.PathId);
        if(Pack.SchemaVersion==3 && option?.BodyVerified==true && !option.ExitVerified && (Current.NextId==null || (!option.SegmentIds.Contains(Current.NextId) && Current.NextId!=option.BoundaryId)))
            AdvanceTo(Pack.ById[option.BoundaryId!]);
        else if (Current.NextId != null) AdvanceTo(Pack.ById[Current.NextId]);
        else { Mode = RunMode.End; Changed?.Invoke(); }
    }
    public void Previous()
    {
        ClearCorrection();
        if (Mode == RunMode.Original || History.Count == 0 || historyPosition < 0) return;
        StopRequested?.Invoke();
        if (CurrentId == History[historyPosition].NodeId && historyPosition > 0) historyPosition--;
        var visit = History[historyPosition];
        CurrentId = visit.NodeId; Choices = new(visit.Choices); Mode = RunMode.Following;
        Facts=new(visit.Facts??new());Heard=new(visit.Heard??new());PendingMenu=visit.PendingMenu;singleLine=visit.SingleLine;ReviewRoute=visit.ReviewRoute;
        ObservedMenus=new(visit.ObservedMenus??new());
        choiceCursor=visit.ChoiceCursor;CurrentReselectOptionId=null;
        if(!Allowed(Current!) && !(singleLine && CanLocate(Current!))) {Mode=RunMode.Ready;Changed?.Invoke();return;}
        PlayRequested?.Invoke(Current); Changed?.Invoke();
    }
    public void SelectBranch(int index)
    {
        if (Mode != RunMode.Choice || Current == null || index < 0 || index >= AvailableOptions.Count) return;
        var option = AvailableOptions[index];
        StopRequested?.Invoke();
        ObservedMenus.Add(Current.Id);
        RecordChoice(option);
        if (!(Pack.SchemaVersion==3?option.BodyVerified:option.Verified)) { ReviewRoute=option.Id;Notice=option.Reason.Length>0?option.Reason:"正文范围或返回位置尚未核实，请定位当前台词或手动选句。";if(Pack.SchemaVersion==1)Mode=RunMode.Gap; Changed?.Invoke(); return; }
        Choices[Current.Id] = option.PathId;
        Notice="已进入："+option.Label;singleLine=false;ReviewRoute=null;
        AdvanceTo(Pack.ById[option.TargetId]);
    }
    public void SelectContinuation(int index)
    {
        if(Mode!=RunMode.Gap || index<0 || index>=ResumeMenus.Count)return;
        StopRequested?.Invoke();var target=ResumeMenus[index];ReviewRoute=null;singleLine=false;AdvanceTo(target);
    }
    public void Replay()
    {
        if (Mode == RunMode.Original || Current?.Kind != "line") return;
        StopRequested?.Invoke(); PlayRequested?.Invoke(Current);
    }
    public void TogglePause()
    {
        if (Mode == RunMode.Following) { Mode = RunMode.Paused; StopRequested?.Invoke(); }
        else if (Mode == RunMode.Paused) Mode = RunMode.Following;
        Changed?.Invoke();
    }
    public void EnterOriginal() { ClearCorrection();StopRequested?.Invoke(); ReviewRoute=null;singleLine=false;Mode = RunMode.Original; Changed?.Invoke(); }
    public void OpenMenu() {if(Mode!=RunMode.Original && Current?.Kind is "choice" or "gap"){Mode=Current.Kind=="choice"?RunMode.Choice:RunMode.Gap;if(Mode==RunMode.Choice)PendingMenu=CurrentId;else {ReviewRoute=Pack.Nodes.Where(x=>!x.Archived).SelectMany(x=>x.Options).FirstOrDefault(o=>o.PathId==Current.PathId)?.Id;Notice="已核对段落已结束，请选择续接位置。";}Changed?.Invoke();}}
    public void SuspendSound() => StopRequested?.Invoke();
}
