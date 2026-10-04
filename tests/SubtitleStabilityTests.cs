using PgrVoice;
using PgrVoice.Following;

public static class SubtitleStabilityTests
{
    public static void Run()
    {
        int tests = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("字幕稳定门控：" + message); }
        void Test(string name, Action action) { action(); tests++; Console.WriteLine("PASS SUBTITLE STABILITY " + name); }
        byte[] Mask(int pixels = 384 * 96, int ink = 300)
        { var mask = new byte[pixels]; Array.Fill(mask, (byte)1, 0, ink); return mask; }

        Test("首帧变化后连续两帧稳定才可 OCR", () =>
        {
            var gate = new SubtitleStability(); var mask = Mask();
            var first = gate.Observe(mask); var second = gate.Observe(mask); var third = gate.Observe(mask);
            Check(first.Changed && !first.Stable && !second.Changed && !second.Stable && !third.Changed && third.Stable, "提前判稳");
            Check(first.FrameKey == third.FrameKey, "相同字幕不断变更编号");
        });
        Test("1到3像素采样噪声不反复改变画面编号", () =>
        {
            var gate = new SubtitleStability(); var original = Mask(); string key = gate.Observe(original).FrameKey;
            for (int frame = 0; frame < 120; frame++)
            {
                var noisy = (byte[])original.Clone();
                for (int dot = 0; dot < 1 + frame % 3; dot++) noisy[1000 + frame * 3 + dot] = 1;
                var result = gate.Observe(noisy);
                Check(!result.Changed && result.FrameKey == key && (frame == 0 || result.Stable), "少量抖动不断触发重识别");
            }
        });
        Test("三个噪点移位的六像素差异也保持稳定", () =>
        {
            var gate = new SubtitleStability(); var initial = Mask(); initial[1000] = initial[1001] = initial[1002] = 1;
            string key = gate.Observe(initial).FrameKey;
            var moved = Mask(); moved[2000] = moved[2001] = moved[2002] = 1;
            Check(!gate.Observe(moved).Changed && gate.Observe(moved).FrameKey == key, "噪点移动误作换句");
        });
        Test("单字24像素变化不被全图或大片白色稀释", () =>
        {
            var gate = new SubtitleStability(); var initial = Mask(384 * 96, 12000); string key = gate.Observe(initial).FrameKey;
            var changed = (byte[])initial.Clone();
            Array.Fill(changed, (byte)0, 200, 12); Array.Fill(changed, (byte)1, 13000, 12);
            var result = gate.Observe(changed);
            Check(result.Changed && !result.Stable && result.FrameKey != key, "24像素单字变化被漏掉");
            Check(!gate.Observe(changed).Stable && gate.Observe(changed).Stable, "改字后未重新等待稳定");
        });
        Test("小段字幕按白字并集比例发现8像素改字", () =>
        {
            var gate = new SubtitleStability(); var initial = Mask(384 * 96, 64); gate.Observe(initial);
            var changed = (byte[])initial.Clone(); Array.Fill(changed, (byte)0, 0, 8);
            Check(gate.Observe(changed).Changed, "只使用绝对24像素门槛漏掉小字幕变化");
        });
        Test("逐字显示每次增加一字都重新等待两帧", () =>
        {
            var gate = new SubtitleStability(); string? prior = null;
            for (int glyph = 1; glyph <= 10; glyph++)
            {
                var partial = Mask(384 * 96, 24 * glyph); var result = gate.Observe(partial);
                Check(result.Changed && !result.Stable && result.FrameKey != prior, "逐字显示被误判完整");
                Check(!gate.Observe(partial).Stable, "只有一帧稳定就运行 OCR"); prior = result.FrameKey;
            }
            Check(gate.Observe(Mask(384 * 96, 240)).Stable, "显示完整后不能稳定");
        });
        Test("空字幕不触发稳定 OCR，文字消失再出现可区分", () =>
        {
            var gate = new SubtitleStability(); var empty = Mask(384 * 96, 0);
            for (int i = 0; i < 5; i++) Check(!gate.Observe(empty).Stable, "空字幕被当作 OCR 目标");
            var text = Mask(); string before = gate.Observe(text).FrameKey;
            gate.Observe(text); Check(gate.Observe(text).Stable, "非空字幕未稳定");
            var removed = gate.Observe(empty); Check(removed.Changed && !removed.Stable, "字幕消失没有中断跟随");
            var repeated = gate.Observe(text); Check(repeated.Changed && repeated.FrameKey != before && !repeated.Stable, "消失后重现没有新画面编号");
        });
        Test("完全同文同画面始终保留同一编号", () =>
        {
            var gate = new SubtitleStability(); var mask = Mask(); string key = gate.Observe(mask).FrameKey;
            for (int i = 0; i < 1000; i++)
            { var result = gate.Observe(mask); Check(!result.Changed && result.FrameKey == key, "同画面重播依据凭空出现"); }
        });
        Test("尺寸改变和 Reset 后编号保持单调且重新稳定", () =>
        {
            var gate = new SubtitleStability(); string first = gate.Observe(Mask()).FrameKey; long revision = gate.Revision;
            var resized = gate.Observe(Mask(192 * 48, 100));
            Check(resized.Changed && !resized.Stable && gate.Revision > revision && resized.FrameKey != first, "尺寸变化没有失效");
            revision = gate.Revision; gate.Reset(); var reset = gate.Observe(Mask(192 * 48, 100));
            Check(reset.Changed && !reset.Stable && gate.Revision > revision && reset.FrameKey != resized.FrameKey, "Reset复用旧编号");
        });
        Test("保存自己的基准缓冲且稳定帧不分配新数组", () =>
        {
            var gate = new SubtitleStability(); var reused = Mask(); gate.Observe(reused);
            Array.Fill(reused, (byte)1, 400, 24); Check(gate.Observe(reused).Changed, "借用输入数组被调用方覆盖");
            for (int i = 0; i < 100; i++) gate.Observe(reused);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) gate.Observe(reused);
            Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "稳定截图产生逐帧托管分配");
        });
        Console.WriteLine($"SUBTITLE STABILITY: {tests} synthetic groups passed; no real background-noise or device validation");
    }
}
