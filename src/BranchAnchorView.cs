using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

// 12-B 首句确认：左栏 = 完整句读数条 + 按选项分道的卡片（每道：道签 → 第 1 句 → 黄铜箭头 → 第 2 句）+ 翻页行；
// 右栏 = 262px 竖排 10-A3 指令条。卡片仍是 Options 里的 ListBoxItem（每页 4 张、键盘移动与翻页不变）。
public sealed partial class BranchMenuWindow
{
    PlaybackEngine? presentedEngine;
    BranchAnchorOffer? anchorOffer;
    StackPanel? anchorFooter;
    Grid? anchorPaging;
    ScrollViewer? anchorDetails;
    readonly TextBlock anchorFullText=new(){TextWrapping=TextWrapping.Wrap,Foreground=Hud.Solid("#E9EDF1"),Margin=new Thickness(0,1,0,1)};
    readonly TextBlock anchorPageText=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,6,0),Style=Theme.Style("NumberText"),Foreground=Theme.Brush("Slate")};
    readonly StackPanel anchorDots=new(){Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
    Button? anchorConfirm,anchorPrevious,anchorNext;
    ItemsPanelTemplate? optionItemsPanel;
    int anchorPage;
    double normalLeft,normalTop;
    bool anchorDragged;
    public bool IsAnchorView {get;private set;}
    public BranchAnchorCard? SelectedAnchor => IsAnchorView ? (Options.SelectedItem as ListBoxItem)?.Tag as BranchAnchorCard : null;
    public BranchAnchorOffer? AnchorOffer => anchorOffer;
    public int AnchorPage => anchorPage;
    /// <summary>说话人头像（由主窗口提供本机图片缓存）；Show 为假时不显示头像列。</summary>
    public Func<string,(bool Show,System.Windows.Media.ImageSource? Image)>? Portraits {get;set;}

    void InitializeAnchorView(StackPanel header)
    {
        optionItemsPanel=Options.ItemsPanel;
        // 右侧指令条
        anchorFooter=new StackPanel{Width=262,Margin=new Thickness(12,0,0,0),Visibility=Visibility.Collapsed};
        Grid.SetColumn(anchorFooter,1);Grid.SetRow(anchorFooter,1);Grid.SetRowSpan(anchorFooter,3);panel.Children.Add(anchorFooter);
        Button Add(string text,string sub,Geometry icon,Action action,string kind="")
        {
            var button=ActionButton.Apply(new Button{Content=text,Focusable=false,Margin=new Thickness(0,0,0,6)},sub,icon,kind);
            button.Click+=(_,_)=>action();anchorFooter.Children.Add(button);return button;
        }
        anchorConfirm=Add("游戏正显示这句 · 确认","CONFIRM",ActionIcons.Confirm,()=>{if(CanConfirm)Confirm?.Invoke();},"go");anchorConfirm.MinHeight=36;
        Add("展开 / 收起完整句","FULL LINE",ActionIcons.Expand,()=>{if(anchorDetails!=null)anchorDetails.Visibility=anchorDetails.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;});
        Add("原选项菜单","OPTIONS",ActionIcons.Menu,()=>{if(presentedEngine!=null)Present(presentedEngine,normalLeft,normalTop,0);});
        Add("定位当前画面","LOCATE",ActionIcons.Locate,()=>Locate?.Invoke());Add("全部台词","ALL LINES",ActionIcons.Line,()=>Manual?.Invoke());Add("收起","STANDBY",ActionIcons.Collapse,()=>Cancel?.Invoke(),"ghost");
        // 左栏底部翻页行
        anchorPaging=new Grid{Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};Grid.SetRow(anchorPaging,3);panel.Children.Add(anchorPaging);
        var pager=new StackPanel{Orientation=Orientation.Horizontal};anchorPaging.Children.Add(pager);
        Button Page(string text,Geometry icon,Action action)
        {
            var b=ActionButton.Apply(new Button{Content=text,Focusable=false,Margin=new Thickness(0),MinHeight=28,MinWidth=86},"",icon);
            b.Click+=(_,_)=>action();return b;
        }
        anchorPrevious=Page("上一页",ActionIcons.Return,()=>ChangeAnchorPage(-1));anchorNext=Page("下一页",ActionIcons.Confirm,()=>ChangeAnchorPage(1));
        pager.Children.Add(anchorPrevious);pager.Children.Add(anchorPageText);pager.Children.Add(anchorDots);pager.Children.Add(anchorNext);
        anchorPaging.Children.Add(new TextBlock{Text="PAGE · 4 CARDS / PAGE",FontFamily=Theme.Font("LabelFont"),FontSize=8.5,Foreground=Theme.Brush("NormalAccent"),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center});
        // 完整句读数条（原 anchorDetails，显示 / 收起逻辑不变）
        var readout=new DockPanel();
        var readLabel=new TextBlock{Text="完整句 · FULL LINE",FontSize=10,Foreground=Hud.Solid("#D6B268"),Margin=new Thickness(0,1,12,0),VerticalAlignment=VerticalAlignment.Top};
        DockPanel.SetDock(readLabel,Dock.Left);readout.Children.Add(readLabel);readout.Children.Add(anchorFullText);
        anchorDetails=new ScrollViewer{Content=new Border{Padding=new Thickness(10,6,10,6),Child=readout},MaxHeight=85,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed,
            Background=Theme.Brush("Plate"),BorderBrush=Theme.Brush("Brass"),BorderThickness=new Thickness(3,0,0,0),Margin=new Thickness(0,0,0,8)};
        Grid.SetRow(anchorDetails,1);panel.Children.Add(anchorDetails);
        badgeHost.MouseLeftButtonDown+=(_,e)=>
        {
            if(!IsAnchorView || e.ButtonState!=MouseButtonState.Pressed)return;
            e.Handled=true;try{DragMove();anchorDragged=true;}catch(InvalidOperationException){}
        };
        Options.SelectionChanged+=(_,_)=>
        {
            if(!IsAnchorView)return;
            var card=SelectedAnchor;
            anchorFullText.Text=card==null?"":card.OptionLabel+"\n"+card.Speaker+"："+card.Text;
            if(anchorConfirm!=null)anchorConfirm.IsEnabled=card is {IsAmbiguous:false};
            if(card?.IsAmbiguous==true)notice.Text="这句在多条路线中相同，无法单凭首句确定分支；请继续核对或切回原选项菜单。";
        };
    }

    bool TryPresentAnchors(PlaybackEngine engine)
    {
        ResetExplore();
        var offer=BranchAnchorPolicy.Create(engine,2);
        if(offer.Cards.Count==0)return false;
        bool same=IsAnchorView && IsVisible && MenuId==offer.MenuId;
        if(!same){anchorPage=0;anchorDragged=false;}
        presentedEngine=engine;anchorOffer=offer;MenuId=offer.MenuId;IsNavigation=false;IsAllMenus=false;IsAnchorView=true;Epoch++;
        footer.Visibility=Visibility.Collapsed;anchorFooter!.Visibility=Visibility.Visible;anchorPaging!.Visibility=Visibility.Visible;anchorDetails!.Visibility=Visibility.Collapsed;
        scopeButton.Visibility=Visibility.Collapsed;gamepadHint.Visibility=Visibility.Collapsed;summaryBar.Visibility=routeOrbit.Visibility=Visibility.Collapsed;
        SetBadgeTone(false);SetDecor("1ST",true);
        title.Text="游戏选完后，现在出现的是哪句？";
        badge.Text="◆ 首句确认 · 可拖动此处 · 只在手动确认时使用";
        var area=SystemParameters.WorkArea;Width=Math.Min(1060,area.Width-24);MaxHeight=Math.Min(440,area.Height-24);
        var order=offer.Cards.Select(c=>c.OptionId).Distinct().ToList();
        Options.Tag=(Func<string,int>)(id=>order.IndexOf(id)+1);
        Options.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(AnchorLanePanel)));Options.MaxHeight=184;
        Options.ItemContainerStyle=(Style)Application.Current.FindResource("AnchorCardStyle");
        RenderAnchorPage();
        if(!same || !anchorDragged){Left=area.Left+(area.Width-Width)/2;Top=area.Top+12;}
        if(!IsVisible)Show();return true;
    }

    void RenderAnchorPage()
    {
        if(anchorOffer==null)return;
        Epoch++;pendingMouseConfirm=null;
        int pages=Math.Max(1,(anchorOffer.Cards.Count+3)/4);anchorPage=Math.Clamp(anchorPage,0,pages-1);
        Options.Items.Clear();
        int slot=0;
        foreach(var card in anchorOffer.Cards.Skip(anchorPage*4).Take(4))
        {
            int position=anchorOffer.Cards.Where(c=>c.OptionId==card.OptionId).ToList().FindIndex(c=>c.Id==card.Id)+1;
            var portrait=Portraits?.Invoke(card.Speaker);
            var body=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
            body.Children.Add(new TextBlock{Text=card.Speaker+"  ·  开头第 "+position+" 句"+(card.IsAmbiguous?" 〔同文，需继续核对〕":""),FontSize=11,Foreground=LineRow.BranchAccent,TextTrimming=TextTrimming.CharacterEllipsis});
            body.Children.Add(new TextBlock{Text=card.Text,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=40,FontSize=13.5,LineHeight=20,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,Margin=new Thickness(0,4,0,0)});
            var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());
            if(portrait is {Show:true} p)
                row.Children.Add(new SpeakerPortrait{Speaker=card.Speaker,Source=p.Image,Width=40,Height=40,ShowSerial=false,Accent=Theme.Brush("Brass"),Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center});
            Grid.SetColumn(body,1);row.Children.Add(body);
            // 左上角序号签（本页第 n 张）
            var index=new Border{Background=Theme.Brush("Plate"),Padding=new Thickness(4,0,4,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(-8,-6,0,0),IsHitTestVisible=false,
                Child=new TextBlock{Text=(anchorPage*4+(++slot)).ToString(),FontFamily=Theme.Font("NumberFont"),FontSize=9.5,Foreground=Brushes.White}};
            var content=new Grid();content.Children.Add(row);content.Children.Add(index);
            Options.Items.Add(new ListBoxItem{Tag=card,Content=content,Height=84,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip=card.Speaker+"："+card.Text});
        }
        notice.Text="先在游戏中选择；画面与哪句一致就点哪句，先播该句再继续原跟随。未核实入口请用原选项或全部台词。";
        Options.SelectedIndex=Options.Items.Count>0?0:-1;
        anchorPageText.Text=$"{anchorPage+1} / {pages}";anchorPrevious!.IsEnabled=anchorPage>0;anchorNext!.IsEnabled=anchorPage+1<pages;
        anchorDots.Children.Clear();
        for(int i=0;i<pages && pages>1;i++)anchorDots.Children.Add(new Border{Width=i==anchorPage?14:6,Height=3,Margin=new Thickness(0,0,3,0),Background=i==anchorPage?Theme.Brush("Accent"):Theme.Brush("StrokeStrong")});
    }
    public void ChangeAnchorPage(int delta)
    {
        if(!IsAnchorView || anchorOffer==null)return;
        int next=Math.Clamp(anchorPage+delta,0,Math.Max(0,(anchorOffer.Cards.Count-1)/4));
        if(next==anchorPage)return;
        anchorPage=next;anchorDetails!.Visibility=Visibility.Collapsed;RenderAnchorPage();
    }
    void RestoreOptionView()
    {
        IsAnchorView=false;anchorOffer=null;
        if(anchorFooter==null)return;
        footer.Visibility=Visibility.Visible;anchorFooter.Visibility=Visibility.Collapsed;anchorPaging!.Visibility=Visibility.Collapsed;anchorDetails!.Visibility=Visibility.Collapsed;
        if(optionItemsPanel!=null)Options.ItemsPanel=optionItemsPanel;
        Options.Tag=null;Options.MaxHeight=300;Width=430;
    }
}

