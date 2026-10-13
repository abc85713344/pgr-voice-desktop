using PgrVoice;

public static class CharacterArtTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        // 名称清理
        Check(CharacterArt.SanitizeName("  露西亚:深红?之渊 ") == "露西亚深红之渊", "非法文件名字符未清理");
        Check(CharacterArt.SanitizeName("指挥官.") == "指挥官" && CharacterArt.SanitizeName("   ") == "", "首尾点或空名处理错误");
        var names = CharacterArt.CandidateNames("丽芙·极昼（通讯）");
        Check(names.SequenceEqual(new[] { "丽芙·极昼（通讯）", "丽芙·极昼", "丽芙" }), "候选名顺序错误：" + string.Join("|", names));
        Check(CharacterArt.CandidateNames("队员 · 甲").Last() == "队员", "「 · 」后缀未作为后备");
        Check(CharacterArt.CandidateNames("").Count == 0, "空说话人产生候选");
        Check(CharacterArt.Monogram("旁白") == "旁" && CharacterArt.Monogram("？？？") == "声" && CharacterArt.Monogram("alpha") == "A" && CharacterArt.Monogram("（通讯）指挥官") == "指", "字标错误");
        Check(CharacterArt.Serial("指挥官") == CharacterArt.Serial("指挥官") && CharacterArt.Serial("指挥官").StartsWith("CN-") && CharacterArt.Serial("指挥官").Length == 7, "编号不稳定");
        Console.WriteLine("PASS 角色图片：说话人名称清理、括号与「·」后备、字标与编号");

        // 查找顺序：设置文件夹 → 总目录内 → 总目录旁 → AppData
        string temp = Path.Combine(Path.GetTempPath(), "pgr-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            string library = Path.Combine(temp, "战双配音软件及配音包", "全部配音");
            string beside = Path.Combine(temp, "战双配音软件及配音包", CharacterArt.AvatarFolderName);
            string inside = Path.Combine(library, CharacterArt.AvatarFolderName);
            string custom = Path.Combine(temp, "custom");
            string appData = Path.Combine(temp, "appdata");
            var folders = CharacterArt.SearchFolders(custom, library, appData);
            Check(folders.SequenceEqual(new[] { Path.GetFullPath(custom), Path.GetFullPath(inside), Path.GetFullPath(beside), Path.GetFullPath(Path.Combine(appData, "PgrVoice", CharacterArt.AvatarFolderName)) }),
                "查找顺序错误：" + string.Join(" | ", folders));
            Check(CharacterArt.SearchFolders("", library + Path.DirectorySeparatorChar, null).First() == Path.GetFullPath(inside), "末尾分隔符导致总目录识别错误");
            Check(CharacterArt.SearchFolders(custom, null, null).Count == 1 && CharacterArt.SearchFolders(custom, custom, null).Count == 3, "去重或空值处理错误");

            Directory.CreateDirectory(beside); Directory.CreateDirectory(Path.Combine(beside, CharacterArt.StandeeFolderName));
            Directory.CreateDirectory(custom); Directory.CreateDirectory(appData);
            File.WriteAllBytes(Path.Combine(beside, "指挥官.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(beside, "丽芙.webp"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(beside, "丽芙.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(beside, "说明.txt"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(beside, CharacterArt.StandeeFolderName, "指挥官.jpeg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(custom, "指挥官.jpg"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(beside, CharacterArt.AliasFileName), "{ \"Commandant\": \"指挥官\", \"露娜（伪装）\": \"露娜.png\", // 注释\n }");
            var index = new CharacterArtIndex(CharacterArt.SearchFolders(custom, library, Path.Combine(temp, "appdata")));
            Check(index.ExistingFolders.Count == 2, "只应收录存在的文件夹");
            Check(index.FindAvatar("指挥官") == Path.Combine(custom, "指挥官.jpg"), "设置文件夹未优先");
            Check(index.FindAvatar("丽芙·极昼") == Path.Combine(beside, "丽芙.jpg"), "基础名后备或扩展名优先级错误（jpg 应先于 webp）");
            Check(index.FindAvatar("Commandant") == Path.Combine(custom, "指挥官.jpg"), "别名未生效");
            Check(index.FindAvatar("说明") == null && index.FindAvatar("未知角色") == null && index.FindAvatar(null) == null, "非图片或未知角色被匹配");
            Check(index.FindStandee("指挥官") == Path.Combine(beside, CharacterArt.StandeeFolderName, "指挥官.jpeg") && index.StandeeCount == 1, "立绘子文件夹查找错误");
            Check(index.AvatarCount == 2 && index.Aliases.Count == 2 && index.Aliases["露娜（伪装）"] == "露娜", "计数或别名扩展名处理错误");

            // 只有总目录旁的文件夹（无需任何设置）也能找到
            var auto = new CharacterArtIndex(CharacterArt.SearchFolders("", library, null));
            Check(auto.FindAvatar("指挥官") == Path.Combine(beside, "指挥官.png"), "未自动找到总目录旁的「角色头像」");

            File.WriteAllText(Path.Combine(beside, CharacterArt.AliasFileName), "{ broken");
            Check(CharacterArt.ReadAliases(beside).Count == 0, "损坏的 aliases.json 应被忽略");
            Check(CharacterArtIndex.Empty.FindAvatar("指挥官") == null, "空索引应返回缺失");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
        Console.WriteLine("PASS 角色图片：查找顺序（设置→总目录内→总目录旁→AppData）、扩展名优先级、别名、立绘子文件夹、损坏配置容错");
        BundledRun();
    }

    // 内置图片包：本机文件夹优先，未命中时用内置；内置别名不覆盖本机同名文件。
    static void BundledRun()
    {
        var zipBytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Put(string name, byte[] data) { using var e = zip.CreateEntry(name).Open(); e.Write(data); }
            Put("角色头像/露西亚.png", new byte[] { 7, 7 }); Put("角色头像/丽芙.png", new byte[] { 8 }); Put("角色头像/“国王”.png", new byte[] { 9 });
            Put("角色头像/说明.txt", new byte[] { 1 }); Put("角色头像/立绘/露西亚.png", new byte[] { 1 });
            Put("角色头像/aliases.json", System.Text.Encoding.UTF8.GetBytes("{ \"国王\": \"“国王”\", \"露西亚？\": \"露西亚\" }"));
            Put("章节封面/主线第01章.jpg", new byte[] { 5 }); Put("章节封面/浮点纪实_游云鲸梦.jpg", new byte[] { 6 });
            Put("章节封面/aliases.json", System.Text.Encoding.UTF8.GetBytes("{ \"ER00\": \"浮点纪实_游云鲸梦\" }"));
        }
        zipBytes.Position = 0;
        var bundle = new ArtBundle(zipBytes);
        Check(!bundle.IsEmpty && bundle.Files(CharacterArt.AvatarFolderName).Count == 3, "内置头像条目数错误（应忽略非图片与子文件夹）");
        string temp = Path.Combine(Path.GetTempPath(), "pgr-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllBytes(Path.Combine(temp, "丽芙.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(temp, "国王.png"), new byte[] { 1 });
            var index = new CharacterArtIndex(new[] { temp }, bundle);
            Check(index.FindAvatar("丽芙") == Path.Combine(temp, "丽芙.jpg"), "本机头像未优先于内置");
            Check(index.FindAvatar("国王") == Path.Combine(temp, "国王.png"), "内置别名不应盖过本机同名文件");
            string? lucia = index.FindAvatar("露西亚？");
            Check(ArtBundle.IsBundled(lucia) && bundle.Read(lucia!) is { Length: 2 }, "本机缺图时未回退到内置头像（含内置别名）");
            Check(index.FindAvatar("未知角色") == null && index.FindStandee("露西亚") == null, "未知角色或立绘不应匹配内置头像");
            Check(index.AvatarCount == 4 && index.LocalAvatarCount == 2 && index.BundledAvatarCount == 3, "头像计数错误：" + index.AvatarCount);
            var empty = new CharacterArtIndex(new[] { Path.Combine(temp, "missing") }, bundle);
            Check(empty.ExistingFolders.Count == 0 && ArtBundle.IsBundled(empty.FindAvatar("丽芙·极昼")), "无本机文件夹时应直接使用内置头像");
            Check(new CharacterArtIndex(new[] { temp }).FindAvatar("露西亚") == null, "未传入内置包时不应有内置头像");

            var covers = new ChapterCoverIndex(new[] { Path.Combine(temp, "nocovers") }, bundle);
            Check(covers.BundledCount == 2 && covers.Count == 2, "内置封面计数错误");
            Check(covers.Find("01 涂鸦艺术", "MAIN01", packFolder: "主线第01章_MAIN01") == ArtBundle.PathPrefix + "章节封面/主线第01章.jpg", "内置封面查找失败");
            Check(covers.Find("ER00", "") == ArtBundle.PathPrefix + "章节封面/浮点纪实_游云鲸梦.jpg", "内置封面别名失败");
            File.WriteAllBytes(Path.Combine(temp, "主线第01章.png"), new byte[] { 1 });
            Check(new ChapterCoverIndex(new[] { temp }, bundle).Find("01 涂鸦艺术", "MAIN01") == Path.Combine(temp, "主线第01章.png"), "本机封面未优先于内置");
            Check(bundle.Read("bundled:章节封面/不存在.jpg") == null && bundle.Read(Path.Combine(temp, "丽芙.jpg")) == null && ArtBundle.None.Files("角色头像").Count == 0, "缺失条目或空包处理错误");
            Check(new ArtBundle(new MemoryStream(new byte[] { 1, 2, 3 })).IsEmpty, "损坏的图片包应视为空");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
        Console.WriteLine("PASS 内置图片包：本机优先、内置回退（头像 / 封面 / 别名）、计数、损坏包容错");
    }
}
