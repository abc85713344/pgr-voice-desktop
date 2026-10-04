using System;
using System.Linq;
using PgrVoice;

public static class DialogueFrameTests
{
    public static void Run()
    {
        int checkedCases = 0;
        void Check(DialogueFrameVerdict expected, string name, DialogueFrameMask before, params DialogueFrameMask[] after)
        {
            var decision = DialogueFrameAnalysis.Analyze(before, after);
            if (decision.Verdict != expected)
                throw new Exception($"白字判断 {name}：期望 {expected}，得到 {decision.Verdict}；{decision.Reason}");
            checkedCases++;
        }
        var original = Mask(0, (0, 200));
        Check(DialogueFrameVerdict.NoChange, "多帧没有变化", original, Mask(100, (0, 200)), Mask(240, (0, 200)));
        Check(DialogueFrameVerdict.NoChange, "少量抗锯齿噪声", original, Mask(100, (1, 199)), Mask(240, (0, 204)));
        Check(DialogueFrameVerdict.Typing, "旧字保留并增加", original, Mask(100, (0, 260)), Mask(240, (0, 340)));
        Check(DialogueFrameVerdict.Typing, "补字后稳定也不当作换句", original, Mask(100, (0, 300)), Mask(240, (0, 300)));
        Check(DialogueFrameVerdict.Typing, "早期未变后补字", original, Mask(60, (0, 200)), Mask(180, (0, 300)), Mask(350, (0, 300)));
        Check(DialogueFrameVerdict.Advanced, "旧字替换且末帧一致", original, Mask(100, (400, 200)), Mask(240, (400, 200)));
        Check(DialogueFrameVerdict.Advanced, "新字增加后支持替换但不宣称完整", original,
            Mask(80, (400, 120)), Mask(220, (400, 200)), Mask(360, (400, 200)));
        Check(DialogueFrameVerdict.Advanced, "先保持旧帧再替换", original,
            Mask(70, (0, 200)), Mask(180, (400, 200)), Mask(350, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "仅单帧", original, Mask(100, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "末帧间隔过短", original, Mask(100, (400, 200)), Mask(110, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "相同采样时间", original, Mask(100, (400, 200)), Mask(100, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "空白前帧", Mask(0), Mask(100, (400, 200)), Mask(240, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "低字量前帧", Mask(0, (0, 20)), Mask(100, (400, 200)), Mask(240, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "全白前帧", Mask(0, (0, 10000)), Mask(100, (400, 200)), Mask(240, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "空白过渡", original, Mask(80), Mask(180, (400, 200)), Mask(350, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "全白闪屏", original, Mask(80, (0, 10000)), Mask(180, (400, 200)), Mask(350, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "新字过少", original, Mask(100, (400, 20)), Mask(240, (400, 20)));
        Check(DialogueFrameVerdict.Uncertain, "类似句无法确认", original, Mask(100, (0, 150), (400, 50)), Mask(240, (0, 150), (400, 50)));
        Check(DialogueFrameVerdict.Uncertain, "仅旧字消失不是换句", original, Mask(100, (0, 90)), Mask(240, (0, 90)));
        Check(DialogueFrameVerdict.Uncertain, "先补字再换句", original,
            Mask(80, (0, 300)), Mask(180, (400, 200)), Mask(350, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "先换句再补回旧字", original,
            Mask(80, (400, 200)), Mask(180, (0, 300)), Mask(350, (0, 300)));
        Check(DialogueFrameVerdict.Uncertain, "替换后又回到旧帧", original,
            Mask(80, (400, 200)), Mask(180, (0, 200)), Mask(350, (0, 200)));
        Check(DialogueFrameVerdict.Uncertain, "只最后一帧支持替换", original, Mask(100, (0, 200)), Mask(240, (400, 200)));
        Check(DialogueFrameVerdict.Uncertain, "连续两次替换", original,
            Mask(80, (400, 200)), Mask(180, (800, 200)), Mask(350, (800, 200)));
        Check(DialogueFrameVerdict.Uncertain, "新字仍大幅增加", original, Mask(100, (400, 100)), Mask(240, (400, 220)));
        Check(DialogueFrameVerdict.Uncertain, "补字后大量回退", original, Mask(100, (0, 400)), Mask(240, (0, 240)));
        Check(DialogueFrameVerdict.Uncertain, "区域尺寸改变", original,
            new(200, 50, 100, Mask(100, (400, 200)).Pixels), new(200, 50, 240, Mask(240, (400, 200)).Pixels));
        Check(DialogueFrameVerdict.Uncertain, "坏缓冲区", original,
            new(100, 100, 100, new byte[3]), Mask(240, (400, 200)));

        var bgr = new byte[] { 170, 170, 170, 169, 255, 255, 170, 215, 170, 170, 216, 170, 99, 99, 99, 99 };
        var white = DialogueFrameAnalysis.CreateWhiteMask(4, 1, 16, bgr);
        if (!white.Pixels.SequenceEqual(new byte[] { 1, 0, 1, 0 })) throw new Exception("BGR白字阈值或行填充处理错误。");
        var bgra = DialogueFrameAnalysis.CreateWhiteMask(2, 1, 8, new byte[] { 255, 255, 255, 0, 20, 20, 20, 255 }, bytesPerPixel: 4);
        if (!bgra.Pixels.SequenceEqual(new byte[] { 1, 0 })) throw new Exception("BGRA处理或Alpha隔离错误。");
        bool rejected = false;
        try { DialogueFrameAnalysis.CreateWhiteMask(2, 1, 5, new byte[6]); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new Exception("无效图像步长未拒绝。");
        Console.WriteLine($"PASS DIALOGUE FRAMES: {checkedCases} synthetic sequences plus BGR/BGRA conversion; no OCR or platform dependency");
    }

    static DialogueFrameMask Mask(double elapsed, params (int Start, int Count)[] ranges)
    {
        var pixels = new byte[10000];
        foreach (var range in ranges) Array.Fill(pixels, (byte)1, range.Start, range.Count);
        return new(100, 100, elapsed, pixels);
    }
}
