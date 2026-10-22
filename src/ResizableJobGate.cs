namespace BlockDock;
public sealed class ResizableJobGate
{
    readonly object sync=new();readonly SemaphoreSlim signal=new(0);int running,limit=3;
    public int Limit{get{lock(sync)return limit;}set{lock(sync)limit=Math.Clamp(value,1,5);signal.Release(5);}}
    public async Task WaitAsync(CancellationToken ct){while(true){ct.ThrowIfCancellationRequested();lock(sync){if(running<limit){running++;return;}}await signal.WaitAsync(ct);}}
    public void Release(){lock(sync){if(running>0)running--;}signal.Release();}
}
