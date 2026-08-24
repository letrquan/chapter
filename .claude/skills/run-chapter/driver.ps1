<#
.SYNOPSIS
  Drives the Chapter desktop app (WPF + WebView2) from the command line.

.DESCRIPTION
  Chapter has no automation surface: the UI is a WebView2 control, and nothing
  outside the process can reach its DOM. So this driver works at the Win32 level
  --- capture with PrintWindow, click with mouse_event, type with SendInput.

  The workflow is: `shot` -> look at the PNG -> `click` the pixel you saw.
  The captured bitmap is exactly the window rect, so coordinates you read off
  the screenshot are the coordinates you pass to `click`. No scaling.

  Every command re-finds the process and re-reads the window rect. That is not
  defensive coding: each agent tool call is a fresh PowerShell process, so
  nothing persists, and the user may have moved, resized or minimised the window
  between two of your calls.

.EXAMPLE
  ./driver.ps1 launch -Repo I:\MyProject\02-AI-ML-Projects\chapter
  ./driver.ps1 shot -Out shot.png
  ./driver.ps1 click -X 134 -Y 230
  ./driver.ps1 key -Combo ctrl+p
  ./driver.ps1 type -Text protocol
  ./driver.ps1 quit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('launch', 'shot', 'click', 'key', 'type', 'rect', 'status', 'quit')]
    [string]$Command,

    [string]$Repo,
    [string]$Out,
    [int]$X,
    [int]$Y,
    [string]$Combo,
    [string]$Text,
    [int]$WaitSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort vk, scan; public uint flags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {
        public uint type; public KEYBDINPUT ki; public int pad1, pad2;
    }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern ushort MapVirtualKey(uint code, uint type);
}
"@ -ReferencedAssemblies System.Runtime.InteropServices, System.Runtime

$UnitRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$Exe = Join-Path $UnitRoot 'src\Chapter.App\bin\Debug\net10.0-windows\Chapter.App.exe'

# ---------------------------------------------------------------- process

function Get-App {
    # MainWindowHandle is cached on the Process object, so a stale object reports
    # 0 forever. Always take a fresh snapshot.
    $p = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like 'Chapter.App*' -and $_.MainWindowHandle -ne 0 })
    if ($p.Count -eq 0) { return $null }
    return $p[0]
}

function Get-AppOrThrow {
    $p = Get-App
    if (-not $p) { throw "Chapter is not running. Run: ./driver.ps1 launch" }
    return $p
}

