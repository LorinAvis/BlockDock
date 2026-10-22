using System.IO;
using System.Text.Json;

namespace BlockDock;
// Declarative transport configuration, deliberately not an executable plugin API.
public sealed record SourceRule(string Host,string BaseUrl);
public sealed record SourceConfig(int SchemaVersion,string Name,SourceRule[] Rules,string JavaManifestUrl="",string BedrockUwpUrl="",string BedrockGdkUrl="");
public static class DownloadProvider
{
    static readonly HashSet<string> allowed=new(StringComparer.OrdinalIgnoreCase){"piston-meta.mojang.com","launchermeta.mojang.com","launcher.mojang.com","piston-data.mojang.com","resources.download.minecraft.net","libraries.minecraft.net","maven.fabricmc.net","meta.fabricmc.net","maven.minecraftforge.net","maven.neoforged.net"};
    public static string ConfigPath=>Path.Combine(Settings.DataPath,"source-provider.json");
    static readonly JsonSerializerOptions options=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    public static SourceConfig Parse(string text)
    {
        if(text.Length>65536)throw new IOException("源配置过大。");var config=JsonSerializer.Deserialize<SourceConfig>(text,options)??throw new IOException("源配置为空。");
        if(config.SchemaVersion!=1||string.IsNullOrWhiteSpace(config.Name)||config.Name.Length>80||config.Rules is null||config.Rules.Length>20)throw new IOException("源配置格式不兼容。");
        foreach(var url in new[]{config.JavaManifestUrl,config.BedrockUwpUrl,config.BedrockGdkUrl})if(url.Length>0&&(!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length>0||u.Fragment.Length>0))throw new IOException("版本清单地址必须为 HTTPS 地址。");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var rule in config.Rules){if(!allowed.Contains(rule.Host)||!seen.Add(rule.Host))throw new IOException("源包含不支持或重复的原始域名。");if(!Uri.TryCreate(rule.BaseUrl,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length>0||u.Query.Length>0||u.Fragment.Length>0)throw new IOException("源目标地址必须为不带登录信息、查询参数或片段的 HTTPS 地址。");}
        return config;
    }
    public static SourceConfig? Load()=>File.Exists(ConfigPath)?Parse(File.ReadAllText(ConfigPath)):null;
    public static string? Rewrite(string original)
    {
        var config=Load();if(config is null||!Uri.TryCreate(original,UriKind.Absolute,out var uri))return null;var rule=config.Rules.FirstOrDefault(x=>x.Host.Equals(uri.Host,StringComparison.OrdinalIgnoreCase));
        return rule is null?null:rule.BaseUrl.TrimEnd('/')+uri.PathAndQuery;
    }
    public static void Save(SourceConfig config)=>Settings.AtomicText(ConfigPath,JsonSerializer.Serialize(config,options));
    public static bool HasJava=>Load()?.JavaManifestUrl.Length>0;
    public static bool HasBedrock=>Load() is SourceConfig c&&(c.BedrockUwpUrl.Length>0||c.BedrockGdkUrl.Length>0);
    public static bool IsGameHost(string host)=>allowed.Contains(host)&&host is not("meta.fabricmc.net" or "maven.fabricmc.net" or "maven.minecraftforge.net" or "maven.neoforged.net");
    public static string JavaManifest()=>Load()?.JavaManifestUrl is string url&&url.Length>0?url:throw new IOException("请先到设置 → 下载源，添加自己的 Java 版本清单。本启动器不预置游戏本体下载源。");
}
