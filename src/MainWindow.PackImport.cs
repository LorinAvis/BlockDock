using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BlockDock;

public partial class MainWindow
{
    bool packDialogOpen;
    (string File,string Name,string Hash)? pendingPackImport;
    async Task ImportPackAsync()
    {
        if(packDialogOpen)return;
        var picker=new OpenFileDialog{Title="导入整合包",Filter="整合包 ZIP / Modrinth|*.zip;*.mrpack|ZIP 整合包|*.zip|Modrinth 整合包|*.mrpack",CheckFileExists=true};
        if(picker.ShowDialog(this)==true)await ImportPackFileAsync(picker.FileName);
    }
    async Task ImportPackFileAsync(string file)
    {
        if(packDialogOpen)return;packDialogOpen=true;
        try
        {
            var dialog=new PackImportWindow(file,settings){Owner=this};if(dialog.ShowDialog()!=true)return;
            pendingPackImport=(file,dialog.InstanceName,dialog.PackageSha256);
            if(dialog.RequiresGameSource&&!EnsureSource("minecraft")){providerHint.Text="已选择整合包："+dialog.InstanceName+"。保存 Java 下载源后将继续导入。";return;}
        }
        catch(Exception e){pendingPackImport=null;Error(e);return;}
        finally{packDialogOpen=false;}
        await ContinuePackImportAsync();
    }
    async Task ContinuePackImportAsync()
    {
        if(pendingPackImport is not { } request)return;pendingPackImport=null;
        Navigate("tasks");GameInstance? imported=null;
        await RunAsync("导入整合包 · "+request.Name,async(p,ct)=>
        {
            using var service=new ResourceService(settings){Log=Log};imported=await service.ImportPackAsync(request.File,request.Name,p,ct,request.Hash);
            settings.LastGame=imported.Id;if(jobContext.Value is LauncherJob job)job.Destination=imported.GameDirectory;
        },destination:Path.Combine(settings.JavaRoot,"versions"));
        if(imported!=null)
        {
            homeGame.SelectedItem=games.FirstOrDefault(x=>x.Id==imported.Id);gameList.SelectedItem=homeGame.SelectedItem;
            editionBox.SelectedIndex=0;NotifyStatus("已导入 "+imported.Name+"，可在 Java 版中启动。");
        }
    }
    void ConfigurePackDrop()
    {
        AllowDrop=true;
        static string? Archive(System.Windows.IDataObject data)=>data.GetDataPresent(DataFormats.FileDrop)&&data.GetData(DataFormats.FileDrop) is string[] files&&files.Length==1&&Path.GetExtension(files[0]).ToLowerInvariant() is ".zip" or ".mrpack"?files[0]:null;
        PreviewDragOver+=(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=!packDialogOpen&&Archive(e.Data)!=null?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}};
        PreviewDrop+=async(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Handled=true;if(Archive(e.Data) is string file)await ImportPackFileAsync(file);else NotifyStatus("请一次拖入一个 .zip 或 .mrpack 整合包。");}};
    }
}
