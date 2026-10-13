using PgrVoice;

public static class ChapterCoverTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        // 标题拆分与编号显示
        Check(ChapterCover.Split("第43章 远信回响") == ("43", "远信回响"), "「第N章」拆分错误");
        Check(ChapterCover.Split("01 涂鸦艺术") == ("01", "涂鸦艺术"), "前置编号拆分错误");
        Check(ChapterCover.Split("EX-02 · 暗影之下") == ("EX-02", "暗影之下"), "字母编号拆分错误");
        Check(ChapterCover.Split("灰烬之羽") == ("", "灰烬之羽") && ChapterCover.Split("  ") == ("", ""), "无编号标题拆分错误");
        Check(ChapterCover.DisplayNumber("7") == "07" && ChapterCover.DisplayNumber("43") == "43" && ChapterCover.DisplayNumber("EX-02") == "EX-02", "编号显示错误");

        // 候选顺序：小节 → 全名 → 名称 → 编号 → 包编号
        var names = ChapterCover.CandidateNames("第43章 远信回响", "voice-v2-MAIN43", "普通 · 43-1 远信", "s43-1");
        int I(string n) => names.FindIndex(x => x == n);
        Check(I("第43章 远信回响/普通 · 43-1 远信") == 0, "小节子文件夹没有最优先：" + string.Join("|", names));
        Check(I("远信回响 43-1 远信") > 0 && I("s43-1") > I("远信回响 43-1 远信"), "小节候选顺序错误：" + string.Join("|", names));
        Check(I("s43-1") < I("第43章 远信回响") && I("第43章 远信回响") < I("远信回响") && I("远信回响") < I("43") && I("43") < I("voice-v2-MAIN43") && I("voice-v2-MAIN43") < I("MAIN43"),
            "章节候选顺序错误：" + string.Join("|", names));
        Check(I("普通 · 43-1 远信") < 0 && I("43-1 远信") < 0, "单独的小节名不应作为候选");
        Check(ChapterCover.CandidateNames("第7章 灰烬", "").Contains("07") && ChapterCover.CandidateNames("", "").Count == 0, "编号补零或空标题错误");
        // 实际封面命名：主线第01章 / 主线第35章_覆海归途 / 主线序章 / 外篇EX00 / 间章_青鸟；配音包文件夹「主线第01章_MAIN01」
        var real = ChapterCover.CandidateNames("01 涂鸦艺术", "MAIN01", packFolder: "主线第01章_MAIN01");
        Check(real.Contains("主线第01章") && real.IndexOf("主线第01章") < real.IndexOf("涂鸦艺术"), "主线编号命名缺失：" + string.Join("|", real));
        var ch35 = ChapterCover.CandidateNames("35 覆海归途", "voice-v2-MAIN35");
        Check(ch35.IndexOf("主线第35章_覆海归途") >= 0 && ch35.IndexOf("主线第35章_覆海归途") < ch35.IndexOf("主线第35章"), "同章多封面应先匹配带名称的文件：" + string.Join("|", ch35));
        Check(ChapterCover.CandidateNames("EX00 极地暗流", "", category: "外篇剧情").Contains("外篇EX00"), "外篇编号命名缺失");
        Check(ChapterCover.CandidateNames("青鸟", "", category: "间章剧情").Contains("间章_青鸟"), "间章命名缺失");
        Check(ChapterCover.CandidateNames("序章", "", category: "主线").Contains("主线序章"), "序章命名缺失");
        Check(ChapterCover.CandidateNames("某章", "", packFolder: "主线第09章_MAIN09").Contains("主线第09章"), "配音包文件夹前缀缺失");
        Console.WriteLine("PASS 章节封面：标题拆分、编号显示、候选优先级（小节 → 全名 → 名称 → 编号 → 包编号）");

        string temp = Path.Combine(Path.GetTempPath(), "pgr-cover-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 查找链与头像相同，含配音总目录旁的「章节封面」
            string library = Path.Combine(temp, "战双配音软件及配音包", "全部配音");
            string beside = Path.Combine(temp, "战双配音软件及配音包", CharacterArt.CoverFolderName);
            var folders = CharacterArt.SearchFolders(null, library, Path.Combine(temp, "appdata"), CharacterArt.CoverFolderName);
            Check(folders.Count == 3 && folders[0] == Path.GetFullPath(Path.Combine(library, CharacterArt.CoverFolderName)) && folders[1] == Path.GetFullPath(beside)
                && folders[2].EndsWith(Path.Combine("PgrVoice", CharacterArt.CoverFolderName)), "封面查找链错误：" + string.Join(" | ", folders));

            Directory.CreateDirectory(beside);
            void Touch(string relative) { string f = Path.Combine(beside, relative); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, new byte[] { 1 }); }
            Touch("43.jpg"); Touch("远信回响.webp"); Touch("远信回响.png"); Touch("其他.txt");
            var index = new ChapterCoverIndex(folders);
            Check(index.ExistingFolders.Count == 1 && index.Count == 2, "封面扫描数量错误：" + index.Count);
            Check(index.Find("第43章 远信回响", "voice-v2-MAIN43")!.EndsWith("远信回响.png"), "名称应优先于编号，且 png 优先于 webp");
            Check(index.Find("第43章 别的名字", "")!.EndsWith("43.jpg"), "编号后备失败");
            Check(index.Find("第44章 无图", "x") == null, "不存在的封面应返回空");

            Touch(Path.Combine("远信回响", "43-1 远信.jpg"));
            Touch("MAIN50.png");
            File.WriteAllText(Path.Combine(beside, CharacterArt.AliasFileName), "{ // 别名\n \"第45章 新名\": \"MAIN50\", }");
            index = new ChapterCoverIndex(folders);
            Check(index.Find("第43章 远信回响", "", "43-1 远信")!.EndsWith(Path.Combine("远信回响", "43-1 远信.jpg")), "小节封面没有优先");
            Check(index.Find("第43章 远信回响", "", "43-2 别处")!.EndsWith("远信回响.png"), "无小节封面时应回退到章节封面");
            Check(index.Find("第45章 新名", "")!.EndsWith("MAIN50.png"), "aliases.json 未生效");
            Check(index.Find("第51章 x", "voice-v2-MAIN50")!.EndsWith("MAIN50.png"), "去前缀的包编号后备失败");
            Touch("主线第01章.png"); Touch("主线第35章.png"); Touch("主线第35章_覆海归途.png");
            index = new ChapterCoverIndex(folders);
            Check(index.Find("01 涂鸦艺术", "MAIN01", packFolder: "主线第01章_MAIN01")!.EndsWith("主线第01章.png"), "实际封面名（主线第01章）未匹配");
            Check(index.Find("35 覆海归途", "MAIN35")!.EndsWith("主线第35章_覆海归途.png") && index.Find("35 覆海离声", "MAIN35")!.EndsWith("主线第35章.png"), "同章两张封面未区分");
            Check(index.Find("35 覆海归途", "MAIN35", packFolder: "主线第35章_MAIN35")!.EndsWith("主线第35章_覆海归途.png"), "带章节名的封面应先于配音包前缀");
            Touch("主线序章.png"); index = new ChapterCoverIndex(folders);
            Check(index.Find("序章", "")!.EndsWith("主线序章.png"), "无分类时序章未匹配主线序章");
            Console.WriteLine("PASS 章节封面：查找链（含总目录旁）、扩展名优先、小节子文件夹、别名、编号与包编号后备");
        }
        finally { try { Directory.Delete(temp, true); } catch (Exception) { } }
    }
}
