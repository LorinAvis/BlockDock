# Convert the transparent high-resolution source PNG into Windows icon sizes.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$taskDrawing=[Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new((Join-Path $PSScriptRoot 'Assets/BlockDock.png')))
$taskSizes=@(16,24,32,48,64,128,256)
$taskImages=@()
foreach($taskSize in $taskSizes){
 $taskVisual=[Windows.Media.DrawingVisual]::new();$taskContext=$taskVisual.RenderOpen();$taskContext.DrawImage($taskDrawing,[Windows.Rect]::new(0,0,$taskSize,$taskSize));$taskContext.Close()
 $taskBitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize,$taskSize,96,96,[Windows.Media.PixelFormats]::Pbgra32);$taskBitmap.Render($taskVisual)
 $taskEncoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap));$taskStream=[IO.MemoryStream]::new();$taskEncoder.Save($taskStream);$taskImages+=,@($taskStream.ToArray());$taskStream.Dispose()
}
$taskOutput=[IO.File]::Create((Join-Path $PSScriptRoot 'Assets/BlockDock.ico'));$taskWriter=[IO.BinaryWriter]::new($taskOutput)
try{
 $taskWriter.Write([uint16]0);$taskWriter.Write([uint16]1);$taskWriter.Write([uint16]$taskSizes.Count);$taskOffset=6+16*$taskSizes.Count
 for($taskIndex=0;$taskIndex -lt $taskSizes.Count;$taskIndex++){$taskDimension=if($taskSizes[$taskIndex] -eq 256){0}else{$taskSizes[$taskIndex]};$taskWriter.Write([byte]$taskDimension);$taskWriter.Write([byte]$taskDimension);$taskWriter.Write([byte]0);$taskWriter.Write([byte]0);$taskWriter.Write([uint16]1);$taskWriter.Write([uint16]32);$taskWriter.Write([uint32]$taskImages[$taskIndex].Length);$taskWriter.Write([uint32]$taskOffset);$taskOffset+=$taskImages[$taskIndex].Length}
 foreach($taskImage in $taskImages){$taskWriter.Write([byte[]]$taskImage)}
}finally{$taskWriter.Dispose()}
Write-Output 'Generated Assets/BlockDock.ico from the original-reference high-resolution PNG.'
