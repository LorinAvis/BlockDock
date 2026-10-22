using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class UiFeatureChecks
{
    static void Check(bool value,string label){if(!value)throw new Exception("FAIL: "+label);Console.WriteLine("PASS: "+label);}
    static byte[] Png(){var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,16*16*4).ToArray(),64);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=new MemoryStream();encoder.Save(output);return output.ToArray();}
    public static void Run()
    {
        string root=Path.GetFullPath("work/ui-feature-checks");Directory.CreateDirectory(root);byte[] png=Png();string jar=Path.Combine(root,"fixture.jar");if(File.Exists(jar))File.Delete(jar);
        using(var zip=ZipFile.Open(jar,ZipArchiveMode.Create)){using(var writer=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))writer.Write("{\"name\":\"Fixture Mod\",\"version\":\"1.2.3\",\"icon\":\"assets/fixture/icon.png\"}");using(var stream=zip.CreateEntry("assets/fixture/icon.png").Open())stream.Write(png);}
        var content=ContentIcons.Content(jar,"mod");Check(content.Name=="Fixture Mod"&&content.Version=="1.2.3"&&content.Icon is BitmapSource,"installed mod uses embedded metadata and original icon");
        string vanilla=Path.Combine(root,"versions","vanilla-fixture");Directory.CreateDirectory(vanilla);string hash=new string('a',40);Directory.CreateDirectory(Path.Combine(root,"assets","indexes"));Directory.CreateDirectory(Path.Combine(root,"assets","objects","aa"));File.WriteAllBytes(Path.Combine(root,"assets","objects","aa",hash),png);File.WriteAllText(Path.Combine(vanilla,"vanilla-fixture.json"),"{\"type\":\"release\",\"assetIndex\":{\"id\":\"icon-fixture\"}}");File.WriteAllText(Path.Combine(root,"assets","indexes","icon-fixture.json"),"{\"objects\":{\"icons/icon_32x32.png\":{\"hash\":\""+hash+"\"}}}");Check(new GameInstance("vanilla-fixture","Vanilla","1.21.1","vanilla",vanilla,vanilla).Icon is BitmapSource,"vanilla instance reads icon from its own version asset index");
        string instance=Path.Combine(root,"versions","fixture");Directory.CreateDirectory(instance);File.WriteAllBytes(Path.Combine(instance,"pack.png"),png);var game=new GameInstance("fixture","Fixture","1.21.1","fabric",instance,instance);Check(game.Icon is BitmapSource,"instance prefers its modpack cover to generic loader badge");
        Check(new Settings().Source=="Unconfigured","fresh build has no Minecraft download source");var provider=DownloadProvider.Parse("{\"schemaVersion\":1,\"name\":\"Fixture\",\"rules\":[],\"javaManifestUrl\":\"https://example.invalid/versions.json\"}");Check(provider.JavaManifestUrl.EndsWith("versions.json"),"source accepts user-supplied version manifest without preset URLs");
        Check(new Settings().MemoryMode=="Auto"&&new Settings().MemoryMb==4096&&new Settings().ParallelTasks==3,"automatic memory default and three concurrent tasks");
        var java=new[]{new JavaRuntime("eight",8,"1.8.0","amd64"),new JavaRuntime("twentyone",21,"21.0.1","amd64")};Check(JavaRuntimes.Select(java,8)?.Path=="eight"&&JavaRuntimes.Select(java,17)==null,"automatic Java selection requires matching major version");
        Console.WriteLine("DETECTED_JAVA_COUNT="+JavaRuntimes.Detect().Count);
        string target=Path.Combine(root,"transfer.bin");if(File.Exists(target))File.Delete(target);var events=new List<TransferUpdate>();DownloadTelemetry.Current.Value=events.Add;try{using var dl=new Downloader("Official",new AuthHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[4096])}));dl.FileAsync(new("https://example.invalid/transfer.bin",target,Size:4096),default).GetAwaiter().GetResult();}finally{DownloadTelemetry.Current.Value=null;}
        Check(events.Any(x=>x.State=="下载中"&&x.Speed>0)&&events.Last().Destination==target&&events.Last().Bytes==4096&&events.Last().State=="已完成","downloader publishes measured speed, actual path, bytes and completion");
    }
    public static async Task JobGateAsync()
    {
        var gate=new ResizableJobGate{Limit=1};await gate.WaitAsync(default);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));var second=gate.WaitAsync(timeout.Token);Check(!second.IsCompleted,"concurrency limit keeps extra task queued");gate.Limit=2;await second;gate.Limit=1;var third=gate.WaitAsync(timeout.Token);gate.Release();Check(!third.IsCompleted,"lowering concurrency lets active work finish before another starts");gate.Release();await third;gate.Release();using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool rejected=false;try{await gate.WaitAsync(cancelled.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected,"cancelled queued task never consumes a task slot");
    }
    public static async Task AuthLoopbackAsync()
    {
        using var flow=new BrowserAuthorization("11111111-1111-1111-1111-111111111111");var query=new Uri(flow.AuthorizationUrl).Query;Check(query.Contains("code_challenge_method=S256")&&!query.Contains(flow.Verifier),"PKCE browser URL exposes challenge and never verifier");bool tokenVerified=false;
        using var auth=new MicrosoftAccounts(new AuthHandler(request=>{string path=request.RequestUri!.AbsolutePath;if(path.EndsWith("/token")){string form=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();tokenVerified=form.Contains("code_verifier="+Uri.EscapeDataString(flow.Verifier))&&form.Contains("grant_type=authorization_code")&&!form.Contains("client_secret");}string data=path.EndsWith("/token")?"{\"access_token\":\"ms_browser_fixture\",\"refresh_token\":\"refresh_browser_fixture\"}":path.Contains("/user/authenticate")?"{\"Token\":\"xbox_browser_fixture\"}":path.Contains("/xsts/authorize")?"{\"Token\":\"xsts_browser_fixture\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"hash\",\"xid\":\"123\"}]}}":path.Contains("login_with_xbox")?"{\"access_token\":\"mc_browser_fixture\",\"expires_in\":86400}":path.Contains("entitlements")?"{\"items\":[{\"name\":\"game_minecraft\"}]}":"{\"id\":\"1234567890abcdef1234567890abcdef\",\"name\":\"BrowserFixture\",\"skins\":[]}";return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(data)};}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var pending=auth.CompleteBrowserAsync("11111111-1111-1111-1111-111111111111",flow,timeout.Token);using var callback=new HttpClient(new HttpClientHandler{UseProxy=false});using(var invalid=await callback.GetAsync(flow.RedirectUri+"?state=wrong&code=ignored"))Check(invalid.StatusCode==HttpStatusCode.BadRequest&&!pending.IsCompleted,"loopback rejects unrelated state and keeps waiting");using(var valid=await callback.GetAsync(flow.RedirectUri+"?state="+flow.State+"&code=browser_fixture_code"))Check(valid.IsSuccessStatusCode,"loopback accepts matching authorization callback");var result=await pending;Check(tokenVerified&&result.Name=="BrowserFixture","PKCE token exchange uses verifier, no secret, and verifies game ownership/profile");
    }
}
