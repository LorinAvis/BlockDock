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
    void BuildHomeDashboard()
    {
        var selected=Cols(new(88),new(1,GridUnitType.Star));selectedCover.Width=selectedCover.Height=68;Row(selected,selectedCover,0);
        selectedSummary.FontSize=22;selectedSummary.FontWeight=FontWeights.SemiBold;selectedSummary.TextTrimming=TextTrimming.CharacterEllipsis;selectedSummary.SetBinding(ToolTipProperty,new System.Windows.Data.Binding("Text"){Source=selectedSummary});gameInfo.TextWrapping=TextWrapping.Wrap;
        Row(selected,V(selectedSummary,gameInfo,Gap(),selectedFacts),0,1);
        var current=Card(V(Text("继续你的冒险",18,true),Gap(),selected,Gap(),H(Button("选择实例",(_,_)=>Navigate(editionBox.SelectedIndex==1?"bedrock-local":"games")),Button("管理内容",(_,_)=>{if(editionBox.SelectedIndex==1){if(homeBedrock.SelectedItem is BedrockPackage package)OpenBedrockData(package);}else{installedGame.SelectedItem=homeGame.SelectedItem;Navigate("installed");}}),Button("基岩版版本管理",(_,_)=>Navigate("bedrock-local")))));current.MaxWidth=760;current.HorizontalAlignment=HorizontalAlignment.Left;
        var quick=Card(V(Text("发现更多内容",17,true),Gap(8),H(Button("Java",(_,_)=>ShowDownload("minecraft")),Button("基岩版",(_,_)=>ShowDownload("bedrock")),Button("模组",(_,_)=>ShowDownload("mod")),Button("光影",(_,_)=>ShowDownload("shader")),Button("材质包",(_,_)=>ShowDownload("resourcepack")),Button("整合包",(_,_)=>ShowDownload("modpack")))));quick.HorizontalAlignment=HorizontalAlignment.Left;quick.Margin=new(0,16,0,0);
        homeSummary.TextWrapping=TextWrapping.Wrap;Page("home",Scroll(V(current,quick,Gap(10),homeSummary)));
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
