using System;
using System.Diagnostics;
using System.Windows;
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
    void ApplyCompactLayout()
    {
        bool compact = Height < 680, veryCompact = Height < 560;
        Hero.Height = veryCompact ? 40 : compact ? 80 : 116;
        HeroTitleStack.Margin = new Thickness(18, veryCompact ? 8 : compact ? 9 : 16, 0, 0);
        HeroEyebrow.Visibility = veryCompact ? Visibility.Collapsed : Visibility.Visible;
        HeroHeading.Margin = new Thickness(0, veryCompact ? 0 : 8, 0, 0);
        HeroTitle.FontSize = veryCompact ? 21 : 25;
        CurrentLineScroll.MaxHeight = veryCompact ? 24 : compact ? 54 : 84;
        CurrentCard.Padding = new Thickness(12, veryCompact ? 6 : 9, 12, veryCompact ? 6 : 9);
        CurrentCard.Margin = new Thickness(16, 0, 16, veryCompact ? 5 : 9);
        SearchBox.Height = veryCompact ? 32 : 34;
        SearchBox.Padding = new Thickness(30, veryCompact ? 5 : 7, 9, veryCompact ? 5 : 7);
        PlaybackBar.Padding = new Thickness(16, veryCompact ? 6 : 10, 16, veryCompact ? 6 : 9);
        ConfirmPlayButton.Padding = new Thickness(12, veryCompact ? 6 : 9, 12, veryCompact ? 6 : 9);
        ConfirmPlayButton.Margin = new Thickness(0, 0, 0, veryCompact ? 5 : 8);
        HeaderChapter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FontSize = veryCompact ? 12 : 13;
        NavigationToolbar.Margin=new Thickness(0,0,0,veryCompact?2:5);
        foreach(var child in NavigationToolbar.Children)if(child is System.Windows.Controls.Button button){button.Padding=new Thickness(veryCompact?5:8,veryCompact?2:5,veryCompact?5:8,veryCompact?2:5);button.MinHeight=veryCompact?22:28;}
    }
}