/// <summary>
/// 首句卡片的分道面板：同一选项的卡片排成一道（道签 96px → 卡 → 黄铜箭头 → 卡）；单卡选项两道并排一行。
/// 只决定位置并画道签 / 箭头，选择、键盘移动与翻页仍由 ListBox 处理。
/// </summary>
public sealed class AnchorLanePanel : Panel
{
    const double TagW = 96, Gap = 8, Arrow = 26, RowGap = 8;
    readonly List<(int Start, int Count, string OptionId, string Label, Rect TagRect)> lanes = new();
    readonly List<(Point From, Point To)> arrows = new();
    static BranchAnchorCard? Card(UIElement e) => (e as ListBoxItem)?.Tag as BranchAnchorCard;
    List<List<(int Start, int Count)>> Rows()
    {
        var groups = new List<(int Start, int Count)>();
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            string id = Card(InternalChildren[i])?.OptionId ?? i.ToString();
            if (groups.Count > 0 && (Card(InternalChildren[groups[^1].Start])?.OptionId ?? "") == id && groups[^1].Count < 2) groups[^1] = (groups[^1].Start, groups[^1].Count + 1);
            else groups.Add((i, 1));
        }
        var rows = new List<List<(int, int)>>();
        foreach (var g in groups)
        {
            if (rows.Count > 0 && rows[^1].Count == 1 && rows[^1][0].Item2 == 1 && g.Count == 1) rows[^1].Add(g);
            else rows.Add(new() { g });
        }
        return rows;
    }
    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 760 : available.Width;
        var rows = Rows(); double h = 0;
        foreach (var row in rows)
        {
            double cardW = CardWidth(w, row), rowH = 0;
            foreach (var (start, count) in row)
                for (int i = start; i < start + count; i++) { InternalChildren[i].Measure(new Size(cardW, double.PositiveInfinity)); rowH = Math.Max(rowH, InternalChildren[i].DesiredSize.Height); }
            h += rowH + RowGap;
        }
        return new Size(w, Math.Max(0, h - RowGap));
    }
    static double CardWidth(double w, List<(int Start, int Count)> row) =>
        row.Count == 2 ? Math.Max(80, (w - 2 * (TagW + Gap) - 12) / 2) : Math.Max(80, (w - TagW - Gap - Arrow) / 2);
    protected override Size ArrangeOverride(Size size)
    {
        lanes.Clear(); arrows.Clear();
        var owner = ItemsControl.GetItemsOwner(this);
        var order = owner?.Tag as Func<string, int>;
        double y = 0;
        foreach (var row in Rows())
        {
            double cardW = CardWidth(size.Width, row), rowH = 0, x = 0;
            foreach (var (start, count) in row) for (int i = start; i < start + count; i++) rowH = Math.Max(rowH, InternalChildren[i].DesiredSize.Height);
            foreach (var (start, count) in row)
            {
                var card = Card(InternalChildren[start]);
                lanes.Add((start, count, card?.OptionId ?? "", card?.OptionLabel ?? "", new Rect(x, y, TagW, rowH)));
                x += TagW + Gap;
                for (int i = start; i < start + count; i++)
                {
                    InternalChildren[i].Arrange(new Rect(x, y, cardW, rowH));
                    x += cardW;
                    if (i < start + count - 1) { arrows.Add((new Point(x + 5, y + rowH / 2), new Point(x + Arrow - 5, y + rowH / 2))); x += Arrow; }
                }
                x += 12;
            }
            y += rowH + RowGap;
        }
        InvalidateVisual();
        return size;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var owner = ItemsControl.GetItemsOwner(this); var order = owner?.Tag as Func<string, int>;
        var mist = Hud.Solid("#6F8FB0"); var fill = Hud.Solid("#B3EEF1F4");
        foreach (var lane in lanes)
        {
            var r = lane.TagRect;
            dc.DrawRectangle(fill, null, r);
            dc.DrawRectangle(mist, null, new Rect(r.X, r.Y, 2, r.Height));
            int n = order?.Invoke(lane.OptionId) ?? 0;
            if (n > 0)
            {
                var num = Hud.Text(this, n.ToString("00"), "NumberFont", 20, Brushes.Black, FontWeights.Bold);
                dc.DrawGeometry(null, Hud.Pen(Hud.Solid("#8FA6BF"), 1), num.BuildGeometry(new Point(r.X + 10, r.Y + 6)));
            }
            var label = Hud.Text(this, lane.Label, "UiFont", 12, Theme.Brush("Fg"));
            label.MaxTextWidth = r.Width - 18; label.MaxLineCount = 2; label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(r.X + 10, r.Y + 32));
            var route = Hud.Text(this, "ROUTE", "LabelFont", 8.5, Theme.Brush("NormalAccent"));
            dc.DrawText(route, new Point(r.X + 10, r.Bottom - route.Height - 5));
        }
        var brass = Theme.Brush("Brass"); var pen = Hud.Pen(brass, 1.2);
        foreach (var (from, to) in arrows)
        {
            dc.DrawLine(pen, from, new Point(to.X - 2, to.Y));
            var head = new StreamGeometry();
            using (var c = head.Open()) { c.BeginFigure(new Point(to.X, to.Y), true, true); c.LineTo(new Point(to.X - 6, to.Y - 4), true, false); c.LineTo(new Point(to.X - 6, to.Y + 4), true, false); }
            head.Freeze(); dc.DrawGeometry(brass, null, head);
        }
    }
}
