using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BlockDock;
public sealed class ResourceDetailWindow : Window
{
    readonly WebView2 web=new();public ResourceVersion? ChosenVersion{get;private set;}
    public ResourceDetailWindow(ResourceProject project,List<ResourceVersion> versions,string targetName)
    {
        Title=project.Name+" · 项目介绍";Width=1030;Height=790;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Theme.Background;Foreground=Theme.Text;FontFamily=new FontFamily("Microsoft YaHei UI");
        var root=new DockPanel{Margin=new(22)};Content=root;var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);header.Children.Add(new TextBlock{Text=project.Name,FontSize=25,FontWeight=FontWeights.SemiBold});header.Children.Add(new TextBlock{Text=project.Author+"  ·  "+project.Count,Foreground=Brushes.SlateGray,Margin=new(0,7,0,14)});
        var footer=new WrapPanel{Margin=new(0,15,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var choice=new ComboBox{ItemsSource=versions,MinWidth=360,MaxWidth=550,SelectedItem=versions.FirstOrDefault(x=>x.Type=="release")??versions.FirstOrDefault()};var install=new Button{Content=targetName.Length>0?"安装到 "+targetName:"安装这个整合包",IsEnabled=versions.Count>0};install.Click+=(_,_)=>{if(choice.SelectedItem is ResourceVersion v){ChosenVersion=v;DialogResult=true;}};footer.Children.Add(install);var advanced=new Expander{Header="选择其他兼容版本",Content=choice,Margin=new(5)};footer.Children.Add(advanced);var site=new Button{Content="浏览器打开项目页"};site.Click+=(_,_)=>Paths.Open(project.ProjectUrl);footer.Children.Add(site);
        root.Children.Add(web);Loaded+=async(_,_)=>{try{var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Settings.DataPath,"project-web"));await web.EnsureCoreWebView2Async(env);web.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)&&u.Scheme=="https")Paths.Open(u.AbsoluteUri);};web.Source=new Uri(project.ProjectUrl);}catch(Exception e){root.Children.Remove(web);root.Children.Add(new TextBlock{Text="介绍网页暂时无法加载，请点击下方按钮打开原项目页面。\n"+e.Message,TextWrapping=TextWrapping.Wrap});}};Closed+=(_,_)=>web.Dispose();
    }
}
