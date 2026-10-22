using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BlockDock;
public sealed class LauncherJob : INotifyPropertyChanged
{
    public string Name{get;}public CancellationTokenSource Cancellation{get;}=new();
    public string Destination{get;set;}="";
    public string Category{get;set;}="game";
    public string Source{get;set;}="本机操作";
    public string IconUrl{get;set;}="";
    public System.Windows.Media.ImageSource Icon{get;set;}=InstanceIcons.Get("vanilla");
    public string Description{get;set;}="";
    public DateTimeOffset Created{get;}=DateTimeOffset.Now;
    public string TypeLabel=>Category switch{"mod"=>"Mod","modpack"=>"整合包","shader"=>"光影","resourcepack"=>"材质包",_=>"游戏本体"};
    public string Metadata=>TypeLabel+"  ·  "+Source;
    public string GroupName=>Finished?(State=="已完成"?"已完成":"已结束"):State=="排队中"?"等待中":"进行中";
    public int GroupOrder=>Finished?2:State=="排队中"?1:0;
    public double CurrentSpeed=>Finished?0:Files.Sum(x=>x.Speed);
    public string ProgressText=>$"{Percent:0}%";
    public string Summary=>Files.Count==0?Detail:$"{Files.Sum(x=>x.Bytes)/1048576.0:0.0} / {Files.Sum(x=>x.Total)/1048576.0:0.0} MB · {Speed}";
    public System.Collections.ObjectModel.ObservableCollection<DownloadTransfer> Files{get;}=new();
    public string Speed=>Finished?State:Files.Any(x=>x.Speed>0)?DownloadTransfer.SpeedText(Files.Sum(x=>x.Speed)):State=="进行中"?"正在准备 / 校验":"等待执行";
    public string FileSummary=>$"{Files.Count(x=>x.State is "已完成" or "已存在")} / {Files.Count} 个文件";
    public void Transfer(TransferUpdate update){var file=Files.FirstOrDefault(x=>x.Destination==update.Destination);if(file==null){file=new(update.File,update.Destination);Files.Add(file);}file.Update(update);Changed(nameof(Speed));Changed(nameof(FileSummary));Changed(nameof(Summary));}
    public void StopTransfers(string result){foreach(var file in Files.Where(x=>x.State is "下载中" or "正在连接"))file.Update(new(file.File,file.Destination,file.Bytes,file.Total,0,result));Changed(nameof(Speed));Changed(nameof(FileSummary));}
    string state="排队中",detail="等待执行…";double percent;bool indeterminate=true,finished,canInterrupt=true;
    public LauncherJob(string name){Name=name;CancelCommand=new JobCommand(()=>Cancel());}
    public string State{get=>state;set{state=value;Changed();Changed(nameof(CanCancel));Changed(nameof(Speed));Changed(nameof(GroupName));Changed(nameof(GroupOrder));Changed(nameof(Summary));}}
    public string Detail{get=>detail;set{detail=value;Changed();Changed(nameof(Summary));}}
    public double Percent{get=>percent;set{percent=Math.Clamp(value,0,100);Changed();Changed(nameof(ProgressText));}}
    public bool Indeterminate{get=>indeterminate;set{indeterminate=value;Changed();}}
    public bool Finished{get=>finished;set{finished=value;Changed();Changed(nameof(CanCancel));Changed(nameof(Speed));Changed(nameof(GroupName));Changed(nameof(GroupOrder));Changed(nameof(Summary));}}
    public bool CanInterrupt{get=>canInterrupt;set{canInterrupt=value;Changed();Changed(nameof(CanCancel));}}
    public bool CanCancel=>!Finished&&CanInterrupt&&!Cancellation.IsCancellationRequested;
    public ICommand CancelCommand{get;}
    public void Cancel(){if(!CanCancel)return;Cancellation.Cancel();State="正在取消…";}
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    sealed class JobCommand(Action run):ICommand{public bool CanExecute(object? p)=>true;public void Execute(object? p)=>run();public event EventHandler? CanExecuteChanged{add{}remove{}}}
}
