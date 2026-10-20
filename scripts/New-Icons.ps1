param([string]$Version='preview')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$assets=Join-Path $root 'desktop/Assets';New-Item -ItemType Directory -Path $assets -Force|Out-Null
# Original geometric artwork, generated from this MIT-licensed script.
$frames=@()
foreach($size in @(16,24,32,48,64,128,256)){
 $image=New-Object Drawing.Bitmap $size,$size
 $g=[Drawing.Graphics]::FromImage($image);$g.Clear([Drawing.Color]::FromArgb(24,28,24))
 $gold=New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(239,210,138))
 try{
  $g.FillRectangle($gold,[single]($size*.25),[single]($size*.18),[single]($size*.16),[single]($size*.64))
  $g.FillRectangle($gold,[single]($size*.25),[single]($size*.18),[single]($size*.5),[single]($size*.14))
  $g.FillRectangle($gold,[single]($size*.25),[single]($size*.44),[single]($size*.38),[single]($size*.13))
  if($size -eq 256){$image.Save((Join-Path $assets 'ForeverFlip.png'),[Drawing.Imaging.ImageFormat]::Png)}
  $memory=New-Object IO.MemoryStream;$image.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
  $frames+=,@{Size=$size;Bytes=$memory.ToArray()};$memory.Dispose()
 }finally{$gold.Dispose();$g.Dispose();$image.Dispose()}
}
$stream=[IO.File]::Create((Join-Path $assets 'ForeverFlip.ico'));$writer=New-Object IO.BinaryWriter $stream
try{
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count);$offset=6+16*$frames.Count
 foreach($frame in $frames){$d=if($frame.Size -eq 256){0}else{$frame.Size};$writer.Write([byte]$d);$writer.Write([byte]$d);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.Bytes.Length}
 foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
}finally{$writer.Dispose();$stream.Dispose()}
Copy-Item -LiteralPath (Join-Path $assets 'ForeverFlip.ico') -Destination (Join-Path $assets 'ForeverFlip-Setup.ico') -Force
$art=Join-Path $root 'artifacts/installer-art';New-Item -ItemType Directory -Path $art -Force|Out-Null
$image=New-Object Drawing.Bitmap 700,300;$g=[Drawing.Graphics]::FromImage($image)
$brush=New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(239,210,138));$font=New-Object Drawing.Font 'Segoe UI',26
try{$g.Clear([Drawing.Color]::FromArgb(24,28,24));$g.DrawString("ForeverFlip Desktop`nSETUP - $Version",$font,$brush,35,65);$image.Save((Join-Path $art 'Setup.png'),[Drawing.Imaging.ImageFormat]::Png)}finally{$font.Dispose();$brush.Dispose();$g.Dispose();$image.Dispose()}
