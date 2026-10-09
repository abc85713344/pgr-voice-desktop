using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PgrVoice;

public partial class MainWindow
{
    const string ListeningWaitText = "等待续接；本段连接尚未确认。可在下方查看本节全部台词，选中未接入的正文后仅试听这一句。";

    void ShowListeningWait()
    {
        // 保留阻断项目与已选路线；“下一句”不能把待核实当作章末或跨到下一节。
        ++listeningRequest; listeningRunning = false; listeningTicket = 0;
        listeningPreviewTicket = 0; listeningPreviewNode = null; listeningStarting = false;
        QueueListeningStop(listeningRequest);
        listeningMessage = ListeningWaitText;
        SaveListeningProgress(); RefreshListening();
    }

    void PreviewListeningLine(string nodeId)
    {
        var session = listeningSession;
        if (session == null || !session.Pack.ById.TryGetValue(nodeId, out var node) || node.Archived || node.Kind != "line") return;
        // 暂停只保存原位置；试听不 Seek/Choose，不改历史或书签，不消费分支。
        if (!PauseListening()) return;
        ActivateListening();
        string notice = session.Pack.AudioNotice(node);
        string? file = session.Pack.ResolveAudio(node);
        if (notice.Length > 0 || file == null || !File.Exists(file))
        {
            listeningMessage = "本句无法试听：" + (notice.Length > 0 ? notice : "音频文件不存在") + "。原收听位置保持不变。";
            RefreshListeningStatus(); return;
        }
        long request = ++listeningRequest;
        listeningPreviewTicket = request; listeningPreviewNode = node;
        listeningMessage = "仅试听这一句 · " + node.Speaker + " · 不续播，不改变收听路线。";
        SetListeningVolume((float)preferences.Volume / 100); RefreshListening();
        if (listeningTestAudio) return;
        string device = preferences.OutputDeviceId;
        _ = Task.Run(async () =>
        {
            await listeningAudioQueue.WaitAsync();
            string? error = null;
            try
            {
                if (request != Volatile.Read(ref listeningRequest) || listeningDisposed) return;
                listeningAudio.SelectDevice(device);
                listeningAudio.Play(file, request, 0, () => request == Volatile.Read(ref listeningRequest) && !listeningDisposed);
                if (request != Volatile.Read(ref listeningRequest)) listeningAudio.Stop();
            }
            catch (Exception ex) { error = ex.Message; }
            finally { listeningAudioQueue.Release(); }
            if (error != null) _ = Dispatcher.BeginInvoke(() => FinishListeningPreview(request, "本句试听失败：" + error));
        });
    }

    void FinishListeningPreview(long request, string message)
    {
        if (request == 0 || request != listeningPreviewTicket || request != listeningRequest || listeningDisposed) return;
        listeningPreviewTicket = 0; listeningPreviewNode = null;
        listeningMessage = message;
        RefreshListening();
    }
}
