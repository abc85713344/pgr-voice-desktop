using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PgrVoice;

public sealed partial class BranchMenuWindow
{
    PlaybackEngine? presentedEngine;
    BranchAnchorOffer? anchorOffer;
    StackPanel? optionFooter;
    WrapPanel? anchorFooter;
    ScrollViewer? anchorDetails;
    readonly TextBlock anchorFullText=new(){TextWrapping=TextWrapping.Wrap,Foreground=Theme.Brush("Fg"),Margin=new Thickness(5)};
    readonly TextBlock anchorPageText=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
    Button? anchorConfirm,anchorPrevious,anchorNext;
    ItemsPanelTemplate? optionItemsPanel;
    int anchorPage;
    double normalLeft,normalTop;
    bool anchorDragged;
    public bool IsAnchorView {get;private set;}
    public BranchAnchorCard? SelectedAnchor => IsAnchorView ? (Options.SelectedItem as ListBoxItem)?.Tag as BranchAnchorCard : null;
    public BranchAnchorOffer? AnchorOffer => anchorOffer;
    public int AnchorPage => anchorPage;

    void InitializeAnchorView(Grid panel,StackPanel footer,StackPanel header)
    {
        optionFooter=footer;optionItemsPanel=Options.ItemsPanel;
        var switchButton=new Button{Content="按下一句正文确认",Focusable=false,Padding=new Thickness(8,6,8,6),Margin=new Thickness(0,6,0,0)};
        switchButton.Click+=(_,_)=>{if(presentedEngine!=null && !TryPresentAnchors(presentedEngine))notice.Text="没有可直接确认的已核首句；请保留原选项，或定位游戏当前正文。";};
        footer.Children.Insert(1,switchButton);
        anchorFooter=new WrapPanel{Visibility=Visibility.Collapsed};Grid.SetRow(anchorFooter,2);panel.Children.Add(anchorFooter);
        Button Add(string text,Action action)
        {
            var button=new Button{Content=text,Focusable=false,Padding=new Thickness(8,5,8,5),Margin=new Thickness(3,5,3,0)};
            button.Click+=(_,_)=>action();anchorFooter.Children.Add(button);return button;
        }
        anchorPrevious=Add("上一页",()=>ChangeAnchorPage(-1));anchorFooter.Children.Add(anchorPageText);anchorNext=Add("下一页",()=>ChangeAnchorPage(1));
        anchorConfirm=Add("游戏正显示这句 · 确认",()=>{if(CanConfirm)Confirm?.Invoke();});
        Add("展开 / 收起完整句",()=>{if(anchorDetails!=null)anchorDetails.Visibility=anchorDetails.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;});
        Add("原选项菜单",()=>{if(presentedEngine!=null)Present(presentedEngine,normalLeft,normalTop,0);});
        Add("定位当前画面",()=>Locate?.Invoke());Add("全部台词",()=>Manual?.Invoke());Add("收起",()=>Cancel?.Invoke());
        anchorDetails=new ScrollViewer{Content=anchorFullText,MaxHeight=85,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};
        header.Children.Add(anchorDetails);
        badge.MouseLeftButtonDown+=(_,e)=>
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
        var offer=BranchAnchorPolicy.Create(engine,2);
        if(offer.Cards.Count==0)return false;
        bool same=IsAnchorView && IsVisible && MenuId==offer.MenuId;
        if(!same){anchorPage=0;anchorDragged=false;}
        presentedEngine=engine;anchorOffer=offer;MenuId=offer.MenuId;IsNavigation=false;IsAllMenus=false;IsAnchorView=true;Epoch++;
        optionFooter!.Visibility=Visibility.Collapsed;anchorFooter!.Visibility=Visibility.Visible;anchorDetails!.Visibility=Visibility.Collapsed;
        scopeButton.Visibility=Visibility.Collapsed;gamepadHint.Visibility=Visibility.Collapsed;
        title.Text="游戏选完后，现在出现的是哪句？";
        badge.Text="◆ 首句确认 · 可拖动此处 · 只在手动确认时使用";
        var area=SystemParameters.WorkArea;Width=Math.Min(1060,area.Width-24);MaxHeight=Math.Min(440,area.Height-24);
        var grid=new FrameworkElementFactory(typeof(UniformGrid));grid.SetValue(UniformGrid.ColumnsProperty,2);grid.SetValue(UniformGrid.RowsProperty,2);
        Options.ItemsPanel=new ItemsPanelTemplate(grid);Options.MaxHeight=204;
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
        foreach(var card in anchorOffer.Cards.Skip(anchorPage*4).Take(4))
        {
            var body=new StackPanel{Margin=new Thickness(4)};
            body.Children.Add(new TextBlock{Text=card.Speaker+"  ·  "+card.OptionLabel+" · 开头第 "+(anchorOffer.Cards.Where(c=>c.OptionId==card.OptionId).ToList().FindIndex(c=>c.Id==card.Id)+1)+" 句"+(card.IsAmbiguous?" 〔同文，需继续核对〕":""),FontSize=11,Foreground=LineRow.BranchAccent,TextTrimming=TextTrimming.CharacterEllipsis});
            body.Children.Add(new TextBlock{Text=card.Text,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=58,FontSize=14,Margin=new Thickness(0,5,0,0)});
            Options.Items.Add(new ListBoxItem{Tag=card,Content=body,Height=98,Focusable=false,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(2),ToolTip=card.Speaker+"："+card.Text});
        }
        notice.Text="先在游戏中选择；画面与哪句一致就点哪句，先播该句再继续原跟随。未核实入口请用原选项或全部台词。";
        Options.SelectedIndex=Options.Items.Count>0?0:-1;
        anchorPageText.Text=$"{anchorPage+1} / {pages}";anchorPrevious!.IsEnabled=anchorPage>0;anchorNext!.IsEnabled=anchorPage+1<pages;
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
        if(optionFooter==null)return;
        optionFooter.Visibility=Visibility.Visible;anchorFooter!.Visibility=Visibility.Collapsed;anchorDetails!.Visibility=Visibility.Collapsed;
        if(optionItemsPanel!=null)Options.ItemsPanel=optionItemsPanel;
        Options.MaxHeight=300;Width=430;
    }
}
