using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PgrVoice;

/// <summary>
/// 0.4.8 文字跟随匹配检查：同文同角色重复短句按当前位置消歧、个别错字别名。
/// 纯逻辑检查，不播放、不点击游戏、不写配音包；真实第32章 32-6 作为只读夹具。
/// </summary>
public partial class MainWindow
{
    async Task RunGameTextMatchTest()
    {
        var report = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); report.Add("PASS: " + name); }
        try
        {
            await Task.Yield();
            // 一、合成夹具：同一小节两条“曲：……”，两条路线。
            const string S = "s1";
            Node L(string id, string speaker, string text, string? next, string path = "") =>
                new() { Id = id, SectionId = S, Kind = "line", Speaker = speaker, Text = text, NextId = next, PathId = path };
            var synthetic = new Pack
            {
                Id = "test-match", Title = "匹配检查", SchemaVersion = 3,
                Chapters = { new Chapter { Title = "章", Sections = { new Section { Id = S, Title = "节", StartId = "a1" } } } },
                Nodes =
                {
                    L("a1", "华胥", "更正，已经4775天。", "a2"), L("a2", "曲", "……", "a3"), L("a3", "旁白", "她抬头。", "a4"),
                    L("a4", "曲", "……", "a5"), L("a5", "旁白", "风雪。", "c1"),
                    new Node { Id = "c1", SectionId = S, Kind = "choice", NextId = null },
                    L("p1", "曲", "……", "p2", "pathA"), L("p2", "旁白", "分支里。", null, "pathA"),
                    L("f1", "旁白", "远处。", "f2"), L("f2", "旁白", "更远。", "f3"), L("f3", "旁白", "再远。", "f4"), L("f4", "旁白", "又远。", "f5"),
                    L("f5", "旁白", "很远。", "f6"), L("f6", "旁白", "极远。", "f7"), L("f7", "旁白", "尽头。", "a4"),
                }
            };
            synthetic.Validate();
            Check(GameTextPolicy.Match(synthetic, S, "……", out string reason, "曲") == null && reason.Contains("多个位置"),
                "没有当前位置时，同文同角色多处仍按原规则等待");
            Check(GameTextPolicy.Match(synthetic, S, "……", out _, "曲", synthetic.ById["a1"])?.Id == "a2", "当前句的直接下一句优先");
            Check(GameTextPolicy.Match(synthetic, S, "……", out _, "曲", synthetic.ById["a2"])?.Id == "a4", "从当前“……”沿顺序找到下一个“……”，不选自己");
            Check(GameTextPolicy.Match(synthetic, S, "……", out _, "曲", synthetic.ById["a3"])?.Id == "a4", "中间隔一句旁白仍能定位");
            Check(GameTextPolicy.Match(synthetic, S, "……", out reason, "曲", synthetic.ById["a5"]) == null && reason.Contains("多个位置"),
                "前方是选项节点时不跨越，仍等待");
            Check(GameTextPolicy.Match(synthetic, S, "……", out reason, "曲", synthetic.ById["p2"]) == null && reason.Contains("多个位置"),
                "路线尽头无后继时仍等待");
            Check(GameTextPolicy.Match(synthetic, S, "……", out reason, "曲", synthetic.ById["f1"]) == null && reason.Contains("多个位置"),
                "超过 " + GameTextPolicy.LookAheadLimit + " 步不再猜测");
            Check(GameTextPolicy.Match(synthetic, S, "……", out _, "曲", synthetic.ById["f2"])?.Id == "a4", "恰在步数上限内可定位");
            Check(GameTextPolicy.Match(synthetic, S, "她抬头。", out _, "旁白", synthetic.ById["a1"])?.Id == "a3", "唯一匹配不受当前位置影响");
            Check(GameTextPolicy.Match(synthetic, S, "没有这句", out reason, "曲", synthetic.ById["a1"]) == null && reason.Contains("暂未匹配"),
                "未匹配文字不因位置被猜成任何一句");
            Check(GameTextPolicy.PreferByPosition(synthetic, synthetic.ById["a1"], new[] { synthetic.ById["p1"], synthetic.ById["a4"] })?.Id == "a4",
                "消歧只在候选里选，不跨路线取另一条路线的同文句");

            // 二、真实第32章 32-6 只读夹具（当前库里有第32章时）。
            var ch32File = LibraryBox.Items.OfType<PackChoice>().Select(c => c.File)
                .FirstOrDefault(f => { try { return Pack.Load(f).Id == "pgr-ch32"; } catch { return false; } });
            if (ch32File != null)
            {
                var pack = DesktopPackLoader.Load(ch32File);
                const string section = "ch32-71fcf055b68c2cfb1350";
                var dots = pack.Nodes.Where(n => n.Kind == "line" && !n.Archived && n.SectionId == section && n.Speaker == "曲" && n.Text == "……").ToList();
                Check(dots.Count >= 2, "32-6 有多条“曲：……”（实际 " + dots.Count + " 条）");
                Check(GameTextPolicy.Match(pack, section, "……", out reason, "曲") == null && reason.Contains("多个位置"), "32-6 无当前位置时“曲：……”等待");
                var huaxu = pack.Nodes.Single(n => n.SectionId == section && n.Speaker == "华胥" && n.Text.Contains("4775"));
                var afterHuaxu = pack.ById[huaxu.NextId!];
                Check(afterHuaxu.Speaker == "曲" && afterHuaxu.Text == "……", "华胥「4775天」的下一句是“曲：……”");
                Check(GameTextPolicy.Match(pack, section, "……", out _, "曲", huaxu)?.Id == afterHuaxu.Id, "日志卡住处 1：华胥句后读到“……”选中其下一句");
                var tide = pack.Nodes.Single(n => n.SectionId == section && n.Text.StartsWith("红潮絮絮着凝结出"));
                Check(GameTextPolicy.Match(pack, section, "……", out _, "曲", tide)?.Id == tide.NextId, "日志卡住处 2：「红潮絮絮」后读到“……”选中其下一句");
                var gaze = pack.Nodes.Single(n => n.SectionId == section && n.Text.StartsWith("她眸正神清"));
                Check(GameTextPolicy.Match(pack, section, "……", out _, "曲", gaze)?.Id == tide.NextId, "中间夹一句红字低语（无论包里有没有）仍能定位到后面的“……”");
                // 2026-10-13 特效文字补配入包后，这句红字低语已在 32-6 包内；未入包的旧库则应等待，不被位置猜成别句。
                var whisper = pack.Nodes.FirstOrDefault(n => n.SectionId == section && n.Kind == "line" && !n.Archived && n.Text == "他们早已抛弃了你……");
                var whisperMatch = GameTextPolicy.Match(pack, section, "他们早已抛弃了你……", out reason, "", huaxu);
                if (whisper != null) Check(whisperMatch?.Id == whisper.Id, "已补入包的红字低语直接匹配到新增节点");
                else Check(whisperMatch == null && reason.Contains("暂未匹配"), "包里没有的红字低语仍等待，不被位置猜成别句");
                // 三、错字别名：游戏“长大了”，包里“长大子”，只对登记节点生效。
                var typo = pack.Nodes.Single(n => n.SectionId == section && n.Speaker == "狻猊众" && n.Text.Contains("等你长大"));
                Check(GameTextPolicy.AliasCount >= 1, "内置错字别名已加载");
                if (typo.Text == "没关系，等你长大子，你会明白的。")
                {
                    Check(GameTextPolicy.Match(pack, section, "没关系，等你长大了，你会明白的。", out _, "狻猊众")?.Id == typo.Id, "日志卡住处 3：游戏“长大了”匹配到包内“长大子”那句");
                    Check(GameTextPolicy.Match(pack, section, "没关系，等你长大了，你会明白的。", out _, "")?.Id == typo.Id, "无角色名时别名同样生效");
                    Check(GameTextPolicy.Match(pack, section, "没关系，等你长大了，你会明白的。", out reason, "曲") == null, "角色不符时别名不生效");
                    Check(GameTextPolicy.Match(pack, section, "没关系，等你长大啦，你会明白的。", out reason, "狻猊众") == null, "别名只接受登记的那一句游戏正文");
                    Check(typo.Text.Contains("长大子") && typo.Audio != null, "别名不改配音包显示文本和音频绑定");
                }
                else report.Add("INFO: 32-6 该句正文已被修正为“" + typo.Text + "”，错字别名不再需要，跳过别名用例。");
            }
            else report.Add("INFO: 当前库没有第32章，真实夹具用例跳过。");
            report.Add("说明：以上为纯匹配逻辑与只读真实配音包检查，未播放音频、未点击游戏、未修改配音包。");
        }
        catch (Exception ex) { report.Add("FAIL: " + ex); }
        File.WriteAllLines(Path.Combine(Log.DataDir, "game-text-match-test.txt"), report); Close();
    }
}
