# 메이플 스샷 정리.exe 만들기 (윈도우에 들어 있는 C# 컴파일러 사용)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path (Split-Path -Parent $here) '메이플 스샷 정리.exe'
$ico = Join-Path $here 'icon.ico'

# 아이콘 파일 만들기 (청자색 동그라미 안 렌즈, 32×32 PNG를 담은 ICO)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(74, 140, 128))), 1, 1, 30, 30)
$g.FillEllipse([System.Drawing.Brushes]::White, 9, 9, 14, 14)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 80, 72))), 12, 12, 8, 8)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]32); $w.Write([byte]32); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
$w.Write($png)
$w.Close()

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32icon:$ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    (Join-Path $here 'MapleShotSorter.cs')
if ($LASTEXITCODE -ne 0) { throw '만들기 실패' }
Remove-Item $ico
"만들었어요: $out"
