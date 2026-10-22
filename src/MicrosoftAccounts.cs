using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core.Auth;

namespace BlockDock;
public sealed record MicrosoftAccount(string Name,string Uuid,string Xuid,string AccessToken,string RefreshToken,DateTimeOffset Expires,string ClientId,string SkinUrl="",string SkinVariant="CLASSIC")
{
    public override string ToString()=>Name+" · 微软正版账号";
    public MSession Session=>new(){Username=Name,UUID=Uuid,AccessToken=AccessToken,ClientToken="",Xuid=Xuid,UserType="msa"};
}
public sealed record MicrosoftDeviceCode(string Code,string DeviceCode,string Url,int Interval,int Expires);
public sealed partial class MicrosoftAccounts : IDisposable
{
    readonly HttpClient http;const string OAuth="https://login.microsoftonline.com/consumers/oauth2/v2.0/";const string Scope="XboxLive.signin offline_access";
    static string Vault=>Path.Combine(Settings.DataPath,"microsoft-accounts.dat");static readonly byte[] entropy=Encoding.UTF8.GetBytes("BlockDock Microsoft accounts v2");
    public MicrosoftAccounts(HttpMessageHandler? handler=null){http=new(handler??new HttpClientHandler{AllowAutoRedirect=false});http.Timeout=TimeSpan.FromSeconds(35);}
    public static string ClientId()
    {
        string file=Path.Combine(AppContext.BaseDirectory,"app-registration.json");if(!File.Exists(file))return "";string value=JsonNode.Parse(File.ReadAllText(file))?["clientId"]?.ToString()??"";return Guid.TryParse(value,out _)?value:"";
    }
    public static List<MicrosoftAccount> Load(){if(!File.Exists(Vault))return new();try{var list=JsonSerializer.Deserialize<List<MicrosoftAccount>>(ProtectedData.Unprotect(File.ReadAllBytes(Vault),entropy,DataProtectionScope.CurrentUser))??new();foreach(var a in list)Remember(a);return list;}catch(CryptographicException){throw new IOException("此微软登录记录属于另一位 Windows 用户，请重新登录。");}}
    public static void Save(IEnumerable<MicrosoftAccount> values){Directory.CreateDirectory(Settings.DataPath);File.WriteAllBytes(Vault+".new",ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(values),entropy,DataProtectionScope.CurrentUser));File.Move(Vault+".new",Vault,true);}
    static void Remember(MicrosoftAccount account){SkinAccounts.RememberSecret(account.AccessToken);SkinAccounts.RememberSecret(account.RefreshToken);}
    public static void SaveClientId(string value)
    {
        if(!Guid.TryParse(value.Trim(),out var id))throw new IOException("应用 ID 应为微软门户中 Application (client) ID 的完整编号。");
        Settings.AtomicText(Path.Combine(AppContext.BaseDirectory,"app-registration.json"),JsonSerializer.Serialize(new{clientId=id.ToString()},new JsonSerializerOptions{WriteIndented=true}));
    }
    async Task<JsonNode> Post(string url,object value,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,url);if(request.RequestUri!.Host.EndsWith(".xboxlive.com",StringComparison.Ordinal))request.Headers.TryAddWithoutValidation("x-xbl-contract-version","1");request.Content=value is Dictionary<string,string> form?new FormUrlEncodedContent(form):new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json");using var response=await http.SendAsync(request,ct);var text=await response.Content.ReadAsStringAsync(ct);JsonNode data;try{data=JsonNode.Parse(text)??new JsonObject();}catch(JsonException){throw new IOException("登录服务暂时返回了无法识别的结果。");}
        if(!response.IsSuccessStatusCode){string error=data["error"]?.ToString()??data["XErr"]?.ToString()??response.StatusCode.ToString();if(error is "authorization_pending" or "slow_down")return data;if(error=="invalid_client"&&data["error_description"]?.ToString().Contains("70002")==true)throw new IOException("微软应用尚未启用公共客户端登录。请由发布者在微软门户 → 应用注册 → 此应用 → 身份验证 → 高级设置，将允许公共客户端流设为是并保存。无需填写客户端密码。具体步骤见《开发者登录配置.md》。");if(error=="2148916233")throw new IOException("这个微软账号还没有 Xbox 玩家资料，请先在 Xbox 官网创建资料。");if(error is "2148916238" or "2148916235")throw new IOException("Xbox 账号的年龄或家庭设置限制了登录，请在微软家庭设置中处理。");if((int)response.StatusCode==403&&url.Contains("minecraftservices")){bool registration=text.Contains("Invalid app registration",StringComparison.OrdinalIgnoreCase);throw new IOException(registration?"微软账号授权已完成，但 Minecraft 服务尚未接受 BlockDock 的应用 ID。请等待 AppID 审核通过并生效后重试。":"微软账号授权已完成，但 Minecraft 游戏服务拒绝了请求（HTTP 403）。当前无法判断是否与 AppID 审核有关，请稍后重试。");}throw new IOException("微软 / Xbox 登录未完成："+error+"。请重试或重新登录。");}return data;
    }
    public async Task<MicrosoftDeviceCode> BeginAsync(string clientId,CancellationToken ct)
    {
        if(!Guid.TryParse(clientId,out _))throw new IOException("此构建的正版登录还未配置完成，请等待发布者完成接入。登记方法在随包的《开发者登录配置》中。");var d=await Post(OAuth+"devicecode",new Dictionary<string,string>{{"client_id",clientId},{"scope",Scope}},ct);return new(d["user_code"]!.ToString(),d["device_code"]!.ToString(),d["verification_uri"]!.ToString(),d["interval"]?.GetValue<int>()??5,d["expires_in"]?.GetValue<int>()??900);
    }
    public async Task<MicrosoftAccount> CompleteAsync(string clientId,MicrosoftDeviceCode device,CancellationToken ct)
    {
        var until=DateTimeOffset.UtcNow.AddSeconds(device.Expires);int interval=Math.Max(5,device.Interval);while(DateTimeOffset.UtcNow<until){await Task.Delay(TimeSpan.FromSeconds(interval),ct);var data=await Post(OAuth+"token",new Dictionary<string,string>{{"client_id",clientId},{"grant_type","urn:ietf:params:oauth:grant-type:device_code"},{"device_code",device.DeviceCode}},ct);if(data["error"]?.ToString()=="slow_down"){interval+=5;continue;}if(data["error"]?.ToString()=="authorization_pending")continue;return await ExchangeAsync(clientId,data["access_token"]!.ToString(),data["refresh_token"]?.ToString()??"",ct);}throw new IOException("登录码已过期，请重新登录。");
    }
    public async Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account,CancellationToken ct)
    {
        if(account.RefreshToken.Length==0)throw new IOException("微软登录已过期，请重新登录。");var data=await Post(OAuth+"token",new Dictionary<string,string>{{"client_id",account.ClientId},{"grant_type","refresh_token"},{"refresh_token",account.RefreshToken},{"scope",Scope}},ct);return await ExchangeAsync(account.ClientId,data["access_token"]!.ToString(),data["refresh_token"]?.ToString()??account.RefreshToken,ct);
    }
    async Task<MicrosoftAccount> ExchangeAsync(string clientId,string access,string refresh,CancellationToken ct)
    {
        SkinAccounts.RememberSecret(access);SkinAccounts.RememberSecret(refresh);var xbox=await Post("https://user.auth.xboxlive.com/user/authenticate",new{Properties=new{AuthMethod="RPS",SiteName="user.auth.xboxlive.com",RpsTicket="d="+access},RelyingParty="http://auth.xboxlive.com",TokenType="JWT"},ct);string user=xbox["Token"]!.ToString();SkinAccounts.RememberSecret(user);var xsts=await Post("https://xsts.auth.xboxlive.com/xsts/authorize",new{Properties=new{SandboxId="RETAIL",UserTokens=new[]{user}},RelyingParty="rp://api.minecraftservices.com/",TokenType="JWT"},ct);string token=xsts["Token"]!.ToString(),hash=xsts["DisplayClaims"]!["xui"]![0]!["uhs"]!.ToString(),xuid=xsts["DisplayClaims"]!["xui"]![0]?["xid"]?.ToString()??"";SkinAccounts.RememberSecret(token);
        var minecraft=await Post("https://api.minecraftservices.com/authentication/login_with_xbox",new{identityToken="XBL3.0 x="+hash+";"+token},ct);string gameToken=minecraft["access_token"]!.ToString();SkinAccounts.RememberSecret(gameToken);var entitlement=await GetGame("entitlements/mcstore",gameToken,ct);if(entitlement["items"] is not JsonArray items||!items.Any(x=>x?["name"]?.ToString() is "game_minecraft" or "product_minecraft"))throw new IOException("这个微软账号当前没有 Java 版权限，请确认购买账号或有效订阅。");
        var profile=await GetGame("minecraft/profile",gameToken,ct);var skin=profile["skins"]?.AsArray().FirstOrDefault(x=>x?["state"]?.ToString()=="ACTIVE");var account=new MicrosoftAccount(profile["name"]!.ToString(),profile["id"]!.ToString(),xuid,gameToken,refresh,DateTimeOffset.UtcNow.AddSeconds(minecraft["expires_in"]?.GetValue<int>()??86400),clientId,skin?["url"]?.ToString()??"",skin?["variant"]?.ToString()??"CLASSIC");Remember(account);return account;
    }
    async Task<JsonNode> GetGame(string path,string token,CancellationToken ct){using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/"+path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);using var response=await http.SendAsync(request,ct);if((int)response.StatusCode==404)throw new IOException("账号没有 Java 版角色，请先在 Minecraft 官网完成正版购买和角色设置。");if(!response.IsSuccessStatusCode)throw new IOException("无法验证 Java 版权限，请重新登录或稍后重试。");return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;}
    public void Dispose()=>http.Dispose();
}
public sealed class DeviceCodeLoginWindow : Window
{
    readonly CancellationTokenSource cancel=new();readonly TextBlock message=new(){TextWrapping=TextWrapping.Wrap};readonly TextBox code=new(){IsReadOnly=true,FontSize=32,TextAlignment=TextAlignment.Center};readonly Button open=new(){Content="在浏览器登录微软",IsEnabled=false};string url="";public MicrosoftAccount? Account{get;private set;}
    public DeviceCodeLoginWindow()
    {
        Background=Theme.Background;Foreground=Theme.Text;Title="微软正版登录";Width=500;Height=345;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;var body=new StackPanel{Margin=new(28)};Content=body;body.Children.Add(new TextBlock{Text="使用微软账号登录",FontSize=23,Margin=new(0,0,0,15)});body.Children.Add(message);body.Children.Add(code);open.Margin=new(0,16,0,12);body.Children.Add(open);body.Children.Add(new TextBlock{Text="密码只在微软官方网页输入。此窗口关闭后取消等待。",TextWrapping=TextWrapping.Wrap});open.Click+=(_,_)=>{if(Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&(u.Host=="microsoft.com"||u.Host.EndsWith(".microsoft.com")||u.Host=="login.live.com"))Paths.Open(url);};Closed+=(_,_)=>cancel.Cancel();Loaded+=async(_,_)=>{using var service=new MicrosoftAccounts();try{message.Text="正在准备微软登录…";string id=MicrosoftAccounts.ClientId();var device=await service.BeginAsync(id,cancel.Token);url=device.Url;code.Text=device.Code;open.IsEnabled=true;message.Text="打开微软页面，输入下方登录码并完成登录。";Account=await service.CompleteAsync(id,device,cancel.Token);DialogResult=true;}catch(OperationCanceledException){}catch(Exception e){message.Text=e.Message;code.Visibility=Visibility.Collapsed;}};
    }
}
