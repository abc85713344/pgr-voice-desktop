using PgrVoice;

public static class StateSaveQueueTests
{
    public static void Run()
    {
        var queue = new StateSaveQueue(); var values = new List<int>();
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        queue.Enqueue(() => { ready.Set(); release.Wait(); values.Add(0); }, _ => { });
        if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new Exception("后台保存未开始");
        for (int i = 1; i <= 20; i++) { int frozen = i; queue.Enqueue(() => values.Add(frozen), _ => { }); }
        if (values.Count != 0) throw new Exception("保存次序失效");
        release.Set();
        if (queue.Flush() != null || !values.SequenceEqual(Enumerable.Range(0, 21))) throw new Exception("新旧状态乱序或退出未等待");
        queue.Enqueue(() => throw new IOException("模拟磁盘拒绝写入"), _ => { });
        if (queue.Flush() is not IOException) throw new Exception("退出检查丢失保存错误");
        queue.Enqueue(() => values.Add(21), _ => { });
        if (queue.Flush() != null || values[^1] != 21) throw new Exception("保存失败后不能重试");
        Console.WriteLine("PASS 后台保存：UI不等写盘、顺序保持、退出等待、错误报告及重试");
    }
}
