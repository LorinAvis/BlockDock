using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace BlockDock;
public sealed class BrowserAuthorization : IDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);
    public string Verifier{get;}=Base64(RandomNumberGenerator.GetBytes(48));
    public string State{get;}=Base64(RandomNumberGenerator.GetBytes(32));
    public string RedirectUri{get;}
    public string AuthorizationUrl{get;}
    static string Base64(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    public BrowserAuthorization(string clientId)
    {
        if(!Guid.TryParse(clientId,out _))throw new IOException("启动器登录配置暂未完成，请联系发布者。");listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;RedirectUri=$"http://localhost:{port}/";
        string challenge=Base64(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));var parameters=new Dictionary<string,string>{{"client_id",clientId},{"response_type","code"},{"redirect_uri",RedirectUri},{"response_mode","query"},{"scope","XboxLive.signin offline_access"},{"state",State},{"code_challenge",challenge},{"code_challenge_method","S256"},{"prompt","select_account"}};
        AuthorizationUrl="https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?"+string.Join("&",parameters.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)));SkinAccounts.RememberSecret(Verifier);
    }
    public async Task<string> WaitCodeAsync(CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while(true){using var client=await listener.AcceptTcpClientAsync(timeout.Token);using var inputTimeout=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);inputTimeout.CancelAfter(TimeSpan.FromSeconds(10));using var stream=client.GetStream();var bytes=new byte[16384];int length=0;bool ended=false;
            try{while(length<bytes.Length){int count=await stream.ReadAsync(bytes.AsMemory(length),inputTimeout.Token);if(count==0)break;length+=count;if(Encoding.ASCII.GetString(bytes,0,length).Contains("\r\n\r\n",StringComparison.Ordinal)){ended=true;break;}}}catch(OperationCanceledException)when(!timeout.IsCancellationRequested){continue;}
            string first=Encoding.ASCII.GetString(bytes,0,length).Split("\r\n")[0];var parts=first.Split(' ');Dictionary<string,string> values=new(StringComparer.Ordinal);bool valid=ended&&parts.Length==3&&parts[0]=="GET"&&parts[1].StartsWith("/?",StringComparison.Ordinal);
            if(valid){try{foreach(string pair in parts[1][2..].Split('&')){var item=pair.Split('=',2);if(!values.TryAdd(Uri.UnescapeDataString(item[0]),item.Length>1?Uri.UnescapeDataString(item[1].Replace('+',' ')):"")){valid=false;break;}}}catch(UriFormatException){valid=false;}}
            valid=valid&&values.TryGetValue("state",out var state)&&state==State;string html=valid?"<meta charset='utf-8'><title>BlockDock</title><p>已收到登录结果，可以关闭此页面并返回 BlockDock。</p>":"<meta charset='utf-8'><title>BlockDock</title><p>此请求不属于当前登录。请回到 BlockDock 重新登录。</p>";
            byte[] body=Encoding.UTF8.GetBytes(html);string headers=$"HTTP/1.1 {(valid?"200 OK":"400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n";await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),timeout.Token);await stream.WriteAsync(body,timeout.Token);
            if(!valid)continue;if(values.TryGetValue("error",out var error))throw new IOException(error=="access_denied"?"微软登录已取消。":"微软网页登录未完成，请重试。当前 AppID 审核结果以审核邮件和游戏服务验证为准。");if(values.TryGetValue("code",out var code)&&code.Length>0){SkinAccounts.RememberSecret(code);return code;}
        }
    }
    public void Dispose()=>listener.Stop();
}
public sealed partial class MicrosoftAccounts
{
    public async Task<MicrosoftAccount> CompleteBrowserAsync(string clientId,BrowserAuthorization browser,CancellationToken ct)
    {
        string code=await browser.WaitCodeAsync(ct);var data=await Post(OAuth+"token",new Dictionary<string,string>{{"client_id",clientId},{"grant_type","authorization_code"},{"code",code},{"redirect_uri",browser.RedirectUri},{"code_verifier",browser.Verifier},{"scope",Scope}},ct);return await ExchangeAsync(clientId,data["access_token"]!.ToString(),data["refresh_token"]?.ToString()??"",ct);
    }
}
public sealed class MicrosoftLoginWindow : Window
{
    readonly CancellationTokenSource cancel=new();readonly TextBlock message=new(){Text="打开系统浏览器，在微软官方网页完成登录。",TextWrapping=TextWrapping.Wrap};readonly Button browser=new(){Content="在浏览器登录微软"};
    public MicrosoftAccount? Account{get;private set;}
    public MicrosoftLoginWindow()
    {
        Title="微软正版登录";Width=540;Height=390;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Theme.Background;Foreground=Theme.Text;FontFamily=new("Microsoft YaHei UI");var root=new StackPanel{Margin=new(28)};Content=root;root.Children.Add(new TextBlock{Text="登录微软账号",FontSize=24,Margin=new(0,0,0,18)});root.Children.Add(message);browser.Style=(Style)FindResource("Primary");browser.Margin=new(0,20,0,12);root.Children.Add(browser);var backup=new Button{Content="备用：使用登录码"};root.Children.Add(backup);browser.Click+=async(_,_)=>{browser.IsEnabled=backup.IsEnabled=false;try{using var flow=new BrowserAuthorization(MicrosoftAccounts.ClientId());using var auth=new MicrosoftAccounts();message.Text="等待微软浏览器登录…\n完成后会自动返回。";Paths.Open(flow.AuthorizationUrl);Account=await auth.CompleteBrowserAsync(MicrosoftAccounts.ClientId(),flow,cancel.Token);DialogResult=true;}catch(OperationCanceledException){if(!cancel.IsCancellationRequested)message.Text="登录等待超时，请重试。";}catch(Exception e){if(!cancel.IsCancellationRequested)message.Text=SkinAccounts.Redact(e.Message);}finally{browser.IsEnabled=backup.IsEnabled=true;}};
        backup.Click+=(_,_)=>{var dialog=new DeviceCodeLoginWindow{Owner=this};if(dialog.ShowDialog()==true){Account=dialog.Account;DialogResult=true;}};Closed+=(_,_)=>cancel.Cancel();
    }
}
