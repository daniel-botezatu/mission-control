$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBitmap = New-Object System.Drawing.Bitmap 64,64
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
try {
    $taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $taskGraphics.Clear([System.Drawing.Color]::FromArgb(35,59,50))
    $taskPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(192,221,171)),3
    $taskPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $taskPen.StartCap = $taskPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $taskGraphics.DrawPolygon($taskPen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 13,24),(New-Object System.Drawing.Point 32,14),(New-Object System.Drawing.Point 51,24),(New-Object System.Drawing.Point 32,34)))
    $taskGraphics.DrawLines($taskPen,[System.Drawing.Point[]]@((New-Object System.Drawing.Point 13,33),(New-Object System.Drawing.Point 32,43),(New-Object System.Drawing.Point 51,33)))
    $taskGraphics.DrawLines($taskPen,[System.Drawing.Point[]]@((New-Object System.Drawing.Point 13,42),(New-Object System.Drawing.Point 32,52),(New-Object System.Drawing.Point 51,42)))
    $taskStream = New-Object System.IO.MemoryStream
    $taskBitmap.Save($taskStream,[System.Drawing.Imaging.ImageFormat]::Png)
    $taskPng = $taskStream.ToArray()
    $taskFile = [System.IO.File]::Create((Join-Path $taskRoot 'src-tauri/icons/icon.ico'))
    $taskWriter = New-Object System.IO.BinaryWriter $taskFile
    try {
        $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]1)
        $taskWriter.Write([byte]64); $taskWriter.Write([byte]64); $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32); $taskWriter.Write([uint32]$taskPng.Length); $taskWriter.Write([uint32]22)
        $taskWriter.Write($taskPng)
    } finally { $taskWriter.Dispose(); $taskFile.Dispose(); $taskStream.Dispose(); $taskPen.Dispose() }
} finally { $taskGraphics.Dispose(); $taskBitmap.Dispose() }
