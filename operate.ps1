$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:WINAPP_UI_WORKFLOW_ID = 'hands-on'
$AppPid = 0
$results = [Collections.Generic.List[object]]::new()
$sp = Join-Path $env:APPDATA 'RedditWallpaper\settings.json'

function IW { param([string[]]$a) $global:LASTEXITCODE = $null; $o = @(& winapp @a); if ($global:LASTEXITCODE -ne 0) { throw "winapp $($a -join ' ') 失败: $($o -join "`n")" }; $o }
function T { param($n, [scriptblock]$s) try { $r = & $s; if ($r) { $results.Add(@{ name = $n; status = 'PASS'; note = $r }) } else { $results.Add(@{ name = $n; status = 'PASS' }) } } catch { $results.Add(@{ name = $n; status = 'FAIL'; detail = "$_" }) } }
function Get-V { param($id) ((IW @('ui', 'get-value', $id, '--json', '-a', $AppPid)) | ConvertFrom-Json).text }
function Get-Tree { param($p = $AppPid)
    $j = IW @('ui', 'inspect', '--json', '-a', $p) | ConvertFrom-Json
    function Get-El($nodes) { foreach ($n in $nodes) { if ($null -ne $n) { $n; Get-El $n.children } } }
    Get-El @($j.windows | ForEach-Object { $_.elements })
}
function Show-AppWindow {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Hnd {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
"@
    $script:hwnd = [IntPtr]::Zero
    $cb = [Hnd+EnumProc]{ param($w, $l) [uint32]$op = 0; [Hnd]::GetWindowThreadProcessId($w, [ref]$op) | Out-Null; if ($op -eq $AppPid) { $sb = New-Object System.Text.StringBuilder 256; [Hnd]::GetWindowText($w, $sb, 256) | Out-Null; if ($sb.ToString() -like '*壁纸轮换*') { $script:hwnd = $w; return $false } }; return $true }
    [Hnd]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    [Hnd]::ShowWindow($script:hwnd, 9) | Out-Null   # SW_RESTORE
    Start-Sleep -Milliseconds 1000
}

try {
    # ---------- P0 启动并记录初始状态 ----------
    Get-Process RedditWallpaper -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    $launch = IW @('run', '.', '--detach', '--json') | ConvertFrom-Json
    $AppPid = [int]$launch.ProcessId
    Start-Sleep -Seconds 2
    Show-AppWindow
    # 等待 UIA 树就绪（窗口显示后控件才会 realization）
    IW @('ui', 'wait-for', 'UseOAuthToggle', '-t', '10000', '-a', $AppPid) | Out-Null

    $orig = @{}
    foreach ($id in 'UseRedditToggle','UseBingToggle','UseLocalToggle','SourcePriorityToggle','AutoRotateOnLaunchToggle','StartMinimizedToggle','AutoStartToggle','SkipNsfwToggle','UseOAuthToggle') {
        $orig[$id] = Get-V $id
    }
    $origSort = Get-V 'SortCombo'; $origRange = Get-V 'RangeCombo'
    $origStyle = Get-V 'StyleCombo'; $origRes = Get-V 'ResolutionCombo'; $origMin = Get-V 'MinScoreBox'
    "初始值记录完成"

    # ---------- P1 只读核对 ----------
    T 'P1 设置与 UI 一致（保存文件夹/排序/样式）' {
        $sf = Get-V 'SaveFolderBox'
        if ($sf -ne 'E:\reddit') { throw "SaveFolderBox=$sf（用户设置应为 E:\reddit）" }
        $s = Get-Content -LiteralPath $sp -Raw | ConvertFrom-Json
        if ($s.SaveFolder -ne 'E:\reddit') { throw "settings=$($s.SaveFolder)" }
        if ($origSort -notmatch 'best') { throw "SortCombo=$origSort" }
    }

    # ---------- P2 壁纸样式立即应用 ----------
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class WpStyle {
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, string c, uint d);
}
"@
    T 'P2a 样式=适应 → 注册表 6' {
        IW @('ui', 'set-value', 'StyleCombo', '适应', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 900
        $style = (Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallpaperStyle
        if ($style -ne '6') { throw "WallpaperStyle=$style" }
        $st = Get-V 'StatusText'
        if ($st -notmatch '壁纸样式已切换') { throw "状态=$st" }
    }
    T 'P2b 样式=填充 → 注册表 10' {
        IW @('ui', 'set-value', 'StyleCombo', '填充', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 900
        $style = (Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallpaperStyle
        if ($style -ne '10') { throw "WallpaperStyle=$style" }
    }

    # ---------- P3 最低分辨率持久化 ----------
    T 'P3 分辨率 2K 保存 → 恢复 1080' {
        IW @('ui', 'set-value', 'ResolutionCombo', '最低 2560 × 1440', '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'SaveSettingsBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 500
        $j = Get-Content -LiteralPath $sp -Raw | ConvertFrom-Json
        if ($j.WallpaperResolution -ne '2560x1440') { throw "res=$($j.WallpaperResolution)" }
        IW @('ui', 'set-value', 'ResolutionCombo', '最低 1920 × 1080', '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'SaveSettingsBtn', '-a', $AppPid) | Out-Null
    }

    # ---------- P4 随机排序提示 ----------
    T 'P4 随机排序：忽略时间范围与得分' {
        IW @('ui', 'set-value', 'SortCombo', '随机 random', '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'RotateNowBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Seconds 8
        $st = Get-V 'StatusText'
        if ($st -notmatch '随机排序：忽略') { throw "状态=$st" }
    }
    T 'P4b 恢复排序 best' {
        IW @('ui', 'set-value', 'SortCombo', '最佳 best', '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'SaveSettingsBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 400
        $j = Get-Content -LiteralPath $sp -Raw | ConvertFrom-Json
        if ($j.RedditSort -ne 'best') { throw "sort=$($j.RedditSort)" }
    }

    # ---------- P5 Reddit 轮换（记录文件名） ----------
    T 'P5 Reddit 轮换出图' {
        IW @('ui', 'invoke', 'RotateNowBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Seconds 12
        $st = Get-V 'StatusText'
        if ($st -notmatch '壁纸已更换') { throw "状态=$st" }
        if ($st -match '\(E:') { }
        $m = [regex]::Match($st, '壁纸已更换：(.+?)（')
        $script:downloadedName = $m.Groups[1].Value
        "本次下载: $downloadedName"
    }

    # ---------- P6 自动轮换 ----------
    T 'P6 轮换启动/倒计时/停止' {
        IW @('ui', 'invoke', 'ToggleTimerBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 600
        $st = Get-V 'StatusText'; $n = Get-V 'NextChangeText'
        if ($st -notmatch '自动轮换已启动') { throw "状态=$st" }
        if ($n -notmatch '后更换') { throw "倒计时=$n" }
        IW @('ui', 'invoke', 'ToggleTimerBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 400
        $n2 = Get-V 'NextChangeText'
        if ($n2 -notmatch '已停止') { throw "停止后=$n2" }
    }

    # ---------- P7 Bing 来源切换 ----------
    T 'P7 Reddit关/Bing开 → 轮换' {
        IW @('ui', 'set-value', 'UseRedditToggle', 'Off', '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'UseBingToggle', 'On', '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'RotateNowBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Seconds 5
        $st = Get-V 'StatusText'
        "Bing 轮换状态: $st"
        if ($st -notmatch '来源：Bing') { throw "Bing 未生效: $st" }
    }
    T 'P7b 恢复来源' {
        IW @('ui', 'set-value', 'UseRedditToggle', 'On', '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'UseBingToggle', 'Off', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 300
    }

    # ---------- P8 社区行添加/删除 ----------
    T 'P8 添加社区行 → ✕ 删除' {
        $before = @(Get-Tree | Where-Object { $_.name -eq '✕' }).Count
        IW @('ui', 'invoke', '＋ 添加社区', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 500
        $after = @(Get-Tree | Where-Object { $_.name -eq '✕' }).Count
        if ($after -ne $before + 1) { throw "行未增加 $before -> $after" }
        $del = (Get-Tree | Where-Object { $_.name -eq '✕' } | Select-Object -Last 1).selector
        IW @('ui', 'invoke', $del, '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 500
        $final = @(Get-Tree | Where-Object { $_.name -eq '✕' }).Count
        if ($final -ne $before) { throw "行未删回 $after -> $final" }
    }

    # ---------- P9 图片库：设为壁纸 → 再轮换 → 删除测试文件 ----------
    T 'P9a 图片库「设为壁纸」' {
        $insp = IW @('ui', 'inspect', '--interactive', '--json', '-a', $AppPid) | ConvertFrom-Json
        function Get-El($nodes) { foreach ($n in $nodes) { if ($null -ne $n) { $n; Get-El $n.children } } }
        $all = Get-El @($insp.windows | ForEach-Object { $_.elements })
        $btn = $all | Where-Object { $_.name -eq '设为壁纸' } | Select-Object -First 1
        if (-not $btn) { throw '图片库为空（无设为壁纸按钮）' }
        IW @('ui', 'invoke', $btn.automationId, '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 800
        $st = Get-V 'StatusText'
        if ($st -notmatch '壁纸已设置') { throw "状态=$st" }
    }
    T 'P9b 再轮换一次（换走当前壁纸）' {
        IW @('ui', 'invoke', 'RotateNowBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Seconds 10
        $st = Get-V 'StatusText'
        if ($st -notmatch '壁纸已更换') { throw "状态=$st" }
    }
    T 'P9c 删除测试下载的文件（两步确认 → 回收站）' {
        if (-not $script:downloadedName) { throw '无 P5 记录' }
        $path = "E:\reddit\$downloadedName"
        if (-not (Test-Path -LiteralPath $path)) { throw "文件缺失: $path" }
        $insp = IW @('ui', 'inspect', '--interactive', '--json', '-a', $AppPid) | ConvertFrom-Json
        function Get-El($nodes) { foreach ($n in $nodes) { if ($null -ne $n) { $n; Get-El $n.children } } }
        $all = Get-El @($insp.windows | ForEach-Object { $_.elements })
        $del = $all | Where-Object { $_.name -eq '删除' } | Select-Object -First 1
        if (-not $del) { throw '未找到删除按钮' }
        IW @('ui', 'invoke', $del.automationId, '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 400
        $conf = (Get-Tree -p $AppPid) | Where-Object { $_.name -eq '确认删除' } | Select-Object -First 1
        if (-not $conf) { throw '未进入确认态' }
        IW @('ui', 'invoke', $conf.automationId, '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 900
        if (Test-Path -LiteralPath $path) { throw '文件未被移除' }
        $shell = New-Object -ComObject Shell.Application
        $inBin = @($shell.NameSpace(10).Items()) | Where-Object { $_.Name -eq $downloadedName }
        if (-not $inBin) { throw '回收站未找到' }
    }
    T 'P9c2 从回收站还原该文件' {
        $shell = New-Object -ComObject Shell.Application
        $item = @($shell.NameSpace(10).Items()) | Where-Object { $_.Name -eq $script:downloadedName } | Select-Object -First 1
        $verb = @($item.Verbs()) | Where-Object { $_.Name -match '\(&E\)' } | Select-Object -First 1
        $verb.DoIt()
        Start-Sleep -Milliseconds 600
        if (-not (Test-Path -LiteralPath "E:\reddit\$downloadedName")) { throw '还原失败' }
    }

    # ---------- P10 关窗 → 托盘 ----------
    T 'P10 关闭窗口 → 进程存活（托盘）' {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Msg { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }
"@
        [Msg]::PostMessage($script:hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 900
        if (-not (Get-Process RedditWallpaper -ErrorAction SilentlyContinue)) { throw '进程退出，托盘失效' }
        Show-AppWindow
    }

    # ---------- P11 设置完整性 ----------
    T 'P11 设置文件路径无乱码' {
        $j = [IO.File]::ReadAllText($sp, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($j.SaveFolder -ne 'E:\reddit') { throw "SaveFolder=$($j.SaveFolder)" }
    }

    # ---------- P12 恢复全部开关初始值 ----------
    T 'P12 恢复所有开关初始值' {
        foreach ($id in $orig.Keys) { IW @('ui', 'set-value', $id, $orig[$id], '-a', $AppPid) | Out-Null }
        IW @('ui', 'set-value', 'SortCombo', $origSort, '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'RangeCombo', $origRange, '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'StyleCombo', $origStyle, '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'ResolutionCombo', $origRes, '-a', $AppPid) | Out-Null
        IW @('ui', 'set-value', 'MinScoreBox', $origMin, '-a', $AppPid) | Out-Null
        IW @('ui', 'invoke', 'SaveSettingsBtn', '-a', $AppPid) | Out-Null
        Start-Sleep -Milliseconds 500
        $j = Get-Content -LiteralPath $sp -Raw | ConvertFrom-Json
        if ($j.SaveFolder -ne 'E:\reddit') { throw "保存路径被改动: $($j.SaveFolder)" }
    }
}
catch {
    $results.Add(@{ name = '执行错误'; status = 'FAIL'; detail = "$_" })
}
$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
[ordered]@{ passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath '.\hands-on-results.json' -Encoding utf8
"通过: $($results.Count - $failed) 失败: $failed（明细: hands-on-results.json）"
