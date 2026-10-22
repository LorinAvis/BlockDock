using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BlockDock;
public partial class MainWindow
{
    readonly Slider memorySlider=new(){Minimum=512,Maximum=32768,TickFrequency=256,IsSnapToTickEnabled=true,Margin=new(0,10,0,8)};
    readonly TextBox memoryAmount=new(){Width=100};readonly TextBlock memoryHint=new(){TextWrapping=TextWrapping.Wrap};readonly StackPanel manualMemory=new();bool updatingMemory;
    UIElement BuildMemoryConfiguration()
    {
        memoryBox.ItemsSource=new[]{"自动分配（推荐）","手动设置"};memoryBox.SelectedIndex=settings.MemoryMode=="Manual"?1:0;memorySlider.Value=Math.Clamp(settings.MemoryMb,512,32768);memoryAmount.Text=((int)memorySlider.Value).ToString(CultureInfo.InvariantCulture);manualMemory.Children.Add(H(Text("内存上限（MB）"),memoryAmount));manualMemory.Children.Add(memorySlider);
        memorySlider.ValueChanged+=(_,_)=>{if(updatingMemory)return;updatingMemory=true;memoryAmount.Text=((int)memorySlider.Value).ToString(CultureInfo.InvariantCulture);updatingMemory=false;UpdateMemoryHint();};
        memoryAmount.TextChanged+=(_,_)=>{if(updatingMemory)return;if(int.TryParse(memoryAmount.Text,out int value)&&value is >=512 and <=32768){updatingMemory=true;memorySlider.Value=value;updatingMemory=false;}UpdateMemoryHint();};
        memoryBox.SelectionChanged+=(_,_)=>UpdateMemoryHint();UpdateMemoryHint();return V(Text("游戏内存",15,true),memoryBox,Gap(8),manualMemory,memoryHint);
    }
    void UpdateMemoryHint()
    {
        manualMemory.Visibility=memoryBox.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;try{var memory=MemoryAllocation.Snapshot();var game=homeGame.SelectedItem as GameInstance;bool manual=memoryBox.SelectedIndex==1;int amount=manual&&int.TryParse(memoryAmount.Text,out int mb)?Math.Clamp(mb,512,32768):MemoryAllocation.Resolve(new Settings{JavaRoot=settings.JavaRoot,MemoryMode="Auto"},game,memory);memoryHint.Text=(manual?"手动上限：":"当前建议：")+MemoryAllocation.Format(amount)+$"。系统共 {memory.TotalMb/1024.0:0.#} GB，当前可用 {memory.AvailableMb/1024.0:0.#} GB。"+(manual?"": "按电脑总内存、游戏版本和模组规模估算，启动时重新计算。")+(amount+1024>memory.AvailableMb?" 当前空闲内存偏低，建议先关闭其他程序；分配上限会保留。":"");}catch(IOException e){memoryHint.Text=e.Message;}
    }
    void SaveMemoryConfiguration(){if(memoryBox.SelectedIndex==1&&(!int.TryParse(memoryAmount.Text,out int value)||value is <512 or >32768))throw new IOException("请填写 512–32768 之间的内存大小，单位 MB。");settings.MemoryMode=memoryBox.SelectedIndex==1?"Manual":"Auto";if(int.TryParse(memoryAmount.Text,out int mb))settings.MemoryMb=Math.Clamp(mb,512,32768);}
}
