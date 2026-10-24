using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlockDock;

public partial class MainWindow
{
    readonly Dictionary<string,FrameworkElement> settingsSections=new();
    readonly Dictionary<string,Button> settingsNavigation=new();
    static FrameworkElement SearchField(TextBox input,string prompt)
    {
        var grid=new Grid{Width=double.IsNaN(input.Width)?230:input.Width,Margin=new(0,0,8,0)};
        input.Width=double.NaN;input.Padding=new(34,8,10,8);input.ToolTip=prompt;
        var icon=new TextBlock{Text="\uE721",FontFamily=new("Segoe MDL2 Assets"),FontSize=14,Foreground=Theme.Muted,Margin=new(12,0,0,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
        var hint=new TextBlock{Text=prompt,Foreground=Theme.Muted,FontSize=12,Margin=new(34,0,0,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
        input.TextChanged+=(_,_)=>hint.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        hint.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;grid.Children.Add(input);grid.Children.Add(icon);grid.Children.Add(hint);return grid;
    }
    Button Shortcut(string name,string description,string glyph,Action action)
    {
        var b=Button("",(_,_)=>action());b.HorizontalContentAlignment=HorizontalAlignment.Stretch;b.Padding=new(15);b.Margin=new(0,0,0,10);
        var grid=Cols(new(34),new(1,GridUnitType.Star),new(18));
        Row(grid,new TextBlock{Text=glyph,FontFamily=new("Segoe MDL2 Assets"),FontSize=20,Foreground=Theme.Accent,VerticalAlignment=VerticalAlignment.Center},0);
        Row(grid,V(Text(name,14,true),new TextBlock{Text=description,Foreground=Theme.Muted,FontSize=11,Margin=new(0,4,0,0),TextWrapping=TextWrapping.Wrap}),0,1);
        Row(grid,new TextBlock{Text="›",FontSize=22,Foreground=Theme.Muted,VerticalAlignment=VerticalAlignment.Center},0,2);b.Content=grid;return b;
    }
    void BuildHomeDashboard()
    {
        var selected=Cols(new(100),new(1,GridUnitType.Star));selectedCover.Width=selectedCover.Height=80;
        Row(selected,new Border{Child=selectedCover,Background=Theme.Raised,CornerRadius=new(16),Padding=new(8),Margin=new(0,0,14,0)},0);
        selectedSummary.FontSize=24;selectedSummary.FontWeight=FontWeights.SemiBold;selectedSummary.TextTrimming=TextTrimming.CharacterEllipsis;selectedSummary.SetBinding(ToolTipProperty,new System.Windows.Data.Binding("Text"){Source=selectedSummary});gameInfo.Foreground=Theme.Muted;gameInfo.TextWrapping=TextWrapping.Wrap;
        Row(selected,V(selectedSummary,Gap(5),gameInfo,Gap(8),selectedFacts),0,1);
        var current=Card(V(new TextBlock{Text="当前游戏",Foreground=Theme.Accent,FontSize=12,FontWeight=FontWeights.SemiBold},Gap(16),selected,Gap(22),H(Button("选择版本",(_,_)=>Navigate(editionBox.SelectedIndex==1?"bedrock-local":"games")),Button("管理内容",(_,_)=>{if(editionBox.SelectedIndex==1){if(homeBedrock.SelectedItem is BedrockPackage package)OpenBedrockData(package);}else{installedGame.SelectedItem=homeGame.SelectedItem;Navigate("installed");}}),Button("地图与存档",(_,_)=>OpenWorlds(editionBox.SelectedIndex==0?homeGame.SelectedItem as GameInstance:null,editionBox.SelectedIndex==1?homeBedrock.SelectedItem as BedrockPackage:null)))));
        var resources=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
        foreach(var item in new[]{("模组","为游戏添加新玩法","\uEA86","mod"),("整合包","开始一段完整的冒险","\uE7B8","modpack"),("材质包","换一种方块风格","\uE8B9","resourcepack"),("光影","发现更好看的世界","\uE706","shader")})
        {string type=item.Item4;var b=Shortcut(item.Item1,item.Item2,item.Item3,()=>ShowDownload(type));b.Margin=new(0,0,10,10);resources.Children.Add(b);}
        var library=V(Text("发现内容",17,true),Gap(12),resources);library.Margin=new(2,22,0,0);
        var side=V(Text("快速访问",14,true),Gap(12),Shortcut("Java 版","版本与加载器","\uE8A5",()=>Navigate("games")),Shortcut("基岩版","独立版本与存档","\uE809",()=>Navigate("bedrock-local")),Shortcut("下载任务","查看速度与进度","\uE896",()=>Navigate("tasks")),Gap(10),Text("本机游戏",12,true),Gap(5),homeSummary);
        homeSummary.Foreground=Theme.Muted;homeSummary.TextWrapping=TextWrapping.Wrap;homeSummary.LineHeight=22;var right=Card(side);right.VerticalAlignment=VerticalAlignment.Top;
        var grid=Cols(new(1,GridUnitType.Star),new(20),new(260));Row(grid,V(current,library),0);Row(grid,right,0,2);Page("home",Scroll(grid));
    }
    void BuildSettingsDashboard()
    {
        rootBox.Text=settings.JavaRoot;javaBox.Text=settings.JavaPath;
        var startup=Card(V(Text("游戏启动",20,true),Hint("选择游戏目录、Java 环境和内存分配。"),Gap(20),Text("游戏文件夹",13,true),Gap(6),rootBox,Gap(8),Button("选择文件夹",(_,_)=>{var d=new Microsoft.Win32.OpenFolderDialog{Title="选择 .minecraft 文件夹"};if(d.ShowDialog()==true)rootBox.Text=d.FolderName;}),Gap(20),BuildMemoryConfiguration(),Gap(20),BuildJavaConfiguration(),Gap(22),Button("保存启动设置",(_,_)=>SaveSettings(),true)));
        var storage=Card(V(Text("备份与记录",20,true),Hint("查找备份文件与启动器运行记录。"),Gap(20),H(Button("打开备份目录",(_,_)=>OpenBackupFolder()),Button("打开记录目录",(_,_)=>Open(Settings.DataPath)))));
        var entries=new (string Id,string Name,string Glyph,UIElement Content)[]{("launch","游戏启动","\uE768",startup),("source","下载源","\uE774",BuildSourceConfiguration()),("appearance","外观与壁纸","\uE790",BuildWallpaperSettings()),("updates","启动器更新","\uE895",BuildUpdateConfiguration()),("storage","备份与记录","\uE8B7",storage)};
        var menu=new StackPanel();var deck=new Grid();
        foreach(var entry in entries)
        {
            string id=entry.Id;var content=Scroll(entry.Content);content.Visibility=Visibility.Collapsed;settingsSections[id]=content;deck.Children.Add(content);
            var b=Button("",(_,_)=>ShowSettingsSection(id));b.SetResourceReference(StyleProperty,"Nav");b.Margin=new(0,0,0,6);b.Padding=new(12,10,6,10);
            b.Content=H(new TextBlock{Text=entry.Glyph,FontFamily=new("Segoe MDL2 Assets"),Margin=new(0,0,10,0),VerticalAlignment=VerticalAlignment.Center},Text(entry.Name,12));settingsNavigation[id]=b;menu.Children.Add(b);
        }
        var layout=Cols(new(156),new(20),new(1,GridUnitType.Star));Row(layout,menu,0);Row(layout,deck,0,2);Page("settings",layout);ShowSettingsSection("launch");
    }
    void ShowSettingsSection(string id)
    {
        foreach(var entry in settingsSections)entry.Value.Visibility=entry.Key==id?Visibility.Visible:Visibility.Collapsed;
        foreach(var entry in settingsNavigation){entry.Value.Background=entry.Key==id?Theme.Brush("#183C33"):Brushes.Transparent;entry.Value.Foreground=entry.Key==id?Theme.Accent:Theme.Muted;}
    }
}
