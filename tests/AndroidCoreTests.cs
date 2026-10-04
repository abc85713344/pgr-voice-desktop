using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PgrVoice;
using PgrVoice.Following;
using PgrVoice.Packages;

public static class AndroidCoreTests
{
    /// <summary>用真实章节正文回放跟随协议；只报告核心决策，不代替截图 OCR 或手机性能。</summary>
    public static void RunPack(Pack pack)
    {
        int menus = 0, routes = 0, boundaries = 0, automaticPairs = 0;
        foreach (var node in pack.Nodes)
            Require(node.Audio == null || File.Exists(pack.ResolveAudio(node)), "实际章节缺少音频：" + node.Id);
        foreach (var menu in pack.Nodes.Where(n => !n.Archived && n.Kind == "choice"))
        {
            menus++;
            foreach (var option in menu.Options)
            {
                var engine = new PlaybackEngine(pack);
                Require(engine.OpenGameMenu(menu.Id, menu.SectionId), "实际菜单不能明确定位：" + menu.Id);
                int plays = 0; engine.PlayRequested += _ => plays++;
                engine.SelectBranch(engine.AvailableOptions.FindIndex(o => o.Id == option.Id));
                if (!(pack.SchemaVersion == 3 ? option.BodyVerified : option.Verified))
                { Require(plays == 0 && engine.Mode is RunMode.Choice or RunMode.Gap, "未核实路线自行播音"); continue; }
                routes++; int steps = 0;
                while (engine.Mode == RunMode.Following && steps++ < 2000)
                {
                    var next = engine.GetAutomaticNextLine();
                    if (automaticPairs < 40 && next != null && Matcher.Normalize(next.Text).Length >= 6 &&
                        pack.Nodes.Count(n => n.Kind == "line" && !n.Archived && n.SectionId == next.SectionId && Matcher.Normalize(n.Text) == Matcher.Normalize(next.Text)) == 1)
                    {
                        var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic);
                        Require(safety.ConfirmPosition(engine, "previous"), "真实路线不能明确启用跟随");
                        var observation = new FollowObservation(safety.Epoch, 1, "changed", true,
                            [new() { Text = next.Text, Score = .99 }, new() { Text = next.Speaker, Score = .99 }], next.SectionId);
                        Require(safety.Evaluate(engine, observation).Kind == FollowDecisionKind.Waiting, "真实台词一次识别就推进");
                        observation = observation with { Sequence = 2 };
                        var decision = safety.Evaluate(engine, observation); int before = plays;
                        Require(decision.Kind == FollowDecisionKind.Advance && safety.TryApply(engine, observation, decision) &&
                            engine.CurrentId == next.Id && plays == before + 1 && !safety.TryApply(engine, observation, decision), "真实台词跟随重复播放或无法推进：" + next.Id);
                        automaticPairs++;
                    }
                    else engine.Next();
                }
                Require(steps < 2000 && engine.Mode is RunMode.Choice or RunMode.Merge or RunMode.Gap or RunMode.End, "真实路线没有停在安全边界：" + option.Id);
                if (engine.Mode == RunMode.Gap)
                { boundaries++; string? before = engine.CurrentId; int sound = plays; engine.Next(true); Require(engine.CurrentId == before && plays == sound, "真实未知边界可被越过"); }
                var snapshot = engine.ExportNavigation(); var restored = new PlaybackEngine(pack); int restorePlays = 0; restored.PlayRequested += _ => restorePlays++;
                Require(restored.ImportNavigation(snapshot) && restored.CurrentId == engine.CurrentId && restorePlays == 0, "实际分支存档不能静音恢复：" + restored.NavigationError);
                string? position = engine.CurrentId; int originalPlays = plays; engine.EnterOriginal(); engine.Next(true); engine.Previous(); engine.Replay();
                Require(engine.CurrentId == position && plays == originalPlays && engine.Mode == RunMode.Original, "实际路线原声冻结失败");
            }
        }
        Require(automaticPairs > 0, "实际章节未覆盖任何自动跟随句对");
        Console.WriteLine($"ANDROID REAL PACK {pack.Id}: {pack.Nodes.Count} nodes, {menus} menus, {routes} verified routes, {boundaries} held boundaries, {automaticPairs} automatic pairs; audio paths, silent restore and original freeze passed");
    }

    public static void Run()
    {
        int count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS Android core " + name); }
        Test("schema 1/2/3 中文 ZIP、流式输入与音频反斜线兼容", () =>
        {
            WithRepository((repository, _) =>
            {
                for (int schema = 1; schema <= 3; schema++)
                {
                    var pack = Fixture(); pack.Id = "测试章节" + schema; pack.SchemaVersion = schema;
                    pack.Nodes[0].Audio = "audio\\中文配音.wav";
                    using var zip = Archive(pack, ("audio/中文配音.wav", [1, 2, 3]));
                    using var nonSeekable = new NonSeekableStream(zip);
                    var installed = repository.ImportAsync(nonSeekable).GetAwaiter().GetResult();
                    var loaded = repository.Load(pack.Id);
                    Require(installed.SchemaVersion == schema && File.Exists(loaded.ResolveAudio(loaded.Nodes[0])), "中文或反斜线音频没有正常导入");
                }
                Require(repository.List().Count == 3, "章节列表不完整");
            });
        });
        Test("音频更新保留旧包、历史、书签和进度且恢复静音", () =>
        {
            WithRepository((repository, folder) =>
            {
                var pack = Fixture(); pack.Nodes[0].Audio = "audio/旧配音.wav";
                using var first = Archive(pack, ("audio/旧配音.wav", [1]));
                var initial = repository.ImportAsync(first).GetAwaiter().GetResult();
                var engine = new PlaybackEngine(repository.Load(pack.Id)); engine.Commit("first"); engine.Next(true);
                var store = new ProgressStore(Path.Combine(folder, "progress"));
                store.Save(engine.Pack, engine.ExportNavigation()); var bookmark = engine.CreateBookmark("原来的位置");
                store.SaveBookmark(pack.Id, bookmark);
                pack.Nodes[0].Audio = "audio/新配音.wav";
                using var second = Archive(pack, ("audio/新配音.wav", [2]));
                var updated = repository.ImportAsync(second).GetAwaiter().GetResult();
                var restored = new PlaybackEngine(repository.Load(pack.Id)); int plays = 0; restored.PlayRequested += _ => plays++;
                Require(updated.IsUpdate && !updated.NavigationChanged && initial.PackFile != updated.PackFile && File.Exists(initial.PackFile), "旧 revision 被删除或音频更新被误判成剧情变化");
                Require(store.TryRestore(restored) && restored.CurrentId == "second" && restored.History.Count == 2 && plays == 0 && restored.Mode == RunMode.Ready, "音频更新丢失进度或自行发声");
                Require(store.Bookmarks(pack.Id).Single().Id == bookmark.Id, "更新丢失书签");
            });
        });
        Test("剧情变化沿用导航指纹校验，不偷偷放宽旧存档", () =>
        {
            WithRepository((repository, folder) =>
            {
                var pack = Fixture(); using var zip = Archive(pack); repository.ImportAsync(zip).GetAwaiter().GetResult();
                var engine = new PlaybackEngine(repository.Load(pack.Id)); engine.Commit("first");
                var store = new ProgressStore(Path.Combine(folder, "progress")); store.Save(engine.Pack, engine.ExportNavigation());
                pack.Nodes[0].NextId = "third"; using var update = Archive(pack);
                var result = repository.ImportAsync(update).GetAwaiter().GetResult();
                var changed = new PlaybackEngine(repository.Load(pack.Id));
                Require(result.NavigationChanged && !store.TryRestore(changed), "新剧情错误接受旧导航");
            });
        });
        Test("导入取消、缺音、损坏 ZIP 保留已安装版本", () =>
        {
            WithRepository((repository, _) =>
            {
                var pack = Fixture(); using var zip = Archive(pack);
                var original = repository.ImportAsync(zip).GetAwaiter().GetResult();
                using var cancel = new CancellationTokenSource(); using var update = Archive(pack);
                var progress = new InlineProgress(p => { if (p.Stage == "解压章节") cancel.Cancel(); });
                Reject(() => repository.ImportAsync(update, cancel.Token, progress).GetAwaiter().GetResult());
                pack.Nodes[0].Audio = "不存在.wav"; using var missing = Archive(pack);
                Reject(() => repository.ImportAsync(missing).GetAwaiter().GetResult());
                using var corrupt = new MemoryStream([1, 2, 3]); Reject(() => repository.ImportAsync(corrupt).GetAwaiter().GetResult());
                Require(repository.Find(pack.Id)!.Revision == original.Revision && File.Exists(original.PackFile), "失败导入破坏旧包");
            });
        });
        Test("拒绝 ZIP 越界、大小写冲突、符号链接和重复清单", () =>
        {
            WithRepository((repository, folder) =>
            {
                foreach (var path in new[] { "../escape.wav", "a/../../escape.wav", "..\\escape.wav", "F:/escape.wav", "/escape.wav", "a//escape.wav" })
                { using var zip = Archive(Fixture(), (path, [1])); Reject(() => repository.ImportAsync(zip).GetAwaiter().GetResult()); }
                using var caseConflict = Archive(Fixture(), ("音频/A.wav", [1]), ("音频/a.wav", [2]));
                Reject(() => repository.ImportAsync(caseConflict).GetAwaiter().GetResult());
                using var multiple = Archive(Fixture(), ("别的包/pack.json", Encoding.UTF8.GetBytes("{}")));
                Reject(() => repository.ImportAsync(multiple).GetAwaiter().GetResult());
                using var symlink = Archive(Fixture(), ("link", [1]));
                using (var edit = new ZipArchive(symlink, ZipArchiveMode.Update, true)) edit.GetEntry("章节目录/link")!.ExternalAttributes = unchecked((int)0xA1FF0000);
                symlink.Position = 0; Reject(() => repository.ImportAsync(symlink).GetAwaiter().GetResult());
                Require(repository.List().Count == 0 && !File.Exists(Path.Combine(folder, "escape.wav")), "恶意 ZIP 写入包目录外或被发布");
            });
        });
        Test("索引损坏可恢复备份，后续更新保持有效备份", () =>
        {
            WithRepository((repository, folder) =>
            {
                var pack = Fixture(); using var first = Archive(pack); var initial = repository.ImportAsync(first).GetAwaiter().GetResult();
                using var second = Archive(pack); repository.ImportAsync(second).GetAwaiter().GetResult();
                var pointer = Directory.EnumerateFiles(Path.Combine(folder, "packages"), "current.json", SearchOption.AllDirectories).Single();
                File.WriteAllText(pointer, "{broken");
                Require(repository.Find(pack.Id)!.Revision == initial.Revision, "旧索引备份无法恢复");
                using var third = Archive(pack); repository.ImportAsync(third).GetAwaiter().GetResult();
                File.WriteAllText(pointer, "{broken again");
                Require(repository.Find(pack.Id)!.Revision == initial.Revision, "损坏 current 覆盖了有效备份");
            });
        });
        Test("ZIP 大小限制在写入前后生效", () =>
        {
            WithRepository((_, folder) =>
            {
                var limited = new PackageRepository(Path.Combine(folder, "limited"), new() { MaximumEntryBytes = 16 });
                using var zip = Archive(Fixture()); Reject(() => limited.ImportAsync(zip).GetAwaiter().GetResult());
                Require(limited.List().Count == 0, "超限包被发布");
            });
        });

        Test("章节文件浏览限制边界、保留中文子目录并拒绝旧版本路径", () =>
        {
            WithRepository((repository, _) =>
            {
                var pack = Fixture(); using var zip = Archive(pack, ("音频/对白.wav", [1, 2, 3]));
                var installed = repository.ImportAsync(zip).GetAwaiter().GetResult();
                var top = repository.Browse(pack.Id);
                Require(top.Entries.Any(e => e.Name == "音频" && e.IsDirectory) && top.Entries.Any(e => e.Name == "pack.json"), "未列出章节文件");
                var sub = repository.Browse(pack.Id, "音频", installed.Revision);
                Require(sub.Entries.Single().Bytes == 3 && sub.Entries.Single().RelativePath == "音频/对白.wav", "中文子目录或文件大小错误");
                foreach (string bad in new[] { "..", "../..", "/tmp", "C:/Windows", "音频/../../.." })
                    Reject(() => repository.Browse(pack.Id, bad));
                using var update = Archive(pack); repository.ImportAsync(update).GetAwaiter().GetResult();
                Reject(() => repository.Browse(pack.Id, "音频", installed.Revision));
            });
        });
        Test("删除章节清理全部版本且保留别章、原ZIP、存档与书签，可重新导入续接", () =>
        {
            WithRepository((repository, folder) =>
            {
                var pack = Fixture(); using var zip = Archive(pack, ("音频/对白.wav", [1, 2, 3]));
                string original = Path.Combine(folder, "原压缩包.zip"); File.WriteAllBytes(original, zip.ToArray());
                var first = repository.ImportAsync(zip).GetAwaiter().GetResult();
                var engine = new PlaybackEngine(repository.Load(pack.Id)); engine.Commit("first"); engine.Next(true);
                var store = new ProgressStore(Path.Combine(folder, "progress")); store.Save(engine.Pack, engine.ExportNavigation());
                var bookmark = engine.CreateBookmark("保留书签"); store.SaveBookmark(pack.Id, bookmark);
                using var update = Archive(pack); var second = repository.ImportAsync(update).GetAwaiter().GetResult();
                var other = Fixture(); other.Id = "另一个章节"; using var otherZip = Archive(other);
                var otherInstalled = repository.ImportAsync(otherZip).GetAwaiter().GetResult();
                Reject(() => repository.RemoveAsync(pack.Id, first.Revision).GetAwaiter().GetResult());
                Require(repository.Find(pack.Id)!.Revision == second.Revision, "旧删除确认删掉更新后的章");
                var result = repository.RemoveAsync(pack.Id, second.Revision).GetAwaiter().GetResult();
                Require(!result.CleanupPending && repository.Find(pack.Id) == null && !File.Exists(first.PackFile) && !File.Exists(second.PackFile), "删除遗漏旧音频或备份索引");
                repository.CleanAbandonedImports();
                Require(repository.List().Count == 1 && File.Exists(otherInstalled.PackFile) && File.Exists(original), "误删别章或原ZIP");
                Reject(() => repository.RemoveAsync(pack.Id, second.Revision).GetAwaiter().GetResult());
                using var reimport = File.OpenRead(original); repository.ImportAsync(reimport).GetAwaiter().GetResult();
                var restored = new PlaybackEngine(repository.Load(pack.Id));
                Require(store.TryRestore(restored) && restored.CurrentId == "second" && store.Bookmarks(pack.Id).Single().Id == bookmark.Id, "重新导入丢失位置或书签");
            });
        });
        Test("中断清理不会复活已移除的章节", () =>
        {
            WithRepository((repository, folder) =>
            {
                var pack = Fixture(); using var zip = Archive(pack); var installed = repository.ImportAsync(zip).GetAwaiter().GetResult();
                string pointer = Directory.EnumerateFiles(Path.Combine(folder, "packages"), "current.json", SearchOption.AllDirectories).Single();
                string trashed = Path.Combine(folder, "packages", ".deleted", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(trashed)!); Directory.Move(Path.GetDirectoryName(pointer)!, trashed);
                Require(repository.List().Count == 0, "移入回收区的章节仍被列出");
                repository.CleanAbandonedImports();
                Require(!Directory.Exists(trashed) && repository.Find(pack.Id) == null, "中断清理失败或章节复活");
            });
        });

        Test("静音确认恢复位置不重复履历或改变路线", () =>
        {
            var original = new PlaybackEngine(Fixture()); original.Commit("first"); original.Next(true);
            var engine = new PlaybackEngine(original.Pack); Require(engine.ImportNavigation(original.ExportNavigation()), "存档恢复失败");
            var history = JsonSerializer.Serialize(engine.History, Json.Options); var choices = JsonSerializer.Serialize(engine.Choices, Json.Options);
            int plays = 0; engine.PlayRequested += _ => plays++;
            Require(engine.Mode == RunMode.Ready && engine.ConfirmCurrentPosition() && engine.Mode == RunMode.Following && plays == 0, "确认 Ready 位置不是静音");
            Require(JsonSerializer.Serialize(engine.History, Json.Options) == history && JsonSerializer.Serialize(engine.Choices, Json.Options) == choices, "确认位置重复履历或改了路线");
            engine.PauseForBrowse(); Require(engine.ConfirmCurrentPosition() && engine.Mode == RunMode.Following && plays == 0, "暂停位置不能静音确认");
            Require(engine.ConfirmCurrentPosition() && plays == 0 && engine.History.Count == original.History.Count, "重复确认重播或增加历史");
            engine.Next(true); Require(engine.CurrentId == "third" && plays == 1, "确认后无法手动推进");
        });
        Test("原声、菜单、边界和不允许位置不能被静音确认绕过", () =>
        {
            var engine = new PlaybackEngine(Fixture()); engine.Commit("first"); engine.EnterOriginal();
            Require(!engine.ConfirmCurrentPosition() && engine.Mode == RunMode.Original, "原声被静音确认解除");
            foreach (string kind in new[] { "choice", "merge", "gap", "end" })
            {
                var pack = Fixture(); pack.ById["first"].Kind = kind; var e = new PlaybackEngine(pack); e.Commit("first");
                Require(!e.ConfirmCurrentPosition() && e.CurrentId == "first", "边界被静音确认绕过：" + kind);
            }
            var invalid = new PlaybackEngine(Fixture()); invalid.Commit("first"); invalid.PauseForBrowse(); invalid.Current!.Archived = true;
            Require(!invalid.ConfirmCurrentPosition() && invalid.Mode == RunMode.Paused, "不允许的当前位置被确认");
        });
        Test("未核实单句静音确认保持 ReviewRoute 和单句边界", () =>
        {
            var pack = Fixture(); pack.ById["second"].PathId = pack.ById["third"].PathId = "unknown";
            pack.Nodes.Add(new() { Id = "menu", Kind = "choice", SectionId = "section", Options = [new() { Id = "option", Label = "待核实路线", TargetId = "second", PathId = "unknown", LineIds = ["second", "third"] }] });
            pack.Validate(); var source = new PlaybackEngine(pack); Require(source.ConfirmGameLine("second"), "无法确认未核实单句");
            var engine = new PlaybackEngine(pack); Require(engine.ImportNavigation(source.ExportNavigation()), "单句存档恢复失败");
            var before = engine.ExportNavigation(); int plays = 0; engine.PlayRequested += _ => plays++;
            Require(engine.ConfirmCurrentPosition() && plays == 0 && engine.ReviewRoute == "option" && engine.History.Count == before.Visits.Count && engine.ExportNavigation().Current.SingleLine, "单句限制被静音确认清空");
            var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic);
            Require(!safety.ConfirmPosition(engine) && engine.GetAutomaticNextLine() == null, "待核实单句解锁自动跟随");
            engine.Next(true); Require(engine.CurrentId == "menu" && engine.Mode == RunMode.Choice && plays == 0, "单句确认后越过未核实正文");
        });
        Test("启动和模式切换静音，确认后两次完整 OCR 才推进", () =>
        {
            var engine = new PlaybackEngine(Fixture()); engine.Commit("first"); int plays = 0; engine.PlayRequested += _ => plays++;
            var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic);
            var ignored = safety.Evaluate(engine, Observation(safety, 1, "new", "第二句完整台词"));
            Require(ignored.Kind == FollowDecisionKind.Ignored && plays == 0, "未确认位置就开始推进");
            Require(safety.ConfirmPosition(engine, "old"), "明确位置无法启用");
            var first = Observation(safety, 1, "new", "第二句完整台词");
            Require(safety.Evaluate(engine, first).Kind == FollowDecisionKind.Waiting, "一次 OCR 就推进");
            var second = Observation(safety, 2, "new", "第二句完整台词"); var decision = safety.Evaluate(engine, second);
            Require(decision.Kind == FollowDecisionKind.Advance && safety.TryApply(engine, second, decision), "明确下一句无法推进");
            Require(engine.CurrentId == "second" && plays == 1 && !safety.TryApply(engine, second, decision), "重复推进或播放");
        });
        Test("逐字、模糊、快跳和低置信文字都交给候选", () =>
        {
            foreach (string text in new[] { "第二句", "第二句完整台词错", "第三句完整台词" })
            {
                var (engine, safety) = Following();
                for (int i = 1; i <= 3; i++) Require(safety.Evaluate(engine, Observation(safety, i, "new", text)).Kind != FollowDecisionKind.Advance, "错误文字自动推进：" + text);
                Require(engine.CurrentId == "first", "候选直接修改进度");
            }
            var (e, s) = Following();
            var low = Observation(s, 1, "new", "第二句完整台词") with { Blocks = [new() { Text = "第二句完整台词", Score = .70 }] };
            Require(s.Evaluate(e, low).Kind == FollowDecisionKind.Candidates && s.Evaluate(e, low with { Sequence = 2 }).Kind == FollowDecisionKind.Candidates, "低置信 OCR 自动推进");
        });
        Test("不稳定或不匹配帧打断连续确认", () =>
        {
            var (engine, safety) = Following();
            safety.Evaluate(engine, Observation(safety, 1, "new", "第二句完整台词"));
            safety.Evaluate(engine, Observation(safety, 2, "typing", "第二句") with { IsStable = false });
            Require(safety.Evaluate(engine, Observation(safety, 3, "new", "第二句完整台词")).Kind == FollowDecisionKind.Waiting, "逐字显示没有打断连续确认");
            safety.Evaluate(engine, Observation(safety, 4, "wrong", "别的文本"));
            Require(safety.Evaluate(engine, Observation(safety, 5, "new", "第二句完整台词")).Kind == FollowDecisionKind.Waiting, "不匹配帧没有打断连续确认");
        });
        Test("同一次 OCR 不可重复计数，晚到结果不可反向确认", () =>
        {
            var (engine, safety) = Following(); var first = Observation(safety, 5, "new", "第二句完整台词");
            safety.Evaluate(engine, first);
            Require(safety.Evaluate(engine, first).Kind == FollowDecisionKind.Ignored, "同一结果重复计数");
            Require(safety.Evaluate(engine, first with { Sequence = 4 }).Kind == FollowDecisionKind.Ignored, "过期截图反向确认");
        });
        Test("切模式、原声、暂停、切章和手动推进丢弃旧 OCR", () =>
        {
            foreach (var operation in new Action<PlaybackEngine, FollowSafetyController>[]
            {
                (e,s) => s.SetMode(FollowMode.Manual), (e,s) => s.Invalidate(),
                (e,s) => e.EnterOriginal(), (e,s) => e.TogglePause(), (e,s) => e.Next(true)
            })
            {
                var (engine, safety) = Following();
                safety.Evaluate(engine, Observation(safety, 1, "new", "第二句完整台词"));
                var last = Observation(safety, 2, "new", "第二句完整台词"); var ready = safety.Evaluate(engine, last);
                operation(engine, safety); string? position = engine.CurrentId; int plays = 0; engine.PlayRequested += _ => plays++;
                Require(!safety.TryApply(engine, last, ready) && engine.CurrentId == position && plays == 0, "旧 OCR 覆盖新状态");
            }
            var (oldEngine, controller) = Following(); var nextPack = Fixture(); nextPack.Id = "另一个章节"; var newEngine = new PlaybackEngine(nextPack); newEngine.Commit("first");
            Require(controller.Evaluate(newEngine, Observation(controller, 1, "new", "第二句完整台词")).Kind == FollowDecisionKind.Ignored && !controller.IsArmed, "切章保留旧跟随");
        });
        Test("同文同画面重复句保留手动下一句", () =>
        {
            var pack = Fixture(); pack.Nodes[1].Text = pack.Nodes[0].Text;
            var engine = new PlaybackEngine(pack); engine.Commit("first"); var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic); safety.ConfirmPosition(engine, "same");
            for (int i = 1; i <= 3; i++) Require(safety.Evaluate(engine, Observation(safety, i, "same", pack.Nodes[0].Text)).Kind == FollowDecisionKind.Waiting, "相同画面误播重复句");
            engine.Next(true); Require(engine.CurrentId == "second", "重复句手动按钮不可用");
        });
        Test("相邻同规范文本即使白色背景变化也必须手动确认", () =>
        {
            var pack = Fixture(); pack.ById["first"].Text = "我们出发吧。"; pack.ById["second"].Text = "我们出发吧！";
            var engine = new PlaybackEngine(pack); engine.Commit("first"); var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic); safety.ConfirmPosition(engine, "old");
            int plays = 0; engine.PlayRequested += _ => plays++;
            for (int i = 1; i <= 4; i++)
            {
                var observation = Observation(safety, i, "background-changed-" + i, pack.ById["second"].Text);
                var decision = safety.Evaluate(engine, observation);
                Require(decision.Kind == FollowDecisionKind.Candidates && !safety.TryApply(engine, observation, decision), "相邻同文被背景变化误播");
            }
            Require(plays == 0 && engine.CurrentId == "first", "同规范文本自动重复播音");
            engine.Next(true); Require(plays == 1 && engine.CurrentId == "second", "同文手动下一句失效");
        });
        Test("未给基准画面时先记录当前字幕，不自行发声", () =>
        {
            var (engine, safety) = Following(); safety.ConfirmPosition(engine);
            for (int i = 1; i <= 3; i++) Require(safety.Evaluate(engine, Observation(safety, i, "current", "第二句完整台词")).Kind == FollowDecisionKind.Waiting, "首次画面被当成变化");
        });
        Test("菜单、汇合、缺口、结束和跨小节均不能自动越过", () =>
        {
            foreach (string kind in new[] { "choice", "merge", "gap", "end" })
            {
                var pack = Fixture(); pack.ById["second"].Kind = kind; var engine = new PlaybackEngine(pack); engine.Commit("first");
                Require(engine.GetAutomaticNextLine() == null && !engine.TryAdvanceAutomatically("second"), "自动越过边界 " + kind);
            }
            var cross = Fixture(); cross.ById["second"].SectionId = "other"; var e = new PlaybackEngine(cross); e.Commit("first");
            Require(e.GetAutomaticNextLine() == null, "跨小节自动推进");
        });
        Test("错误路线和单句确认不放开自动推进", () =>
        {
            var pack = Fixture(); pack.ById["second"].PathId = "unknown-route";
            var engine = new PlaybackEngine(pack); engine.Commit("first");
            Require(engine.GetAutomaticNextLine() == null, "进入未选择路线");
            var direct = new PlaybackEngine(Fixture()); direct.CommitSingle("first");
            Require(direct.GetAutomaticNextLine() == null, "单句确认变成持续跟随");
        });
        Test("音频路径拒绝 Windows 根目录和两种越界分隔符", () =>
        {
            var pack = Fixture();
            foreach (string path in new[] { "../x.wav", "..\\x.wav", "C:\\x.wav", "/tmp/x.wav", "\\\\server\\x.wav" })
            { pack.Nodes[0].Audio = path; Reject(() => pack.ResolveAudio(pack.Nodes[0])); }
        });
        Console.WriteLine($"ANDROID CORE: {count} groups passed");
    }

    static (PlaybackEngine, FollowSafetyController) Following()
    {
        var engine = new PlaybackEngine(Fixture()); engine.Commit("first");
        var safety = new FollowSafetyController(); safety.SetMode(FollowMode.Automatic); safety.ConfirmPosition(engine, "old");
        return (engine, safety);
    }
    static FollowObservation Observation(FollowSafetyController safety, long sequence, string frame, string text) =>
        new(safety.Epoch, sequence, frame, true, [new() { Text = text, Score = .99 }], "section");
    static Pack Fixture()
    {
        var pack = new Pack
        {
            Id = "安卓测试章", Title = "安卓测试章", SchemaVersion = 3, Root = Path.GetTempPath(),
            Chapters = [new() { Id = "chapter", Title = "测试章", Sections = [new() { Id = "section", Title = "测试节", StartId = "first" }] }],
            Nodes = [new() { Id = "first", SectionId = "section", Text = "第一句完整台词", Speaker = "丽芙", NextId = "second" },
                new() { Id = "second", SectionId = "section", Text = "第二句完整台词", Speaker = "丽芙", NextId = "third" },
                new() { Id = "third", SectionId = "section", Text = "第三句完整台词", Speaker = "丽芙" }]
        };
        pack.Validate(); return pack;
    }
    static MemoryStream Archive(Pack pack, params (string Path, byte[] Bytes)[] extras)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var output = archive.CreateEntry("章节目录/pack.json").Open()) output.Write(JsonSerializer.SerializeToUtf8Bytes(pack, Json.Options));
            foreach (var (path, data) in extras)
            {
                // 越界样本保留原始路径，其余与 pack.json 位于同一章节目录。
                string target = path.StartsWith("../") || path.StartsWith("..\\") || path.StartsWith('/') || path.Contains(':') || path.Contains("../..") ? path : "章节目录/" + path;
                using var output = archive.CreateEntry(target).Open(); output.Write(data);
            }
        }
        stream.Position = 0; return stream;
    }
    static void WithRepository(Action<PackageRepository, string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "pgr-android-core-" + Guid.NewGuid().ToString("N"));
        try { action(new PackageRepository(Path.Combine(folder, "packages")), folder); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException) { return; }
        throw new Exception("本应拒绝的操作成功了");
    }
    sealed class InlineProgress(Action<PackageImportProgress> action) : IProgress<PackageImportProgress>
    { public void Report(PackageImportProgress value) => action(value); }
    sealed class NonSeekableStream(Stream source) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
