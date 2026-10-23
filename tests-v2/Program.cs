using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net;
using System.Net.Http;

static class Program
{
    static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    [STAThread]static int Main(string[] args)
    {
        try{if(args.Contains("--world-checks")){WorldChecks.Run();return 0;}if(args.Contains("--update-checks")){UpdateChecks.Run(args.Contains("--live-update"));return 0;}if(args.Contains("--release-checks")){ReleaseChecks.Run();return 0;}if(args.Contains("--bedrock-storage-checks")){BedrockStorageChecks.Run();return 0;}if(args.Contains("--bedrock-deployment-checks")){BedrockDeploymentChecks.Run();return 0;}if(args.Contains("--game-window-fixture"))return PlayerLaunchChecks.Fixture(args);if(args.Contains("--player-launch-checks")){PlayerLaunchChecks.Run();return 0;}if(args.Contains("--isolation-checks")){BedrockIsolationChecks.Run();BedrockStorageChecks.Run();IconQualityChecks.Run();return 0;}if(args.Contains("--memory-diagnose")){ConfigurationChecks.DescribeExistingMemory();return 0;}if(args.Contains("--prepare-profile")){ConfigurationChecks.PrepareProfileAsync().GetAwaiter().GetResult();return 0;}SkinTest();if(args.Contains("--jobs"))JobChecks.Run();Run(args).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.WriteLine(e);return 1;}
    }
    static void SkinTest()
    {
        WallpaperTest();UiFeatureChecks.Run();ConfigurationChecks.Run();
        bool rejected=false;var pixels=Enumerable.Repeat((byte)255,64*64*4).ToArray();var bitmap=BitmapSource.Create(64,64,96,96,PixelFormats.Bgra32,null,pixels,256);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);var skin=SkinAccounts.RenderSkin(stream.ToArray());Check(skin.PixelWidth==144&&skin.PixelHeight==192,"renders full front skin without network");
        foreach(var path in new[]{"../escape","/escape","C:/escape","over/../../escape","mods\\escape.jar",".blockdock-profile.json","install.ps1"}){rejected=false;try{ResourceService.CheckPackPath(path);}catch(IOException){rejected=true;}Check(rejected,"reject pack path: "+path);}
        ResourceService.CheckPackPath("mods/example.jar");
        var source=DownloadProvider.Parse("{\"schemaVersion\":1,\"name\":\"Fixture\",\"rules\":[{\"host\":\"libraries.minecraft.net\",\"baseUrl\":\"https://example.test/maven\"}]}");DownloadProvider.Save(source);Check(DownloadProvider.Rewrite("https://libraries.minecraft.net/a/b.jar")=="https://example.test/maven/a/b.jar","declarative source preserves artifact path");using(var dl=new Downloader("Official"))Check(dl.Candidates("https://libraries.minecraft.net/a/b.jar").Single()=="https://libraries.minecraft.net/a/b.jar","official source never rewrites even with imported provider");File.Delete(DownloadProvider.ConfigPath);
        rejected=false;try{DownloadProvider.Parse("{\"schemaVersion\":1,\"name\":\"Bad\",\"rules\":[{\"host\":\"login.live.com\",\"baseUrl\":\"https://evil.example\"}]}");}catch(IOException){rejected=true;}Check(rejected,"download provider cannot redirect account endpoints");
    }
    static void WallpaperTest()
    {
        string root=Path.GetFullPath("work/checks-wallpapers");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"preview.jpg"),"fixture");File.WriteAllText(Path.Combine(root,"movie.mp4"),"fixture");string project=Path.Combine(root,"project.json");
        File.WriteAllText(project,"{\"title\":\"Video fixture\",\"type\":\"video\",\"file\":\"movie.mp4\",\"preview\":\"preview.jpg\"}");var item=WallpaperLibrary.ReadProject(project);Check(item?.Kind=="video"&&item.File==Path.Combine(root,"movie.mp4"),"Wallpaper Engine video uses local media file");
        File.WriteAllText(project,"{\"type\":\"scene\",\"file\":\"scene.pkg\",\"preview\":\"preview.jpg\"}");item=WallpaperLibrary.ReadProject(project);Check(item==null&&WallpaperLibrary.IsScenePreview(Path.Combine(root,"preview.jpg")),"scene wallpaper no longer substitutes a low resolution preview");
        File.WriteAllText(project,"{\"type\":\"image\",\"file\":\"../../escape.jpg\",\"preview\":\"../escape.jpg\"}");Check(WallpaperLibrary.ReadProject(project)==null,"wallpaper metadata cannot escape its project directory");Check(new Settings().WallpaperFile=="","default background selection does not contain a machine-specific file path");
    }
    static async Task Run(string[] args)
    {
        string root=Path.GetFullPath(args.FirstOrDefault(x=>!x.StartsWith("--"))??"work/checks-v2");Directory.CreateDirectory(root);var settings=new Settings{JavaRoot=Path.Combine(root,"java"),JavaPath=@"C:\Program Files\Java\jdk1.8.0_341\bin\java.exe",Source="Official"};
        using var launch=new LaunchCore(settings){Log=Console.WriteLine};using var resource=new ResourceService(settings){Log=Console.WriteLine};var p=new InlineProgress<WorkProgress>(x=>{if(x.Total==0&&x.Bytes==0)Console.WriteLine(x.Message);});
        await ConfigurationChecks.ProfileAsync();await AuthTest();await UiFeatureChecks.AuthLoopbackAsync();await UiFeatureChecks.JobGateAsync();var sourceRoot=Path.GetFullPath("work/checks-live/java");if(!Directory.Exists(settings.JavaRoot)&&Directory.Exists(sourceRoot)){Console.WriteLine("Copying isolated verified 1.8.9 fixture");CopyDirectory(sourceRoot,settings.JavaRoot);}
        if(args.Contains("--ui-data"))
        {
            var list=await resource.SearchAsync("","mod",null,0,default);Check(list.Items.Select(x=>x.Downloads).SequenceEqual(list.Items.Select(x=>x.Downloads).OrderByDescending(x=>x)),"live mod list sorted by download count");var item=list.Items.First(x=>x.IconUrl.StartsWith("https://"));using var images=new HttpClient();var data=await images.GetByteArrayAsync(item.IconUrl);Check(data.Length>100,"project icon URL returns image data");Check(item.ProjectUrl.StartsWith("https://modrinth.com/mod/"),"introduction links point to original project page");
        }
        if(args.Contains("--network"))
        {
            Check((await launch.LoadersAsync("1.21.1","fabric",default)).Count>0,"Fabric official loader catalogue");Check((await launch.LoadersAsync("1.21.1","forge",default)).Count>0,"Forge official loader catalogue");Check((await launch.LoadersAsync("1.21.1","neoforge",default)).Count>0,"NeoForge official loader catalogue");
            var target=new GameInstance("resource-fixture","Resource fixture","1.21.1","fabric",Path.Combine(root,"resources"),Path.Combine(root,"resources"));Directory.CreateDirectory(target.Folder);
            var search=await resource.SearchAsync("iris","mod",target,0,default);Check(search.Items.Count>0,"Modrinth project search filtered by game and loader");var versions=await resource.VersionsAsync("iris","mod",target,default);Check(versions.Count>0,"compatible mod versions");var iris=search.Items.First(x=>x.Slug=="iris");int count=await resource.InstallAsync(iris,versions.First(x=>x.Type=="release"),target,p,default);Check(count>=2&&Directory.GetFiles(Path.Combine(target.Folder,"mods"),"*.jar").Length>=2,"Iris required Sodium dependency installed automatically");
            var installed=ResourceService.Installed(target,"mod").First();ResourceService.Toggle(installed);Check(File.Exists(installed.Path+".disabled"),"disable installed mod while preserving file");ResourceService.Toggle(installed with{Path=installed.Path+".disabled"});
            foreach(var type in new[]{"resourcepack","shader"}){var projects=await resource.SearchAsync(type=="shader"?"Complementary Reimagined":"Default Dark Mode",type,target,0,default);var project=projects.Items.First();var choices=await resource.VersionsAsync(project.Id,type,target,default);Check(choices.Count>0,"compatible "+type+" versions");await resource.InstallAsync(project,choices.First(),target,p,default);Check(ResourceService.Installed(target,type).Count>0,"download and verify "+type);}
            string pack=Path.Combine(root,"fixture.mrpack");if(File.Exists(pack))File.Delete(pack);using(var zip=ZipFile.Open(pack,ZipArchiveMode.Create)){using(var writer=new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))writer.Write(new JsonObject{["formatVersion"]=1,["game"]="minecraft",["versionId"]="1",["name"]="Pack fixture",["files"]=new JsonArray(),["dependencies"]=new JsonObject{["minecraft"]="1.8.9"}}.ToJsonString());using(var writer=new StreamWriter(zip.CreateEntry("overrides/options.txt").Open()))writer.Write("lang:zh_CN\n");}
            var packGame=await resource.ImportPackAsync(pack,"Pack fixture",p,default);Check(File.ReadAllText(Path.Combine(packGame.GameDirectory,"options.txt")).Contains("zh_CN")&&!packGame.Pending,"MRPACK installs separate instance and overrides");var same=await resource.ImportPackAsync(pack,"Pack fixture",p,default);Check(same.Id==packGame.Id,"reimport same MRPACK job is idempotent");
            if(args.Contains("--forge")){using var dl=new Downloader();var mc=(await new JavaService(settings,dl).VersionsAsync(default)).First(x=>x.Id=="1.8.9");var game=await launch.InstallAsync(mc,"forge","","Forge fixture",p,default);Check(game.Loader=="forge"&&File.Exists(Path.Combine(game.Folder,game.Id+".json")),"actual Forge install creates usable inherited instance");}
        }
        Console.WriteLine("ALL CHECKS PASSED");
    }
    static async Task AuthTest()
    {
        bool owned=true;var urls=new List<string>();using var auth=new MicrosoftAccounts(new AuthHandler(request=>{urls.Add(request.RequestUri!.Host);string path=request.RequestUri.AbsolutePath;string data=path.EndsWith("/token")?"{\"access_token\":\"ms_fixture_token\",\"refresh_token\":\"refresh_fixture_token\"}":path.Contains("/user/authenticate")?"{\"Token\":\"xbox_fixture_token\"}":path.Contains("/xsts/authorize")?"{\"Token\":\"xsts_fixture_token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"fixturehash\",\"xid\":\"123\"}]}}":path.Contains("login_with_xbox")?"{\"access_token\":\"minecraft_fixture_token\",\"expires_in\":86400}":path.Contains("entitlements")?(owned?"{\"items\":[{\"name\":\"game_minecraft\"}]}":"{\"items\":[]}"):"{\"id\":\"1234567890abcdef1234567890abcdef\",\"name\":\"Fixture\",\"skins\":[]}";return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(data)};}));
        var initial=new MicrosoftAccount("Fixture","1234567890abcdef1234567890abcdef","123","old_fixture_token","refresh_fixture_token",DateTimeOffset.MinValue,"11111111-1111-1111-1111-111111111111");var result=await auth.RefreshAsync(initial,default);Check(result.Session.UserType=="msa"&&result.Name=="Fixture"&&urls.Contains("api.minecraftservices.com"),"Microsoft flow exchanges Xbox token and checks Minecraft entitlement/profile");MicrosoftAccounts.Save(new[]{result});Check(MicrosoftAccounts.Load().Single()==result,"Microsoft token vault encrypted round trip");Check(!SkinAccounts.Redact(result.AccessToken).Contains(result.AccessToken),"Microsoft token redaction");File.Delete(Path.Combine(Settings.DataPath,"microsoft-accounts.dat"));owned=false;bool rejected=false;try{await auth.RefreshAsync(initial,default);}catch(IOException){rejected=true;}Check(rejected,"Microsoft account without Java entitlement cannot launch Java edition");
        Check(ResourceService.SearchPath("iris","mod",null,0).Contains("index=downloads"),"content search uses download-count sorting by default");
    }
    static void CopyDirectory(string from,string to){Directory.CreateDirectory(to);foreach(var f in Directory.EnumerateFiles(from))File.Copy(f,Path.Combine(to,Path.GetFileName(f)),true);foreach(var d in Directory.EnumerateDirectories(from))CopyDirectory(d,Path.Combine(to,Path.GetFileName(d)));}
}
sealed class InlineProgress<T>(Action<T> callback):IProgress<T>{public void Report(T value)=>callback(value);}
sealed class AuthHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(respond(request));}
