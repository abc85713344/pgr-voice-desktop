using System;
using System.Windows;
using System.Windows.Controls;
using PgrVoice.Listening;

namespace PgrVoice;

public partial class MainWindow
{
    readonly TextBlock listeningRemainingTime = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = Theme.Brush("MutedText"), Margin = new Thickness(0, 3, 0, 2) };
    readonly ListeningRemainingTimeCache listeningTimeCache = new(ReadListeningDuration);

    static long? ReadListeningDuration(string path)
    {
        // Read metadata only: no output device or playback is created.
        using var reader = new NAudio.Wave.AudioFileReader(path);
        return reader.TotalTime > TimeSpan.Zero ? (long)Math.Round(reader.TotalTime.TotalMilliseconds) : null;
    }

    void InitializeListeningRemainingTime()
    {
        listeningTimeCache.Updated += () =>
        {
            if (listeningDisposed || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (!listeningDisposed) RefreshListeningRemainingTime(); }));
        };
    }

    void RefreshListeningRemainingTime()
    {
        var session = listeningSession;
        if (session?.HasBlockingNotice == true) { listeningRemainingTime.Text = "连接尚未确认，等待续接"; return; }
        listeningRemainingTime.Text = session == null ? "" : listeningTimeCache.Estimate(session.Pack,
            session.Items, session.Position, ListeningPositionMs).DisplayText;
    }
}
