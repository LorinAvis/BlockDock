using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;

public partial class MainWindow
{
    readonly SkinPreview skinPreview=new();readonly ComboBox skinModel=new();readonly Button applySkinButton=new();readonly Dictionary<string,BitmapSource> playerAvatars=new();byte[]? selectedSkin,displayedSkin;string selectedSkinOwner="";bool skinUpdating,skinApplying;
    UIElement SkinControls()
    {
        skinModel.ItemsSource=new[]{"宽臂 · Classic","细臂 · Slim"};skinModel.SelectedIndex=0;skinModel.SelectionChanged+=(_,_)=>{if(skinUpdating)return;skinPreview.Slim=skinModel.SelectedIndex==1;if(displayedSkin!=null&&accountBox.SelectedItem is MicrosoftAccount account){selectedSkin=displayedSkin;selectedSkinOwner=account.Uuid;applySkinButton.IsEnabled=!skinApplying;skinStatus.Text="模型预览已更改，点击应用后更新当前 Java 角色。";}};var outer=new CheckBox{Content="显示外层",IsChecked=true,Foreground=Theme.Text,VerticalAlignment=VerticalAlignment.Center};outer.Checked+=(_,_)=>skinPreview.Layers=true;outer.Unchecked+=(_,_)=>skinPreview.Layers=false;
        Primary(applySkinButton,"应用到当前 Java 角色",async(_,_)=>await ApplySelectedSkinAsync());applySkinButton.IsEnabled=false;
        return V(H(Button("正面",(_,_)=>skinPreview.View(0)),Button("背面",(_,_)=>skinPreview.View(180)),Button("重置视角",(_,_)=>skinPreview.ResetView()),outer),Gap(8),Hint("拖动旋转 · 滚轮缩放"),Gap(8),H(skinModel,Button("选择皮肤 PNG",(_,_)=>ChooseSkin()),applySkinButton));
    }
    void ShowPlayerSkin(MicrosoftAccount account,byte[] bytes)
    {
        playerAvatars[account.Uuid]=SkinAccounts.Avatar(bytes);if(accountBox.SelectedItem is not MicrosoftAccount selected||selected.Uuid!=account.Uuid)return;displayedSkin=bytes;skinUpdating=true;try{skinModel.SelectedIndex=account.SkinVariant.Equals("SLIM",StringComparison.OrdinalIgnoreCase)?1:0;skinPreview.SetSkin(bytes,skinModel.SelectedIndex==1);}finally{skinUpdating=false;}defaultSkin.Visibility=Visibility.Collapsed;headerAvatar.Source=playerAvatars[account.Uuid];RenderOptions.SetBitmapScalingMode(headerAvatar,BitmapScalingMode.NearestNeighbor);
    }
    static string SkinCachePath(string uuid){if(!System.Text.RegularExpressions.Regex.IsMatch(uuid,"^[a-fA-F0-9]{32}$"))throw new IOException("角色 UUID 无效。");return Path.Combine(Settings.DataPath,"skins",uuid+".png");}
    static async Task<byte[]> DownloadSkinAsync(string url,CancellationToken ct)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Host!="textures.minecraft.net"||uri.Scheme is not("https" or "http"))throw new IOException("官方角色返回的皮肤地址不受支持。");uri=new UriBuilder(uri){Scheme="https",Port=-1}.Uri;using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(25)};using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>4*1024*1024)throw new IOException("皮肤文件过大。");using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();byte[] buffer=new byte[8192];int read;while((read=await input.ReadAsync(buffer,ct))>0){if(output.Length+read>4*1024*1024)throw new IOException("皮肤文件过大。");output.Write(buffer,0,read);}var bytes=output.ToArray();SkinAccounts.ValidatePng(bytes);return bytes;
    }
    void ChooseSkin()
    {
        if(skinApplying)return;var dialog=new Microsoft.Win32.OpenFileDialog{Title="选择要预览的皮肤",Filter="Minecraft 皮肤|*.png"};if(dialog.ShowDialog()!=true)return;
        try{skinCts?.Cancel();if(new FileInfo(dialog.FileName).Length>4*1024*1024)throw new IOException("皮肤不能超过 4 MB。");var bytes=File.ReadAllBytes(dialog.FileName);skinPreview.SetSkin(bytes,skinModel.SelectedIndex==1);displayedSkin=selectedSkin=bytes;selectedSkinOwner=(accountBox.SelectedItem as MicrosoftAccount)?.Uuid??"";defaultSkin.Visibility=Visibility.Collapsed;applySkinButton.IsEnabled=selectedSkinOwner.Length>0;skinStatus.Text="本地预览："+Path.GetFileName(dialog.FileName)+"\n点击应用后才会更换当前 Java 角色皮肤。";}catch(Exception e){skinStatus.Text=e.Message;}
    }
    async Task ApplySelectedSkinAsync()
    {
        if(skinApplying||selectedSkin==null||accountBox.SelectedItem is not MicrosoftAccount account||account.Uuid!=selectedSkinOwner)return;byte[] png=selectedSkin;bool slim=skinModel.SelectedIndex==1;skinApplying=true;skinModel.IsEnabled=accountBox.IsEnabled=false;applySkinButton.IsEnabled=false;skinStatus.Text="正在应用皮肤…";
        try{using var service=new MicrosoftAccounts();var updated=await service.UploadSkinAsync(account,png,slim,default);accounts.RemoveAll(x=>x.Uuid==updated.Uuid);accounts.Add(updated);MicrosoftAccounts.Save(accounts);selectedSkin=null;selectedSkinOwner="";ReloadAccounts();skinStatus.Text="官方服务已接受皮肤，正在刷新角色。";}
        catch(Exception e){skinStatus.Text="应用未完成："+SkinAccounts.Redact(e.Message);}
        finally{skinApplying=false;skinModel.IsEnabled=accountBox.IsEnabled=true;applySkinButton.IsEnabled=selectedSkin!=null&&accountBox.SelectedItem is MicrosoftAccount current&&current.Uuid==selectedSkinOwner;}
    }
}