function Assert-Ready([System.Diagnostics.Process]$p) {
    <#
      A minimised window sits at (-32000,-32000) and PrintWindow returns a blank
      frame from it --- indistinguishable from "the app failed to render" unless
      you check. Restore instead of guessing.
    #>
    $h = $p.MainWindowHandle
    if ([Native]::IsIconic($h)) {
        [void][Native]::ShowWindow($h, 9)   # SW_RESTORE
        Start-Sleep -Milliseconds 800
    }

    <#
      SetForegroundWindow alone is not enough. Windows' foreground lock lets a
      background process (which is what an agent's shell is) fail this call
      silently --- it returns and the window never activates. Input then goes to
      whatever really is in front, so click/key appear to succeed while doing
      nothing at all. Attaching to the current foreground thread's input queue
      lifts the restriction; verify afterwards rather than trusting it.
    #>
    if ([Native]::GetForegroundWindow() -ne $h) {
        $fg = [Native]::GetForegroundWindow()
        $me = [Native]::GetCurrentThreadId()
        $other = [Native]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
        $attached = ($other -ne 0 -and $other -ne $me) -and [Native]::AttachThreadInput($me, $other, $true)
        try {
            [void][Native]::ShowWindow($h, 9)
            [void][Native]::BringWindowToTop($h)
            [void][Native]::SetForegroundWindow($h)
        } finally {
            if ($attached) { [void][Native]::AttachThreadInput($me, $other, $false) }
        }
        Start-Sleep -Milliseconds 500
        if ([Native]::GetForegroundWindow() -ne $h) {
            throw "Could not bring Chapter to the foreground. Input would have gone to another window, so this command did nothing. Click the Chapter window once and retry."
        }
    }
    Start-Sleep -Milliseconds 400
    return $h
}

function Get-Rect([IntPtr]$h) {
    $r = New-Object Native+RECT
    [void][Native]::GetWindowRect($h, [ref]$r)
    if ($r.L -le -30000) { throw "Window is minimised (rect $($r.L),$($r.T)). Could not restore." }
    return $r
}

# ---------------------------------------------------------------- capture

function Save-Shot([IntPtr]$h, [string]$path) {
    $r = Get-Rect $h
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # Flag 2 is PW_RENDERFULLCONTENT. Without it PrintWindow captures the WPF
    # chrome and leaves the whole WebView2 client area white --- the single most
    # misleading failure mode here, because it looks like the UI never loaded.
    [void][Native]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return "$path ($($w)x$($ht))"
}

# ---------------------------------------------------------------- keyboard

$VK = @{
    'ctrl' = 0x11; 'shift' = 0x10; 'alt' = 0x12; 'enter' = 0x0D; 'escape' = 0x1B
    'esc' = 0x1B; 'tab' = 0x09; 'up' = 0x26; 'down' = 0x28; 'left' = 0x25; 'right' = 0x27
    'pgup' = 0x21; 'pgdn' = 0x22; 'backspace' = 0x08
    # OEM keys, by the character on a US layout, for bindings that are punctuation.
    'slash' = 0xBF; 'comma' = 0xBC; 'period' = 0xBE; 'semicolon' = 0xBA
}
# F1..F12 are contiguous from VK_F1.
1..12 | ForEach-Object { $VK["f$_"] = 0x6F + $_ }
1..9 | ForEach-Object { $VK["$_"] = 0x30 + $_ }
[char[]]'abcdefghijklmnopqrstuvwxyz' | ForEach-Object { $VK["$_"] = [int][char]([string]$_).ToUpper() }

function Send-Vk([ushort]$vk, [bool]$up) {
    $i = New-Object Native+INPUT
    $i.type = 1
    $k = New-Object Native+KEYBDINPUT
    $k.vk = $vk
    # Chromium's input pipeline wants a scan code. Virtual-key-only events ---
    # which is what WScript.Shell SendKeys produces --- are silently dropped by
    # the WebView2 render widget. This is why SendKeys does nothing here.
    $k.scan = [Native]::MapVirtualKey($vk, 0)
    $k.flags = if ($up) { 0x0002 } else { 0x0000 }
    $i.ki = $k
    [void][Native]::SendInput(1, [Native+INPUT[]]@($i), [Runtime.InteropServices.Marshal]::SizeOf([type][Native+INPUT]))
    Start-Sleep -Milliseconds 35
}

function Send-Combo([string]$combo) {
    $parts = $combo.ToLower().Split('+') | ForEach-Object { $_.Trim() }
    foreach ($p in $parts) { if (-not $VK.ContainsKey($p)) { throw "Unknown key '$p' in '$combo'" } }
    $codes = $parts | ForEach-Object { [ushort]$VK[$_] }
    foreach ($c in $codes) { Send-Vk $c $false }
    [array]::Reverse($codes)
    foreach ($c in $codes) { Send-Vk $c $true }
}

function Send-Text([string]$s) {
    foreach ($ch in $s.ToCharArray()) {
        foreach ($up in $false, $true) {
            $i = New-Object Native+INPUT
            $i.type = 1
            $k = New-Object Native+KEYBDINPUT
            $k.vk = 0
            $k.scan = [ushort][char]$ch
            # KEYEVENTF_UNICODE (0x0004): delivers the character directly rather
            # than depending on the keyboard layout.
            $k.flags = if ($up) { 0x0004 -bor 0x0002 } else { 0x0004 }
            $i.ki = $k
            [void][Native]::SendInput(1, [Native+INPUT[]]@($i), [Runtime.InteropServices.Marshal]::SizeOf([type][Native+INPUT]))
        }
        Start-Sleep -Milliseconds 30
    }
}

# ---------------------------------------------------------------- commands

switch ($Command) {

    'launch' {
        $existing = Get-App
        if ($existing) { "already running: PID=$($existing.Id)"; break }
        if (-not (Test-Path $Exe)) { throw "Not built: $Exe`nRun the build steps in SKILL.md first." }
        # -ArgumentList rejects an empty array, so the no-repo case has to omit
        # the parameter entirely rather than pass @().
        $p = if ($Repo) {
            Start-Process -FilePath $Exe -ArgumentList $Repo -PassThru
        } else {
            Start-Process -FilePath $Exe -PassThru
        }
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        while ((Get-Date) -lt $deadline) {
            $live = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
            if (-not $live) { throw "Chapter exited during startup (exit code $($p.ExitCode))." }
            if ($live.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 500
        }
        $live = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if (-not $live -or $live.MainWindowHandle -eq 0) { throw "No window after ${WaitSeconds}s." }
        # The window appears before WebView2 has painted; capturing immediately
        # yields a white frame.
        Start-Sleep -Seconds 7
        "PID=$($p.Id) window=$($live.MainWindowHandle)"
    }

    'status' {
        $p = Get-App
        if (-not $p) { "not running"; break }
        $r = New-Object Native+RECT
        [void][Native]::GetWindowRect($p.MainWindowHandle, [ref]$r)
        "PID=$($p.Id) minimised=$([Native]::IsIconic($p.MainWindowHandle)) rect=$($r.L),$($r.T) $($r.R-$r.L)x$($r.B-$r.T)"
    }

    'rect' {
        $p = Get-AppOrThrow
        $r = Get-Rect (Assert-Ready $p)
        "origin=$($r.L),$($r.T) size=$($r.R-$r.L)x$($r.B-$r.T)"
    }

    'shot' {
        if (-not $Out) { throw "-Out <path.png> is required" }
        $p = Get-AppOrThrow
        Save-Shot (Assert-Ready $p) $Out
    }

    'click' {
        $p = Get-AppOrThrow
        $h = Assert-Ready $p
        $r = Get-Rect $h        # re-read: the window may have moved since your last shot
        $w = $r.R - $r.L; $ht = $r.B - $r.T
        if ($X -lt 0 -or $Y -lt 0 -or $X -ge $w -or $Y -ge $ht) {
            throw "Point $X,$Y is outside the window (${w}x${ht}). A click that lands outside goes to whatever is behind Chapter."
        }
        [void][Native]::SetCursorPos(($r.L + $X), ($r.T + $Y))
        Start-Sleep -Milliseconds 250
        [Native]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
        [Native]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
        Start-Sleep -Milliseconds 900
        "clicked $X,$Y (screen $($r.L + $X),$($r.T + $Y))"
    }

    'key' {
        if (-not $Combo) { throw "-Combo <e.g. ctrl+p> is required" }
        $p = Get-AppOrThrow
        [void](Assert-Ready $p)
        Send-Combo $Combo
        Start-Sleep -Milliseconds 700
        "sent $Combo"
    }

    'type' {
        if (-not $Text) { throw "-Text <string> is required" }
        $p = Get-AppOrThrow
        [void](Assert-Ready $p)
        Send-Text $Text
        Start-Sleep -Milliseconds 600
        "typed '$Text'"
    }

    'quit' {
        $p = Get-App
        if (-not $p) { "not running"; break }
        $id = $p.Id
        [void]$p.CloseMainWindow()
        Start-Sleep -Seconds 3
        if (Get-Process -Id $id -ErrorAction SilentlyContinue) { Stop-Process -Id $id -Force }
        "stopped PID=$id"
    }
}
