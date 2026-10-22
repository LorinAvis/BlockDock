using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlockDock;
public sealed record TransferUpdate(string File,string Destination,long Bytes,long Total,double Speed,string State);
public static class DownloadTelemetry
{
    public static readonly AsyncLocal<Action<TransferUpdate>?> Current=new();
    public static void Report(string file,string destination,long bytes,long total,double speed,string state)=>Current.Value?.Invoke(new(file,destination,bytes,total,speed,state));
}
public sealed class DownloadTransfer : INotifyPropertyChanged
{
    public string File{get;}public string Destination{get;}public long Bytes{get;private set;}public long Total{get;private set;}public double Speed{get;private set;}public string State{get;private set;}="等待下载";
    public DownloadTransfer(string file,string destination){File=file;Destination=destination;}
    public double Percent=>Total>0?Math.Clamp(100.0*Bytes/Total,0,100):0;
    public string Summary=>State+" · "+(Total>0?$"{Bytes/1048576.0:0.0} / {Total/1048576.0:0.0} MB":$"{Bytes/1048576.0:0.0} MB")+(Speed>0?" · "+SpeedText(Speed):"");
    public static string SpeedText(double speed)=>speed>=1048576?$"{speed/1048576:0.0} MB/s":$"{speed/1024:0.0} KB/s";
    public void Update(TransferUpdate value){Bytes=value.Bytes;Total=value.Total;Speed=value.Speed;State=value.State;PropertyChanged?.Invoke(this,new(""));}
    public event PropertyChangedEventHandler? PropertyChanged;
}
