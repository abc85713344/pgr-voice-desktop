using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
namespace PgrVoice;
public partial class MainWindow
{
    void LoadArtwork()
    {
        try
        {
            var image = new BitmapImage(); image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/GrayRaven.png");
            image.DecodePixelWidth = 1200; image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit(); image.Freeze(); HeroArtwork.Source = image;
        }
        catch (Exception ex) { Log.Write("artwork", "使用几何背景：" + ex.Message); }
    }
    void ApplyArtwork() => HeroArtwork.Visibility = preferences.ShowArtwork && HeroArtwork.Source != null ? Visibility.Visible : Visibility.Collapsed;
    void ArtworkChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        preferences.ShowArtwork = ArtworkEnabledBox.IsChecked == true; ApplyArtwork(); Save();
    }
    void ArtworkSourceClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://pns.kurogames.com/picture") { UseShellExecute = true }); }
        catch (Exception ex) { Tell("无法打开官方素材页：" + ex.Message); }
    }
    void UpdateChapterNavigation()
    {
        bool multiple = engine?.Pack.Chapters.Count > 1;
        ChapterSelector.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        ChapterColumn.Width = multiple ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
    bool CompactPanelLayout => Height < 680 || Width < 500;
    bool VeryCompactPanelLayout => Height < 560 || (Width < 500 && Height < 680);
    void ApplyCompactLayout()
    {
        bool compact = CompactPanelLayout, veryCompact = VeryCompactPanelLayout;
        Ui.SetTone(Tabs, Width < 500 ? "compact" : "off");
        gamepadPanelHint.TextWrapping = Width < 500 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        gamepadPanelHint.TextTrimming = TextTrimming.CharacterEllipsis;
        gamepadPanelHint.ToolTip = gamepadPanelHint.Text;
        Hero.Height = veryCompact ? 44 : compact ? 84 : 140;
        HeroTitleStack.Margin = new Thickness(18, veryCompact ? 8 : compact ? 9 : 16, 0, 0);
        HeroEyebrow.Visibility = veryCompact ? Visibility.Collapsed : Visibility.Visible;
        HeroHeading.Margin = new Thickness(0, veryCompact ? 0 : 8, 0, 0);
        HeroTitle.FontSize = veryCompact ? 21 : 25;
        CurrentLineScroll.MaxHeight = veryCompact ? 24 : compact ? 54 : 64;
        CurrentCard.Padding = new Thickness(12, veryCompact ? 6 : 9, 12, veryCompact ? 6 : 9);
        CurrentCard.Margin = new Thickness(16, veryCompact ? 0 : compact ? 6 : 10, 16, veryCompact ? 5 : 9);
        SearchBox.Height = veryCompact ? 32 : 34;
        SearchBox.Padding = new Thickness(40, veryCompact ? 5 : 7, 9, veryCompact ? 5 : 7);
        PlaybackBar.Padding = new Thickness(16, veryCompact ? 6 : 10, 16, veryCompact ? 6 : 9);
        ConfirmPlayButton.Padding = new Thickness(12, 0, 12, 0); ConfirmPlayButton.Height = veryCompact ? 32 : 36;
        ConfirmPlayButton.Margin = new Thickness(0, 0, 0, veryCompact ? 5 : 8);
        HeaderChapter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FontSize = veryCompact ? 12 : 13;
        NavigationToolbar.Margin=new Thickness(0,0,0,veryCompact?2:5);
        if(toolbarStrip!=null)toolbarStrip.Wrap=!compact;
        foreach(var child in NavigationToolbar.Children)
        {
            if(child is System.Windows.Controls.Button button){button.Padding=new Thickness(veryCompact?5:7,veryCompact?2:5,veryCompact?5:7,veryCompact?2:5);button.MinHeight=veryCompact?22:28;Ui.SetTone(button,compact?"compact":"off");}
            else if(child is FrameworkElement groupMark && groupMark.Tag is string)groupMark.Visibility=veryCompact?Visibility.Collapsed:Visibility.Visible;
            else if(child is System.Windows.Controls.CheckBox chip)chip.MinHeight=veryCompact?22:28;
        }
        // 播放轨道：紧凑高度只留菱形节点（44），次级开关变矮；极紧凑时省去名牌下的分隔线。
        double track=compact?44:66;
        PlaybackTrack.Height=track;ManualPlaybackButtons.TrackHeight=track;ManualPlaybackButtons.Margin=new Thickness(0,-track,0,0);
        foreach(var child in ManualPlaybackButtons.Children)
            if(child is System.Windows.Controls.Button deckButton)
            {
                if(deckButton.Style==Theme.Style("NodeButton"))Ui.SetTone(deckButton,compact?"compact":"off");
                else{deckButton.Padding=new Thickness(compact?7:9,compact?2:4,compact?7:9,compact?2:4);deckButton.MinHeight=compact?24:28;}
            }
        SpeakerRule.Visibility=veryCompact?Visibility.Collapsed:Visibility.Visible;
        volumeRowTop=veryCompact?3:compact?5:8;AlignVolumeRow();
        ApplyManualContinuationLayout();
        ApplyCharacterArtLayout();
    }
}
