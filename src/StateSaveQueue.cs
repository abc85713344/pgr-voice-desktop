using System;
using System.Threading.Tasks;

namespace PgrVoice;

// UI 先冻结状态；文件读写只在这个顺序队列里发生，避免旧存档覆盖新存档。
public sealed class StateSaveQueue
{
    readonly object sync = new();
    Task tail = Task.CompletedTask;
    Exception? lastFailure;
    public Task Enqueue(Action write, Action<Exception?> completed)
    {
        lock (sync)
        {
            return tail = tail.ContinueWith(_ =>
            {
                Exception? failure = null;
                try { write(); } catch (Exception ex) { failure = ex; }
                lock (sync) lastFailure = failure;
                completed(failure);
            }, default, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
    public Exception? Flush() { Task pending; lock (sync) pending = tail; pending.GetAwaiter().GetResult(); lock (sync) return lastFailure; }
}
