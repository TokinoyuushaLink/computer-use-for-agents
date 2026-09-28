function computerUse {
  [CmdletBinding()]
  param(
    [Parameter(Position = 0)][string]$Cmd,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest,
    [string]$Window,      # window title substring
    [string]$Process,     # process name (no .exe, wildcards ok)
    [string]$Region,      # x,y,w,h in screen pixels
    [switch]$Screen,      # primary display
    [switch]$All,         # all displays (virtual desktop)
    [string]$Out,         # output image path (.png / .jpg)
    [int]$MaxSide = 1568, # downscale so the longer side <= this (0 = off)
    [switch]$Shot,        # after an action, capture again
    [int]$Wait = 400,     # ms to wait before that capture
    [ValidateSet('left', 'right', 'middle')][string]$Button = 'left',
    [switch]$Double,      # double click
    [switch]$Enter,       # press Enter after typing
    [switch]$Abs,         # coordinates are raw screen pixels
    [string]$Name,        # UIA: control name (exact match first, then substring)
    [string]$Id,          # UIA: AutomationId
    [string]$Type,        # UIA: control type(s), comma separated, e.g. button,edit
    [int]$Index,          # UIA: pick the nth match (1-based)
    [int]$Limit,          # UIA: max items for ui (150) / max chars for text (4000)
    [switch]$Raw,         # UIA: include non-interactive elements (text, panes...)
    [switch]$Invoke,      # UIA: use the control's pattern instead of a mouse click
    [switch]$Keys,        # UIA: set = type the text instead of using ValuePattern
    [Alias('h')][switch]$Help
  )

  $helpText = @'
computerUse - desktop control for CLI agents. Every call prints ONE line of JSON.

Usage:
  computerUse shot [-screen | -all | -window <title> | -process <name> | -region x,y,w,h]
  computerUse windows [-window <title>] [-process <name>]
  computerUse focus  -window <title> | -process <name>
  computerUse click  [x y] [-button left|right|middle] [-double]
  computerUse move   x y
  computerUse drag   x1 y1 x2 y2
  computerUse scroll up|down [n] [x y]
  computerUse type   <text> [-enter]
  computerUse key    <chord> [<chord> ...]
  computerUse ui     [-name <text>] [-type button,edit] [-raw] [-limit n]
  computerUse click  -name <text> | -id <automationId> [-type t] [-index n] [-invoke]
  computerUse set    -name <text> <value> [-enter] [-keys]
  computerUse text   -name <text> | -id <id> | -type document [-limit n]
  computerUse type   <text> -name <text> | -id <id>       focus that control, then type

UI Automation (the control tree; more reliable than coordinates for standard controls):
  ui lists visible interactive controls as {n,name,type,id,c:[x,y]}; c is the center in
  raw screen pixels (use with: click x y -abs). Options: -name/-type/-id filter, -raw adds
  non-interactive elements, -limit caps the count (default 150).
  -name matches exactly first, then as a case-insensitive substring. If several controls
  match, the call fails and lists candidates: refine with -type / -id, or pick the nth
  match with -index.
  click -name uses a real mouse click by default; -invoke uses the control's UIA pattern
  (works for controls the mouse cannot reach, but may block if it opens a modal dialog).
  set uses ValuePattern when available, otherwise focuses, selects all and types.
  Target window: -window / -process, else the last shot's window, else the foreground window.
  Custom-drawn UIs (games, ImGui/Vulkan, some Electron apps) expose no controls: use
  shot + coordinates for those.

Coordinates:
  x/y are pixels in the image of the most recent "computerUse shot". Its origin and scale
  are remembered, so downscaling and window position are handled for you.
  Use -abs to pass raw screen pixels instead.

Options:
  -shot          after an action, wait -wait ms and capture again (same target as the
                 last shot; add -screen to capture the whole screen instead)
  -wait <ms>     delay before that capture (default 400)
  -out <path>    output image path (.png / .jpg). Default: %TEMP%\desk1_<timestamp>.png
  -maxside <n>   downscale so the longer side is <= n (default 1568, 0 = off)
  -abs           coordinates are raw screen pixels
  -help, -h      show this help

Keys (for "computerUse key"): ctrl shift alt win enter tab esc space backspace delete insert
  home end pageup pagedown up down left right f1..f24, or a single character.
  Chords use +, e.g. ctrl+s  alt+f4  ctrl+shift+esc  (use "plus" / "minus" for + and -)

Exit codes: 0 ok | 1 bad arguments | 2 window or element not found | 3 failed
Errors are printed as {"ok":false,"error":"..."}.

Typical loop:
  computerUse shot -window "Notepad"       -> {"ok":true,"path":"...","width":..,"height":..}
  (view the image, pick a point)
  computerUse click 320 180 -shot          -> click, then a fresh screenshot in the same call
  computerUse type "hello" -enter -shot
  computerUse ui -window "Notepad"         -> {"ok":true,"items":[{"n":1,"name":"Save","type":"MenuItem",...}]}
  computerUse click -name "Save" -shot     -> click the control by name, then a fresh screenshot
'@

  if ($Help -or -not $Cmd) { $helpText; $global:LASTEXITCODE = [int](-not $Help); return }

  # ---- setup -------------------------------------------------------------
  try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch {}
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing

  if (-not ('Desk1' -as [type])) {
    $src = @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public class Desk1 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int size);
  [DllImport("user32.dll")] static extern short VkKeyScan(char c);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);

  public static void Init() { SetProcessDPIAware(); }

  // Visible frame bounds (excludes the invisible resize border on Win10/11)
  public static int[] Rect(IntPtr h) {
    RECT r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
    return new int[] { r.L, r.T, r.R, r.B };
  }

  // Restore if minimized and bring to the foreground
  public static void Focus(IntPtr h) {
    if (IsIconic(h)) ShowWindow(h, 9);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) {
      keybd_event(0x12, 0, 0, UIntPtr.Zero);
      keybd_event(0x12, 0, 2, UIntPtr.Zero);
      SetForegroundWindow(h);
    }
  }

  public static void Move(int x, int y) { SetCursorPos(x, y); }

  public static IntPtr Foreground() { return GetForegroundWindow(); }

  public static void Button(string b, bool down) {
    uint f;
    if (b == "right") f = down ? 0x8u : 0x10u;
    else if (b == "middle") f = down ? 0x20u : 0x40u;
    else f = down ? 0x2u : 0x4u;
    mouse_event(f, 0, 0, 0, UIntPtr.Zero);
  }

  public static void Wheel(int notches) { mouse_event(0x800, 0, 0, notches * 120, UIntPtr.Zero); }

  static void SendKey(ushort vk, ushort scan, uint flags) {
    INPUT[] a = new INPUT[1];
    a[0].type = 1;
    a[0].u.ki.wVk = vk;
    a[0].u.ki.wScan = scan;
    a[0].u.ki.dwFlags = flags;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }

  static bool Ext(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }
  static void KeyDown(ushort vk) { SendKey(vk, 0, Ext(vk) ? 1u : 0u); }
  static void KeyUp(ushort vk) { SendKey(vk, 0, (Ext(vk) ? 1u : 0u) | 2u); }

  public static void Chord(ushort[] vks) {
    foreach (ushort v in vks) { KeyDown(v); Thread.Sleep(10); }
    for (int i = vks.Length - 1; i >= 0; i--) { KeyUp(vks[i]); Thread.Sleep(10); }
  }

  // Unicode injection: works for any text (CJK included), independent of keyboard layout
  public static void TypeText(string s) {
    foreach (char c in s) {
      if (c == '\r') continue;
      if (c == '\n') { KeyDown(0x0D); KeyUp(0x0D); continue; }
      if (c == '\t') { KeyDown(0x09); KeyUp(0x09); continue; }
      SendKey(0, c, 4u);
      SendKey(0, c, 6u);
      Thread.Sleep(3);
    }
  }

  public static int Vk(char c) { short r = VkKeyScan(c); return r == -1 ? 0xFF : (r & 0xFF); }
}
'@
    # Cache the compiled helper (Windows PowerShell 5.1) so later calls start fast
    $dll = Join-Path $env:TEMP 'desk1_helper_v1.dll'
    try {
      if (-not (Test-Path $dll)) { Add-Type -TypeDefinition $src -OutputAssembly $dll }
      Add-Type -Path $dll
    } catch {
      Add-Type -TypeDefinition $src
    }
  }
  [Desk1]::Init()

  $stateFile = Join-Path $env:TEMP 'desk1_state.json'

  $KeyMap = @{}
  'ctrl:17 control:17 shift:16 alt:18 win:91 enter:13 return:13 tab:9 esc:27 escape:27 space:32 backspace:8 delete:46 del:46 insert:45 home:36 end:35 pageup:33 pgup:33 pagedown:34 pgdn:34 left:37 up:38 right:39 down:40 plus:187 minus:189 capslock:20' -split ' ' | ForEach-Object {
    $kn, $kv = $_ -split ':'
    $KeyMap[$kn] = [uint16]$kv
  }

  # ---- helpers -----------------------------------------------------------
  function Fail([int]$code, [string]$msg) {
    $global:LASTEXITCODE = $code
    ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = $msg }) -Compress
  }

  function Ok($o) {
    $global:LASTEXITCODE = 0
    $r = [ordered]@{ ok = $true }
    foreach ($k in $o.Keys) { $r[$k] = $o[$k] }
    ConvertTo-Json -InputObject $r -Compress -Depth 5
  }

  function Load-State {
    if (Test-Path $stateFile) {
      try { Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
    }
  }

  # All main windows matching -process / -window, non-minimized first
  function Find-Window {
    $c = @(Get-Process | Where-Object { $_.MainWindowHandle -ne 0 })
    if ($Process) {
      $pn = $Process -replace '\.exe$', ''
      $c = @($c | Where-Object { $_.ProcessName -like $pn })
    }
    if ($Window) { $c = @($c | Where-Object { $_.MainWindowTitle -like "*$Window*" }) }
    $c | Sort-Object { [Desk1]::IsIconic($_.MainWindowHandle) }
  }

  # Decide what to capture. With -UseLast, fall back to the previous shot's target.
  function Resolve-Target([switch]$UseLast) {
    $modes = @(([bool]($Window -or $Process)), [bool]$Region, $All.IsPresent, $Screen.IsPresent) | Where-Object { $_ }
    if (@($modes).Count -gt 1) { throw 'E1|use only one of: -window/-process, -region, -all, -screen' }
    if ($Window -or $Process) {
      $w1 = Find-Window | Select-Object -First 1
      if (-not $w1) { throw "E2|no window matches (process='$Process' window='$Window')" }
      return @{ mode = 'window'; hwnd = $w1.MainWindowHandle.ToInt64(); title = $w1.MainWindowTitle }
    }
    if ($Region) {
      $n = @($Region -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
      if ($n.Count -ne 4) { throw 'E1|-region needs x,y,w,h' }
      return @{ mode = 'region'; rect = $n }
    }
    if ($All) { return @{ mode = 'all' } }
    if ($Screen) { return @{ mode = 'screen' } }
    if ($UseLast) {
      $s = Load-State
      if ($s) {
        if ($s.mode -eq 'window' -and -not [Desk1]::IsWindow([IntPtr][int64]$s.hwnd)) { return @{ mode = 'screen' } }
        return @{ mode = $s.mode; hwnd = [int64]$s.hwnd; title = $s.title; rect = $s.rect }
      }
    }
    return @{ mode = 'screen' }
  }

  # Capture the target, save the image and remember origin/scale for later click coordinates
  function Do-Shot($t, [bool]$focus) {
    switch ($t.mode) {
      'window' {
        $hw = [IntPtr][int64]$t.hwnd
        if (-not [Desk1]::IsWindow($hw)) { throw 'E2|window no longer exists' }
        if ($focus) { [Desk1]::Focus($hw); Start-Sleep -Milliseconds 200 }
        $rc = [Desk1]::Rect($hw)
        $x = $rc[0]; $y = $rc[1]; $w = $rc[2] - $rc[0]; $ht = $rc[3] - $rc[1]
      }
      'region' { $x, $y, $w, $ht = [int[]]$t.rect }
      'all' {
        $bd = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $x = $bd.X; $y = $bd.Y; $w = $bd.Width; $ht = $bd.Height
      }
      default {
        $bd = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $x = $bd.X; $y = $bd.Y; $w = $bd.Width; $ht = $bd.Height
      }
    }
    if ($w -le 0 -or $ht -le 0) { throw "E1|invalid capture size ${w}x${ht}" }

    if ($Out) { $path = $Out }
    else {
      $path = Join-Path $env:TEMP ('desk1_{0:yyyyMMdd_HHmmss_fff}.png' -f (Get-Date))
      # housekeeping: drop old default-named shots
      Get-ChildItem $env:TEMP -Filter 'desk1_*.png' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-6) } |
        Remove-Item -ErrorAction SilentlyContinue
    }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
    $dir = Split-Path $path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()

    if ($MaxSide -gt 0 -and [Math]::Max($w, $ht) -gt $MaxSide) {
      $k = $MaxSide / [Math]::Max($w, $ht)
      $nw = [int][Math]::Round($w * $k); $nh = [int][Math]::Round($ht * $k)
      $small = New-Object System.Drawing.Bitmap $nw, $nh
      $g2 = [System.Drawing.Graphics]::FromImage($small)
      $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g2.DrawImage($bmp, 0, 0, $nw, $nh)
      $g2.Dispose(); $bmp.Dispose(); $bmp = $small
    }
    $iw = $bmp.Width; $ih = $bmp.Height
    $scale = $iw / $w

    $fmt = if ($path -match '\.jpe?g$') { [System.Drawing.Imaging.ImageFormat]::Jpeg } else { [System.Drawing.Imaging.ImageFormat]::Png }
    $bmp.Save($path, $fmt)
    $bmp.Dispose()

    $st = @{ mode = $t.mode; hwnd = [int64]$t.hwnd; title = $t.title; rect = $t.rect; ox = $x; oy = $y; scale = $scale; iw = $iw; ih = $ih }
    $st | ConvertTo-Json -Compress | Set-Content -Path $stateFile -Encoding UTF8

    return [ordered]@{ path = $path; width = $iw; height = $ih; scale = [Math]::Round($scale, 4); origin = @($x, $y); target = $t.mode; title = $t.title }
  }

  # Image coordinates (from the last shot) -> screen coordinates
  function Map-Point($a, $b) {
    try { $ix = [double]$a; $iy = [double]$b } catch { throw "E1|bad coordinate: $a $b" }
    $s = if ($Abs) { $null } else { Load-State }
    if (-not $s) { return @([int][Math]::Round($ix), [int][Math]::Round($iy)) }
    if ($ix -lt 0 -or $iy -lt 0 -or $ix -gt $s.iw -or $iy -gt $s.ih) {
      throw "E1|point ($ix,$iy) is outside the last shot ($($s.iw)x$($s.ih)); take a new shot or use -abs"
    }
    $ox = $s.ox; $oy = $s.oy
    if ($s.mode -eq 'window' -and [Desk1]::IsWindow([IntPtr][int64]$s.hwnd)) {
      $rc = [Desk1]::Rect([IntPtr][int64]$s.hwnd)   # follow the window if it moved
      $ox = $rc[0]; $oy = $rc[1]
    }
    return @([int][Math]::Round($ox + $ix / $s.scale), [int][Math]::Round($oy + $iy / $s.scale))
  }

  function Key-Vk([string]$n) {
    $k = $n.Trim().ToLower()
    if ($KeyMap.ContainsKey($k)) { return [uint16]$KeyMap[$k] }
    if ($k -match '^f([1-9]|1[0-9]|2[0-4])$') { return [uint16](0x6F + [int]$Matches[1]) }
    if ($k.Length -eq 1) {
      $v = [Desk1]::Vk([char]$k[0])
      if ($v -ne 0xFF) { return [uint16]$v }
    }
    throw "E1|unknown key: $n"
  }

  # Finish an action: optionally re-capture, then print the JSON result
  function Finish($r) {
    if ($Shot) {
      Start-Sleep -Milliseconds $Wait
      $r['shot'] = Do-Shot (Resolve-Target -UseLast) $false
    }
    Ok $r
  }

  # ---- UI Automation helpers ---------------------------------------------
  function Init-Uia {
    if (-not ('System.Windows.Automation.AutomationElement' -as [type])) {
      try { Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes }
      catch { throw 'E3|UI Automation assemblies not available (use Windows PowerShell 5.1)' }
    }
  }

  # Window whose control tree we search: -window/-process, else the last shot's window, else the foreground window
  function Resolve-UiTop {
    Init-Uia
    $hwnd = [int64]0
    $title = ''
    if ($Window -or $Process) {
      $w1 = Find-Window | Select-Object -First 1
      if (-not $w1) { throw "E2|no window matches (process='$Process' window='$Window')" }
      $hwnd = $w1.MainWindowHandle.ToInt64(); $title = $w1.MainWindowTitle
    } else {
      $s = Load-State
      if ($s -and $s.mode -eq 'window' -and [Desk1]::IsWindow([IntPtr][int64]$s.hwnd)) { $hwnd = [int64]$s.hwnd; $title = [string]$s.title }
      else { $hwnd = [Desk1]::Foreground().ToInt64() }
    }
    if ($hwnd -eq 0) { throw 'E2|no target window (use -window or -process)' }
    if ([Desk1]::IsIconic([IntPtr]$hwnd)) { [Desk1]::Focus([IntPtr]$hwnd); Start-Sleep -Milliseconds 300 }
    $el = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$hwnd)
    if (-not $title) { $title = [string]$el.Current.Name }
    return @{ hwnd = $hwnd; el = $el; title = $title }
  }

  # Collect on-screen controls under $top (one cross-process query, properties cached)
  function Get-UiItems($top, [bool]$raw) {
    $AE = [System.Windows.Automation.AutomationElement]
    $CT = [System.Windows.Automation.ControlType]
    $off = [System.Windows.Automation.PropertyCondition]::new($AE::IsOffscreenProperty, $false)
    if ($raw) { $cond = $off }
    else {
      $ts = [System.Windows.Automation.Condition[]]@(
        foreach ($kind in @($CT::Button, $CT::CheckBox, $CT::ComboBox, $CT::Edit, $CT::Hyperlink, $CT::ListItem,
                          $CT::MenuItem, $CT::RadioButton, $CT::TabItem, $CT::TreeItem, $CT::Slider, $CT::Spinner,
                          $CT::SplitButton, $CT::DataItem, $CT::Document)) {
          [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $kind)
        })
      $cond = [System.Windows.Automation.AndCondition]::new($off, [System.Windows.Automation.OrCondition]::new($ts))
    }

    $cr = [System.Windows.Automation.CacheRequest]::new()
    foreach ($pp in @($AE::NameProperty, $AE::ControlTypeProperty, $AE::AutomationIdProperty,
                      $AE::BoundingRectangleProperty, $AE::IsEnabledProperty,
                      [System.Windows.Automation.ValuePattern]::ValueProperty,
                      [System.Windows.Automation.TogglePattern]::ToggleStateProperty,
                      [System.Windows.Automation.SelectionItemPattern]::IsSelectedProperty)) { $cr.Add($pp) }
    $cr.AutomationElementMode = [System.Windows.Automation.AutomationElementMode]::Full
    $null = $cr.Activate()
    try { $found = $top.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) }
    finally { $cr.Pop() }

    $items = New-Object System.Collections.ArrayList
    foreach ($el in $found) {
      $c = $el.Cached
      $ty = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
      $nm = [string]$c.Name
      $ai = [string]$c.AutomationId
      if (-not $nm -and -not $ai -and @('Edit', 'Document', 'ComboBox') -notcontains $ty) { continue }
      $r = $c.BoundingRectangle
      $hasRect = -not ($r.IsEmpty -or [double]::IsInfinity($r.X))
      $v = $el.GetCachedPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty, $true)
      $tg = $el.GetCachedPropertyValue([System.Windows.Automation.TogglePattern]::ToggleStateProperty, $true)
      $sl = $el.GetCachedPropertyValue([System.Windows.Automation.SelectionItemPattern]::IsSelectedProperty, $true)
      $tgs = $null
      if ($tg -is [System.Windows.Automation.ToggleState]) { $tgs = $tg.ToString().ToLower() }
      elseif ($tg -is [int]) { $tgs = ([System.Windows.Automation.ToggleState]$tg).ToString().ToLower() }
      [void]$items.Add([pscustomobject]@{
        el = $el; name = $nm; type = $ty; id = $ai
        cx = $(if ($hasRect) { [int]($r.X + $r.Width / 2) } else { 0 })
        cy = $(if ($hasRect) { [int]($r.Y + $r.Height / 2) } else { 0 })
        w = $(if ($hasRect) { [int]$r.Width } else { 0 })
        h = $(if ($hasRect) { [int]$r.Height } else { 0 })
        enabled = [bool]$c.IsEnabled
        value = $(if ($v -is [string]) { $v } else { $null })
        toggle = $tgs
        selected = $(if ($sl -is [bool]) { $sl } else { $null })
      })
    }
    return $items
  }

  # Apply -id / -type / -name filters (name: exact match first, then case-insensitive substring)
  function Select-UiMatches($items) {
    $m = @($items)
    if ($Id) { $m = @($m | Where-Object { $_.id -eq $Id }) }
    if ($Type) {
      $ts = @($Type.ToLower() -split '[,\s]+' | Where-Object { $_ })
      $m = @($m | Where-Object { $ts -contains $_.type.ToLower() })
    }
    if ($Name) {
      $ex = @($m | Where-Object { $_.name -eq $Name })
      if ($ex.Count -gt 0) { $m = $ex }
      else { $m = @($m | Where-Object { $_.name.IndexOf($Name, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) }
    }
    return $m
  }

  # Matches among interactive controls; if none, retry including non-interactive elements
  function Find-UiMatches($top) {
    $m = @(Select-UiMatches @(Get-UiItems $top.el ([bool]$Raw)))
    if ($m.Count -eq 0 -and -not $Raw) { $m = @(Select-UiMatches @(Get-UiItems $top.el $true)) }
    return $m
  }

  function Pick-UiItem($m, [string]$what) {
    $m = @($m)
    if ($m.Count -eq 0) { throw "E2|no UI element matches $what" }
    if ($Index -gt 0) {
      if ($Index -gt $m.Count) { throw "E1|-index $Index out of range (1..$($m.Count)) for $what" }
      return $m[$Index - 1]
    }
    if ($m.Count -gt 1) {
      $cand = (@($m | Select-Object -First 8) | ForEach-Object { "$($_.type):'$($_.name)'" + $(if ($_.id) { "#$($_.id)" } else { '' }) }) -join '; '
      throw "E1|$($m.Count) elements match $what; refine with -type / -id or pick with -index n. Candidates: $cand"
    }
    return $m[0]
  }

  function Ui-Json($it, [int]$i) {
    $o = [ordered]@{ n = $i; name = $it.name; type = $it.type }
    if ($it.id) { $o['id'] = $it.id }
    $o['c'] = @($it.cx, $it.cy)
    if (-not $it.enabled) { $o['disabled'] = $true }
    if ($it.value) {
      $v = [string]$it.value
      if ($v.Length -gt 80) { $v = $v.Substring(0, 80) + '...' }
      $o['value'] = $v
    }
    if ($it.toggle) { $o['toggle'] = $it.toggle }
    if ($it.selected) { $o['selected'] = $true }
    return $o
  }

  # Use the control's own pattern instead of the mouse
  function Invoke-Ui($it) {
    $el = $it.el
    $pt = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pt)) { $pt.Invoke(); return 'invoke' }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pt)) { $pt.Toggle(); return 'toggle' }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pt)) { $pt.Select(); return 'select' }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pt)) { $pt.Expand(); return 'expand' }
    throw 'E3|element supports no invoke-style pattern; drop -invoke to use the mouse'
  }

  # Give the control keyboard focus (falls back to a mouse click on its center)
  function Focus-Ui($top, $it) {
    [Desk1]::Focus([IntPtr]$top.hwnd); Start-Sleep -Milliseconds 100
    try { $it.el.SetFocus() }
    catch {
      [Desk1]::Move($it.cx, $it.cy); Start-Sleep -Milliseconds 40
      [Desk1]::Button('left', $true); Start-Sleep -Milliseconds 30; [Desk1]::Button('left', $false)
    }
    Start-Sleep -Milliseconds 80
  }

  # ---- commands ----------------------------------------------------------
  try {
    switch ($Cmd.ToLower()) {

      'shot' {
        Ok (Do-Shot (Resolve-Target) $true)
      }

      'windows' {
        $rows = @(foreach ($pr in (Find-Window | Where-Object { $_.MainWindowTitle })) {
          $hh = $pr.MainWindowHandle
          $rc = [Desk1]::Rect($hh)
          [ordered]@{
            pid = $pr.Id; process = $pr.ProcessName; title = $pr.MainWindowTitle
            hwnd = $hh.ToInt64(); rect = @($rc[0], $rc[1], ($rc[2] - $rc[0]), ($rc[3] - $rc[1]))
            minimized = [Desk1]::IsIconic($hh)
          }
        })
        $global:LASTEXITCODE = 0
        ConvertTo-Json -InputObject $rows -Compress -Depth 4
      }

      'focus' {
        $t = Resolve-Target
        if ($t.mode -ne 'window') { throw 'E1|focus needs -window or -process' }
        [Desk1]::Focus([IntPtr][int64]$t.hwnd)
        Start-Sleep -Milliseconds 150
        Finish ([ordered]@{ action = 'focus'; title = $t.title })
      }

      'click' {
        if ($Name -or $Id) {
          # UI Automation mode: click a control by name / AutomationId
          $top = Resolve-UiTop
          $what = "(name='$Name' id='$Id' type='$Type')"
          $it = Pick-UiItem @(Find-UiMatches $top) $what
          if (-not $Invoke -and $it.w -gt 0 -and $it.h -gt 0) {
            [Desk1]::Focus([IntPtr]$top.hwnd); Start-Sleep -Milliseconds 120
            [Desk1]::Move($it.cx, $it.cy); Start-Sleep -Milliseconds 40
            $n = if ($Double) { 2 } else { 1 }
            for ($i = 0; $i -lt $n; $i++) {
              [Desk1]::Button($Button, $true); Start-Sleep -Milliseconds 30
              [Desk1]::Button($Button, $false)
              if ($i -lt $n - 1) { Start-Sleep -Milliseconds 60 }
            }
            $via = 'mouse'
          } else { $via = Invoke-Ui $it }
          Finish ([ordered]@{ action = 'click'; via = $via; element = [ordered]@{ name = $it.name; type = $it.type; id = $it.id }; screen = @($it.cx, $it.cy) })
        } else {
          $p = $null
          if ($Rest.Count -ge 2) {
            $p = Map-Point $Rest[0] $Rest[1]
            [Desk1]::Move($p[0], $p[1]); Start-Sleep -Milliseconds 40
          }
          $n = if ($Double) { 2 } else { 1 }
          for ($i = 0; $i -lt $n; $i++) {
            [Desk1]::Button($Button, $true); Start-Sleep -Milliseconds 30
            [Desk1]::Button($Button, $false)
            if ($i -lt $n - 1) { Start-Sleep -Milliseconds 60 }
          }
          Finish ([ordered]@{ action = 'click'; button = $Button; double = [bool]$Double; screen = $p })
        }
      }

      'move' {
        if ($Rest.Count -lt 2) { throw 'E1|usage: computerUse move x y' }
        $p = Map-Point $Rest[0] $Rest[1]
        [Desk1]::Move($p[0], $p[1])
        Finish ([ordered]@{ action = 'move'; screen = $p })
      }

      'drag' {
        if ($Rest.Count -lt 4) { throw 'E1|usage: computerUse drag x1 y1 x2 y2' }
        $p1 = Map-Point $Rest[0] $Rest[1]
        $p2 = Map-Point $Rest[2] $Rest[3]
        [Desk1]::Move($p1[0], $p1[1]); Start-Sleep -Milliseconds 60
        [Desk1]::Button($Button, $true); Start-Sleep -Milliseconds 60
        for ($i = 1; $i -le 10; $i++) {
          [Desk1]::Move([int]($p1[0] + ($p2[0] - $p1[0]) * $i / 10), [int]($p1[1] + ($p2[1] - $p1[1]) * $i / 10))
          Start-Sleep -Milliseconds 15
        }
        Start-Sleep -Milliseconds 60
        [Desk1]::Button($Button, $false)
        Finish ([ordered]@{ action = 'drag'; from = $p1; to = $p2 })
      }

      'scroll' {
        $dir = if ($Rest.Count -ge 1) { $Rest[0].ToLower() } else { '' }
        if ($dir -ne 'up' -and $dir -ne 'down') { throw 'E1|usage: computerUse scroll up|down [n] [x y]' }
        $n = if ($Rest.Count -ge 2) { [int]$Rest[1] } else { 3 }
        $p = $null
        if ($Rest.Count -ge 4) { $p = Map-Point $Rest[2] $Rest[3] }
        elseif (-not $Abs) {
          # no position given: scroll at the center of the last shot
          $s = Load-State
          if ($s) { $p = Map-Point ($s.iw / 2) ($s.ih / 2) }
        }
        if ($p) { [Desk1]::Move($p[0], $p[1]); Start-Sleep -Milliseconds 40 }
        [Desk1]::Wheel($(if ($dir -eq 'up') { $n } else { -$n }))
        Finish ([ordered]@{ action = 'scroll'; direction = $dir; amount = $n; screen = $p })
      }

      'type' {
        $text = $Rest -join ' '
        if (-not $text) { throw 'E1|usage: computerUse type <text> [-enter] [-name <text> | -id <id>]' }
        if ($Name -or $Id) {
          $top = Resolve-UiTop
          $it = Pick-UiItem @(Find-UiMatches $top) "(name='$Name' id='$Id' type='$Type')"
          Focus-Ui $top $it
        }
        [Desk1]::TypeText($text)
        if ($Enter) { Start-Sleep -Milliseconds 30; [Desk1]::Chord([uint16[]]@(0x0D)) }
        Finish ([ordered]@{ action = 'type'; chars = $text.Length; enter = [bool]$Enter })
      }

      'key' {
        if (-not $Rest) { throw 'E1|usage: computerUse key <chord> [<chord> ...]' }
        foreach ($chord in $Rest) {
          $vks = @(foreach ($part in ($chord -split '\+')) { Key-Vk $part })
          [Desk1]::Chord([uint16[]]$vks)
          Start-Sleep -Milliseconds 40
        }
        Finish ([ordered]@{ action = 'key'; keys = @($Rest) })
      }

      'ui' {
        $top = Resolve-UiTop
        $uiAll = @(Select-UiMatches @(Get-UiItems $top.el ([bool]$Raw)))
        $lim = if ($Limit -gt 0) { $Limit } else { 150 }
        $shown = @($uiAll | Select-Object -First $lim)
        $i = 0
        $rows = @(foreach ($it in $shown) { $i++; Ui-Json $it $i })
        Ok ([ordered]@{ window = $top.title; count = $shown.Count; total = $uiAll.Count; items = $rows })
      }

      'set' {
        if (-not ($Name -or $Id -or $Type)) { throw 'E1|usage: computerUse set -name <text> <value> [-enter] [-keys]' }
        $text = $Rest -join ' '
        $top = Resolve-UiTop
        $it = Pick-UiItem @(Find-UiMatches $top) "(name='$Name' id='$Id' type='$Type')"
        $via = 'keys'
        $pt = $null
        if (-not $Keys -and $it.el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pt) -and -not $pt.Current.IsReadOnly) {
          $pt.SetValue($text)
          $via = 'value'
          if ($Enter) { Focus-Ui $top $it }
        } else {
          Focus-Ui $top $it
          [Desk1]::Chord([uint16[]]@(0x11, 0x41)); Start-Sleep -Milliseconds 60
          if ($text) { [Desk1]::TypeText($text) } else { [Desk1]::Chord([uint16[]]@(0x08)) }
        }
        if ($Enter) { Start-Sleep -Milliseconds 30; [Desk1]::Chord([uint16[]]@(0x0D)) }
        Finish ([ordered]@{ action = 'set'; via = $via; element = [ordered]@{ name = $it.name; type = $it.type; id = $it.id }; chars = $text.Length })
      }

      'text' {
        if (-not ($Name -or $Id -or $Type)) { throw 'E1|usage: computerUse text -name <text> | -id <id> | -type document [-limit n]' }
        $top = Resolve-UiTop
        $it = Pick-UiItem @(Find-UiMatches $top) "(name='$Name' id='$Id' type='$Type')"
        $lim = if ($Limit -gt 0) { $Limit } else { 4000 }
        $pt = $null
        if ($it.el.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pt)) {
          $txt = [string]$pt.DocumentRange.GetText($lim + 1); $via = 'text'
        } elseif ($it.value) {
          $txt = [string]$it.value; $via = 'value'
        } else {
          $txt = [string]$it.name; $via = 'name'
        }
        $trunc = $txt.Length -gt $lim
        if ($trunc) { $txt = $txt.Substring(0, $lim) }
        Ok ([ordered]@{ via = $via; chars = $txt.Length; truncated = $trunc; text = $txt })
      }

      default { throw "E1|unknown command: $Cmd (run: computerUse -h)" }
    }
  }
  catch {
    $m = $_.Exception.Message
    if ($m -match '(?s)^E([123])\|(.*)$') { Fail ([int]$Matches[1]) $Matches[2] } else { Fail 3 $m }
  }
}
Export-ModuleMember -Function computerUse