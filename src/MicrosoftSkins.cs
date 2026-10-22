using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BlockDock;

public sealed partial class MicrosoftAccounts
{
    public async Task<MicrosoftAccount> ProfileAsync(MicrosoftAccount account,CancellationToken ct)
    {
        if(account.Expires<=DateTimeOffset.UtcNow.AddMinutes(2))return await RefreshAsync(account,ct);
        var profile=await GetGame("minecraft/profile",account.AccessToken,ct);if(profile["id"]?.ToString()!=account.Uuid)throw new IOException("角色身份与当前账号不一致，请重新登录。");var skin=profile["skins"]?.AsArray().FirstOrDefault(x=>x?["state"]?.ToString()=="ACTIVE");return account with{Name=profile["name"]?.ToString()??account.Name,SkinUrl=skin?["url"]?.ToString()??"",SkinVariant=skin?["variant"]?.ToString()??"CLASSIC"};
    }
    public async Task<MicrosoftAccount> UploadSkinAsync(MicrosoftAccount account,byte[] png,bool slim,CancellationToken ct)
    {
        SkinAccounts.ValidatePng(png);_=SkinAccounts.Texture(png);if(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4))!=64)throw new IOException("Java 正版皮肤上传需要 64×64 或 64×32 PNG；高清图可以本地预览。");
        if(account.Expires<=DateTimeOffset.UtcNow.AddMinutes(2))account=await RefreshAsync(account,ct);
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.minecraftservices.com/minecraft/profile/skins");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",account.AccessToken);using var form=new MultipartFormDataContent();form.Add(new StringContent(slim?"slim":"classic"),"variant");var file=new ByteArrayContent(png);file.Headers.ContentType=new("image/png");form.Add(file,"file","skin.png");request.Content=form;
        using var response=await http.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new IOException("皮肤未能应用（HTTP "+(int)response.StatusCode+"）。原皮肤未在本地标记为已更换，请重新登录或稍后重试。");try{return await ProfileAsync(account,ct);}catch(Exception e)when(e is IOException or HttpRequestException){throw new IOException("官方已接受皮肤上传，但角色刷新尚未确认。请点击刷新皮肤查看结果。",e);}
    }
}
