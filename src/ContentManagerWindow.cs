using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace BlockDock;
public sealed class ContentManagerWindow : Window
{
    readonly GameInstance game;readonly ListView list=new();readonly ComboBox type=new(){ItemsSource=new[]{"模组","材质包","光影包"},SelectedIndex=0};
    string Kind=>type.SelectedIndex switch{1=>"resourcepack",2=>"shader",_=>"mod"};
    public ContentManagerWindow(GameInstance game)
    {
        this.game=game;Title="管理内容 · "+game.Name;Width=850;Height=590;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Theme.Background;Foreground=Theme.Text;FontFamily=new FontFamily("Microsoft YaHei UI");
        var root=new Grid{Margin=new Thickness(24)};root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        type.Margin=new Thickness(0,0,0,14);type.SelectionChanged+=(_,_)=>Refresh();root.Children.Add(type);
        list.ItemTemplate=(DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='55'/><ColumnDefinition Width='*'/><ColumnDefinition Width='85'/></Grid.ColumnDefinitions><Image Source='{Binding Icon}' Width='40' Height='40'/><StackPanel Grid.Column='1'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='{Binding Version}' Foreground='#20E494'/><TextBlock Text='{Binding File}' Foreground='#9CAEBB' FontSize='11'/></StackPanel><TextBlock Grid.Column='2' Text='{Binding State}'/></Grid></DataTemplate>");Grid.SetRow(list,1);root.Children.Add(list);
        var buttons=new WrapPanel{Margin=new Thickness(0,16,0,0)};Grid.SetRow(buttons,2);root.Children.Add(buttons);
        void Add(string name,RoutedEventHandler action){var b=new Button{Content=name};b.Click+=action;buttons.Children.Add(b);}
        Add("启用 / 停用",(_,_)=>{if(list.SelectedItem is not InstalledResource resource)return;try{ResourceService.Toggle(resource);Refresh();}catch(Exception e){MessageBox.Show(this,e.Message);}});
        Add("移到回收目录",(_,_)=>{if(list.SelectedItem is not InstalledResource resource)return;if(MessageBox.Show(this,"将文件移到方块港回收目录？需要时可手动放回。","移出游戏",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;try{var folder=Paths.Safe(Settings.DataPath,"recycle/"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));Directory.CreateDirectory(folder);File.Move(resource.Path,Path.Combine(folder,Path.GetFileName(resource.Path)));Refresh();}catch(Exception e){MessageBox.Show(this,e.Message);}});
        Add("打开文件夹",(_,_)=>{var folder=Path.Combine(game.GameDirectory,ResourceService.ResourceFolder(Kind));Directory.CreateDirectory(folder);Paths.Open(folder);});Refresh();
    }
    void Refresh()=>list.ItemsSource=ResourceService.Installed(game,Kind);
}
