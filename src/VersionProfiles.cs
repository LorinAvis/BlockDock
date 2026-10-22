using System.IO;
using System.Text.Json.Nodes;

namespace BlockDock;
// Create a named instance with a single vanilla parent and explicit loader metadata.
public static class VersionProfiles
{
    public static JsonObject Create(string root,string sourceId,string instanceId)
    {
        var chain=new List<JsonObject>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string current=Paths.Id(sourceId);
        while(true){if(!seen.Add(current)||seen.Count>16)throw new IOException("版本继承存在循环。");var node=JsonNode.Parse(File.ReadAllText(Paths.Safe(root,$"versions/{current}/{current}.json")))?.AsObject()??throw new IOException("版本配置为空。");chain.Add(node);if(node["inheritsFrom"]?.ToString() is not string parent)break;current=Paths.Id(parent);}
        JsonObject profile=new();for(int i=chain.Count-2;i>=0;i--)Merge(profile,chain[i]);var vanilla=chain[^1];string? main=profile["mainClass"]?.ToString()??vanilla["mainClass"]?.ToString();if(string.IsNullOrWhiteSpace(main))throw new IOException("版本配置缺少 mainClass，尚不能创建实例。");
        profile["id"]=Paths.Id(instanceId);profile["inheritsFrom"]=current;profile["jar"]=current;profile["mainClass"]=main;profile["type"]=vanilla["type"]?.ToString()??"release";profile["time"]=DateTimeOffset.UtcNow.ToString("O");profile["releaseTime"]=vanilla["releaseTime"]?.ToString()??DateTimeOffset.UtcNow.ToString("O");return profile;
    }
    static void Merge(JsonObject destination,JsonObject source){foreach(var pair in source){if(pair.Value is JsonObject obj&&destination[pair.Key] is JsonObject target)Merge(target,obj);else if(pair.Value is JsonArray array&&destination[pair.Key] is JsonArray prior){foreach(var item in array)prior.Add(item?.DeepClone());}else destination[pair.Key]=pair.Value?.DeepClone();}}
}
