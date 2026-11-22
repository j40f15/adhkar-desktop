# Adhkar desktop: Makkah prayer-times wallpaper with a random dhikr, plus prayer notifications.
#   -Mode Setup      install (icon, notification name, scheduled tasks) and draw the wallpaper
#   -Mode Refresh    redraw the wallpaper (new dhikr, next prayer) and reschedule today's alerts
#   -Mode Alert      show the prayer notification (run by the scheduled task at each prayer)
#   -Mode Test       show a test notification now
#   -Mode Uninstall  remove the scheduled tasks and the notification registration
param(
  [ValidateSet('Setup', 'Refresh', 'Alert', 'Test', 'Uninstall', 'Preview', 'Notify', 'ScanFile', 'Sandbox')]
  [string]$Mode = 'Refresh',
  [string]$Target   # file or folder for -Mode ScanFile
)

$ErrorActionPreference = 'Stop'
$Root        = $PSScriptRoot
$ScriptPath  = $PSCommandPath
$AppId       = 'Makkah.Adhkar'
$HelperAppId = 'Makkah.PCGuard'   # heat / frozen-app alerts from adhkar-dua.exe
$TaskRefresh = 'Adhkar - Refresh'
$TaskAlert   = 'Adhkar - Prayer Alert'
$LogFile     = Join-Path $Root 'log.txt'

# Makkah (Masjid al-Haram), Umm al-Qura method: Fajr 18.5 deg, Isha = Maghrib + 90 min (120 in Ramadan)
$Lat = 21.4225; $Lng = 39.8262; $TZ = 3; $City = 'مكة المكرمة'
# city.txt (written by the installer) = "name|lat|lng" - any Saudi city, all on Arabia Standard Time / Umm al-Qura
$cityFile = Join-Path $Root 'city.txt'
if (Test-Path $cityFile) {
  $c = "$(Get-Content -LiteralPath $cityFile -Encoding UTF8 -TotalCount 1)" -split '\|'
  if ($c.Count -ge 3) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $City = $c[0].Trim(); $Lat = [double]::Parse($c[1].Trim(), $inv); $Lng = [double]::Parse($c[2].Trim(), $inv)
  }
}
$CityShort = ($City -split ' ')[0]
$MakkahTz = [TimeZoneInfo]::FindSystemTimeZoneById('Arab Standard Time')
$Ar = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')

# Optional reminders - set a value to 0 / $false to turn it off, then run:  adhkar-run.exe Refresh
$RemindBeforeMinutes  = 10      # heads-up before each prayer (0 = off)
$MorningEveningAdhkar = $true   # morning adhkar 20 min after Fajr, evening adhkar 20 min after Asr
$FridayReminder       = $true   # Friday 9:00 - Surat al-Kahf and salawat
$FastingReminder      = $true   # 9 PM the night before Monday / Thursday / the white days (13-15)
$DuhaReminder         = $true   # 20 min after sunrise
$SleepReminder        = $true   # 11 PM - sleep adhkar and Surat al-Mulk
$Weather              = $true   # hourly Makkah weather from open-meteo.com (sends only Makkah's coordinates)
$DuaSeconds           = 30      # how often the dua on the desktop changes (adhkar-dua.exe); run -Mode Setup after changing
$TaskWidget           = 'Adhkar - Dua Widget'
$AppName              = 'حِصن'  # the program's name (notifications, right-click menu)
$Version              = '2.3'   # shown in the settings / used by the auto-update

# settings.ini (written by the settings window in adhkar-dua.exe) overrides the defaults above
$Settings = @{}
$settingsFile = Join-Path $Root 'settings.ini'
if (Test-Path $settingsFile) {
  foreach ($l in Get-Content -LiteralPath $settingsFile -Encoding UTF8) { if ($l -match '^\s*([\w\.]+)\s*=\s*(.*)$') { $Settings[$Matches[1]] = $Matches[2].Trim() } }
}
function Get-SettingInt([string]$k, [int]$def) { $v = 0; if ($Settings.ContainsKey($k) -and [int]::TryParse($Settings[$k], [ref]$v)) { $v } else { $def } }
function Get-SettingBool([string]$k, [bool]$def) { if ($Settings.ContainsKey($k)) { $Settings[$k] -eq '1' } else { $def } }
$RemindBeforeMinutes  = Get-SettingInt  'remind_before'   $RemindBeforeMinutes
$MorningEveningAdhkar = Get-SettingBool 'morning_evening' $MorningEveningAdhkar
$FridayReminder       = Get-SettingBool 'friday'          $FridayReminder
$FastingReminder      = Get-SettingBool 'fasting'         $FastingReminder
$DuhaReminder         = Get-SettingBool 'duha'            $DuhaReminder
$SleepReminder        = Get-SettingBool 'sleep'           $SleepReminder
$DuaSeconds           = Get-SettingInt  'dua_seconds'     $DuaSeconds
# minutes to move each prayer (to match a local mosque), -10..+10
$Offsets = @{}
foreach ($p in 'Fajr', 'Dhuhr', 'Asr', 'Maghrib', 'Isha') { $Offsets[$p] = [math]::Max(-10, [math]::Min(10, (Get-SettingInt ('offset_' + $p.ToLower()) 0))) }
# accent colour of the wallpaper (the widget uses the same theme)
$Accent = switch ($Settings['theme']) { 'green' { 110, 200, 150 } 'blue' { 110, 170, 240 } 'white' { 225, 225, 225 } default { 212, 175, 106 } }

function Write-Log([string]$Message) {
  try {
    if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 200KB) { Remove-Item $LogFile -Force }
    Add-Content -LiteralPath $LogFile -Value ('{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $Mode, $Message) -Encoding UTF8
  } catch {}
}

# ---------------------------------------------------------------- prayer times
function DSin([double]$d) { [math]::Sin($d * [math]::PI / 180) }
function DCos([double]$d) { [math]::Cos($d * [math]::PI / 180) }
function DTan([double]$d) { [math]::Tan($d * [math]::PI / 180) }
function DArcSin([double]$x) { [math]::Asin($x) * 180 / [math]::PI }
function DArcCos([double]$x) { [math]::Acos($x) * 180 / [math]::PI }
function DArcTan2([double]$y, [double]$x) { [math]::Atan2($y, $x) * 180 / [math]::PI }
function DArcCot([double]$x) { [math]::Atan(1 / $x) * 180 / [math]::PI }
function Fix([double]$a, [double]$b) { $a = $a - $b * [math]::Floor($a / $b); if ($a -lt 0) { $a + $b } else { $a } }

function Get-SunPosition([double]$Jd) {
  $D = $Jd - 2451545.0
  $g = Fix (357.529 + 0.98560028 * $D) 360
  $q = Fix (280.459 + 0.98564736 * $D) 360
  $L = Fix ($q + 1.915 * (DSin $g) + 0.020 * (DSin (2 * $g))) 360
  $e = 23.439 - 0.00000036 * $D
  $RA = (DArcTan2 ((DCos $e) * (DSin $L)) (DCos $L)) / 15
  @{ Eqt = $q / 15 - (Fix $RA 24); Decl = DArcSin ((DSin $e) * (DSin $L)) }
}

function Get-PrayerTimes([datetime]$Date) {
  $y = $Date.Year; $m = $Date.Month; $d = $Date.Day
  if ($m -le 2) { $y -= 1; $m += 12 }
  $A = [math]::Floor($y / 100); $B = 2 - $A + [math]::Floor($A / 4)
  $jd = [math]::Floor(365.25 * ($y + 4716)) + [math]::Floor(30.6001 * ($m + 1)) + $d + $B - 1524.5 - $Lng / (15 * 24)

  $midDay   = { param($t) $sp = Get-SunPosition ($jd + $t); Fix (12 - $sp.Eqt) 24 }
  $sunAngle = {
    param($angle, $t, $ccw)
    $sp = Get-SunPosition ($jd + $t)
    $noon = & $midDay $t
    $v = (-(DSin $angle) - (DSin $sp.Decl) * (DSin $Lat)) / ((DCos $sp.Decl) * (DCos $Lat))
    $tt = (DArcCos $v) / 15
    if ($ccw) { $noon - $tt } else { $noon + $tt }
  }

  $fajr    = & $sunAngle 18.5 (5 / 24) $true
  $sunrise = & $sunAngle 0.833 (6 / 24) $true
  $dhuhr   = & $midDay (12 / 24)
  $spA     = Get-SunPosition ($jd + 13 / 24)
  $asr     = & $sunAngle (-(DArcCot (1 + (DTan ([math]::Abs($Lat - $spA.Decl)))))) (13 / 24) $false
  $maghrib = & $sunAngle 0.833 (18 / 24) $false

  $base  = $Date.Date
  $toDt  = { param($h) $base.AddMinutes([math]::Floor(($h + $TZ - $Lng / 15) * 60 + 0.5)) }
  $isRamadan = (New-Object Globalization.UmAlQuraCalendar).GetMonth($Date) -eq 9
  $ishaAfter = if ($isRamadan) { 120 } else { 90 }
  $dhuhrName = if ($Date.DayOfWeek -eq [DayOfWeek]::Friday) { 'الجمعة' } else { 'الظهر' }
  $maghribDt = & $toDt $maghrib

  @(
    [pscustomobject]@{ Key = 'Fajr';    Name = 'الفجر';   Time = (& $toDt $fajr).AddMinutes($Offsets['Fajr']);       Prayer = $true }
    [pscustomobject]@{ Key = 'Sunrise'; Name = 'الشروق';  Time = (& $toDt $sunrise);                                 Prayer = $false }
    [pscustomobject]@{ Key = 'Dhuhr';   Name = $dhuhrName; Time = (& $toDt $dhuhr).AddMinutes($Offsets['Dhuhr']);    Prayer = $true }
    [pscustomobject]@{ Key = 'Asr';     Name = 'العصر';   Time = (& $toDt $asr).AddMinutes($Offsets['Asr']);         Prayer = $true }
    [pscustomobject]@{ Key = 'Maghrib'; Name = 'المغرب';  Time = $maghribDt.AddMinutes($Offsets['Maghrib']);         Prayer = $true }
    [pscustomobject]@{ Key = 'Isha';    Name = 'العشاء';  Time = $maghribDt.AddMinutes($ishaAfter + $Offsets['Isha']); Prayer = $true }
  )
}

function Get-MakkahNow { [TimeZoneInfo]::ConvertTime([datetime]::Now, $MakkahTz) }
function ConvertTo-LocalTime([datetime]$MakkahTime) {
  [TimeZoneInfo]::ConvertTime([datetime]::SpecifyKind($MakkahTime, 'Unspecified'), $MakkahTz, [TimeZoneInfo]::Local)
}

# ---------------------------------------------------------------- text helpers
function ConvertTo-ArabicDigits([string]$s) {
  $digits = '٠١٢٣٤٥٦٧٨٩'
  -join ($s.ToCharArray() | ForEach-Object { if ($_ -ge [char]'0' -and $_ -le [char]'9') { $digits[[int][string]$_] } else { $_ } })
}
function Format-Time([datetime]$t) {
  $h = $t.Hour % 12; if ($h -eq 0) { $h = 12 }
  $ampm = if ($t.Hour -lt 12) { 'ص' } else { 'م' }
  (ConvertTo-ArabicDigits ('{0}:{1:00}' -f $h, $t.Minute)) + ' ' + $ampm
}
function Format-Hijri([datetime]$d) { (ConvertTo-ArabicDigits $d.ToString('dddd d MMMM yyyy', $Ar)) + 'هـ' }
function Format-Gregorian([datetime]$d) {
  $months = 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'
  (ConvertTo-ArabicDigits "$($d.Day)") + ' ' + $months[$d.Month - 1] + ' ' + (ConvertTo-ArabicDigits "$($d.Year)") + 'م'
}

function Get-RandomDhikr {
  $lines = @(Get-Content -LiteralPath (Join-Path $Root 'adhkar.txt') -Encoding UTF8 | Where-Object { $_.Trim() })
  $stateFile = Join-Path $Root 'last-dhikr.txt'
  $last = if (Test-Path $stateFile) { "$(Get-Content -LiteralPath $stateFile -Encoding UTF8 -Raw)".Trim() } else { '' }
  $pool = @($lines | Where-Object { $_.Trim() -ne $last })
  if ($pool.Count -eq 0) { $pool = $lines }
  $pick = (Get-Random -InputObject $pool).Trim()
  Set-Content -LiteralPath $stateFile -Value $pick -Encoding UTF8
  $pick
}

# ---------------------------------------------------------------- drawing (WPF)
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function Col([int]$r, [int]$g, [int]$b, [int]$a = 255) { [System.Windows.Media.Color]::FromArgb($a, $r, $g, $b) }
function Br([int]$r, [int]$g, [int]$b, [int]$a = 255) { $x = [System.Windows.Media.SolidColorBrush]::new((Col $r $g $b $a)); $x.Freeze(); $x }
function Pt([double]$x, [double]$y) { [System.Windows.Point]::new($x, $y) }
function Rc([double]$x, [double]$y, [double]$w, [double]$h) { [System.Windows.Rect]::new($x, $y, $w, $h) }
function Pn($brush, [double]$t) { [System.Windows.Media.Pen]::new($brush, $t) }
function Gold([int]$a = 255) { Br $Accent[0] $Accent[1] $Accent[2] $a }   # theme accent (gold by default)
function White([int]$a = 255) { Br 255 255 255 $a }

function New-FT([string]$Text, [string]$Family, [double]$Size, $Brush, [string]$Weight = 'Normal') {
  $tf = [System.Windows.Media.Typeface]::new([System.Windows.Media.FontFamily]::new($Family), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::$Weight, [System.Windows.FontStretches]::Normal)
  [System.Windows.Media.FormattedText]::new($Text, $Ar, [System.Windows.FlowDirection]::RightToLeft, $tf, $Size, $Brush, 1.0)
}
# Right-to-left FormattedText is laid out leftwards from its origin, so the origin is its top-right corner.
function Out-TextRight($dc, $ft, [double]$right, [double]$top) { $dc.DrawText($ft, (Pt $right $top)) }
function Out-TextLeft($dc, $ft, [double]$left, [double]$top) { $dc.DrawText($ft, (Pt ($left + $ft.Width) $top)) }

function New-Star([double]$cx, [double]$cy, [double]$r) {
  $a = [System.Windows.Media.RectangleGeometry]::new((Rc ($cx - $r) ($cy - $r) (2 * $r) (2 * $r)))
  $b = [System.Windows.Media.RectangleGeometry]::new((Rc ($cx - $r) ($cy - $r) (2 * $r) (2 * $r)))
  $b.Transform = [System.Windows.Media.RotateTransform]::new(45, $cx, $cy)
  [System.Windows.Media.CombinedGeometry]::new([System.Windows.Media.GeometryCombineMode]::Union, $a, $b)
}

function Out-Ornament($dc, [double]$cx, [double]$y) {
  $pen = Pn (Gold 80) 1
  $dc.DrawLine($pen, (Pt ($cx - 170) $y), (Pt ($cx - 20) $y))
  $dc.DrawLine($pen, (Pt ($cx + 20) $y), (Pt ($cx + 170) $y))
  $dc.DrawGeometry((Gold 150), $null, (New-Star $cx $y 5))
}

function Save-Png($visual, [int]$w, [int]$h, [string]$path) {
  $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($visual)
  $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
  $fs = [IO.File]::Create($path)
  try { $enc.Save($fs) } finally { $fs.Close() }
}

function New-Icon([string]$Path) {
  $dv = [System.Windows.Media.DrawingVisual]::new()
  $dc = $dv.RenderOpen()
  $dc.DrawRoundedRectangle((Br 16 48 41), $null, (Rc 0 0 256 256), 56, 56)
  $outer = [System.Windows.Media.EllipseGeometry]::new((Pt 120 132), 82, 82)
  $inner = [System.Windows.Media.EllipseGeometry]::new((Pt 154 110), 72, 72)
  $dc.DrawGeometry((Gold), $null, [System.Windows.Media.CombinedGeometry]::new([System.Windows.Media.GeometryCombineMode]::Exclude, $outer, $inner))
  $dc.DrawGeometry((Gold), $null, (New-Star 182 160 16))
  $dc.Close()
  Save-Png $dv 256 256 $Path
}

function New-ShieldIcon([string]$Path) {
  $dv = [System.Windows.Media.DrawingVisual]::new()
  $dc = $dv.RenderOpen()
  $dc.DrawRoundedRectangle((Br 16 48 41), $null, (Rc 0 0 256 256), 56, 56)
  $dc.DrawGeometry((Gold), $null, [System.Windows.Media.Geometry]::Parse('M128,38 L204,66 L204,128 C204,176 170,208 128,224 C86,208 52,176 52,128 L52,66 Z'))
  $dc.DrawGeometry((Br 16 48 41), $null, [System.Windows.Media.Geometry]::Parse('M88,132 L120,162 L170,106 L184,120 L120,186 L74,146 Z'))
  $dc.Close()
  Save-Png $dv 256 256 $Path
}

function New-Wallpaper($Times, [string]$NextKey, [datetime]$Date, [bool]$IsTomorrow, [string]$Dhikr, [string]$OutFile) {
  $W = 1920; $H = 1080
  try {
    $vc = Get-CimInstance Win32_VideoController | Where-Object CurrentHorizontalResolution | Select-Object -First 1
    if ($vc) { $W = [int]$vc.CurrentHorizontalResolution; $H = [int]$vc.CurrentVerticalResolution }
  } catch {}
  $s  = $H / 1080.0          # design is 1080 units tall, scaled to the screen
  $VW = $W / $s

  $cardW = 440; $cardH = 640; $cardX = $VW - 90 - $cardW; $cardY = 150
  $cx = (150 + $cardX - 60) / 2   # centre of the free area between desktop icons and the card

  $dv = [System.Windows.Media.DrawingVisual]::new()
  $dc = $dv.RenderOpen()
  $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($s, $s))

  # plain black background, easy on the eyes
  $dc.DrawRectangle((Br 0 0 0), $null, (Rc 0 0 $VW 1080))

  # the dua in the middle is drawn live by adhkar-dua.exe (it fades to a new one every $DuaSeconds), not baked in here

  $tasbeeh = New-FT 'سبحان الله   •   الحمد لله   •   لا إله إلا الله   •   الله أكبر' 'Sakkal Majalla' 30 (Gold 95)
  $dc.DrawText($tasbeeh, (Pt ($cx + $tasbeeh.Width / 2) 935))

  # prayer-times card
  $dc.DrawRoundedRectangle((White 8), (Pn (White 20) 1), (Rc $cardX $cardY $cardW $cardH), 26, 26)
  $R = $cardX + $cardW - 36; $L = $cardX + 36
  Out-TextRight $dc (New-FT 'مواقيت الصلاة' 'Sakkal Majalla' 50 (White 220) 'Bold') $R ($cardY + 16)
  Out-TextRight $dc (New-FT $City 'Segoe UI' 19 (Gold 200) 'SemiBold') $R ($cardY + 84)
  $hijri = Format-Hijri $Date
  if ($IsTomorrow) { $hijri = 'غدًا · ' + $hijri }
  Out-TextRight $dc (New-FT $hijri 'Segoe UI' 20 (White 190)) $R ($cardY + 120)
  Out-TextRight $dc (New-FT (Format-Gregorian $Date) 'Segoe UI' 17 (White 115)) $R ($cardY + 152)
  $dc.DrawLine((Pn (White 18) 1), (Pt ($cardX + 30) ($cardY + 196)), (Pt ($cardX + $cardW - 30) ($cardY + 196)))

  $rowH = 66; $rowsTop = $cardY + 212; $i = 0
  foreach ($t in $Times) {
    $ry = $rowsTop + $i * $rowH
    $isNext = $t.Key -eq $NextKey
    if ($isNext) {
      $dc.DrawRoundedRectangle((Gold 26), (Pn (Gold 80) 1), (Rc ($cardX + 16) ($ry + 5) ($cardW - 32) ($rowH - 10)), 16, 16)
      $col = Gold 235
    } elseif (-not $t.Prayer) { $col = White 95 } else { $col = White 200 }
    $nameFt = New-FT $t.Name 'Segoe UI' 25 $col $(if ($isNext) { 'Bold' } else { 'SemiBold' })
    $timeFt = New-FT (Format-Time $t.Time) 'Segoe UI' 25 $col $(if ($isNext) { 'Bold' } else { 'Normal' })
    Out-TextRight $dc $nameFt $R ($ry + ($rowH - $nameFt.Height) / 2)
    Out-TextLeft  $dc $timeFt $L ($ry + ($rowH - $timeFt.Height) / 2)
    if ($isNext) {
      $tag = New-FT 'القادمة' 'Segoe UI' 14 (Gold 200) 'SemiBold'
      Out-TextRight $dc $tag ($R - $nameFt.Width - 12) ($ry + ($rowH - $tag.Height) / 2 + 2)
    }
    $i++
  }

  $dc.Pop()
  $dc.Close()
  Save-Png $dv $W $H $OutFile
}

function Initialize-Native {
  if (-not ('AdhkarNative' -as [type])) {
    Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class AdhkarNative { [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni); }'
  }
}

function Set-Wallpaper([string]$Path) {
  Initialize-Native
  Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value '10'
  Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value '0'
  [void][AdhkarNative]::SystemParametersInfo(0x0014, 0, $Path, 0x03)
}

# Rest screen = Windows screensaver adhkar-rest.scr (a copy of adhkar-dua.exe). settings.ini rest_minutes: 0 = off.
# Windows never starts it while a game or video keeps the screen awake. The lock-on-resume option is left as the user set it.
function Set-RestScreen {
  $scr = Join-Path $Root 'adhkar-rest.scr'
  $min = [Math]::Max(0, [Math]::Min(120, (Get-SettingInt 'rest_minutes' 10)))
  $k = 'HKCU:\Control Panel\Desktop'
  Initialize-Native
  $cur = "$((Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue).'SCRNSAVE.EXE')"
  if ($min -gt 0 -and (Test-Path $scr)) {
    $short = (New-Object -ComObject Scripting.FileSystemObject).GetFile($scr).ShortPath   # Windows wants a path without spaces here
    $old = (Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue).ScreenSaveTimeOut
    if ($cur -eq $short -and "$old" -eq "$($min * 60)") { return }
    Set-ItemProperty -LiteralPath $k -Name 'SCRNSAVE.EXE' -Value $short
    Set-ItemProperty -LiteralPath $k -Name ScreenSaveActive -Value '1'
    Set-ItemProperty -LiteralPath $k -Name ScreenSaveTimeOut -Value "$($min * 60)"
    [void][AdhkarNative]::SystemParametersInfo(0x000F, $min * 60, $null, 0x03)   # SPI_SETSCREENSAVETIMEOUT
    [void][AdhkarNative]::SystemParametersInfo(0x0011, 1, $null, 0x03)           # SPI_SETSCREENSAVEACTIVE
    Write-Log "rest screen: after $min min"
  } elseif ($cur -like '*adhkar-rest.scr' -or $cur -like '*ADHKAR~*.SCR') {      # turned off: remove ours, never someone else's
    Remove-ItemProperty -LiteralPath $k -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue
    [void][AdhkarNative]::SystemParametersInfo(0x0011, 0, $null, 0x03)
    Write-Log 'rest screen: off'
  }
}

# ---------------------------------------------------------------- notifications
# -Reminder keeps the toast on screen until dismissed (used for the prayer itself); the others slide away on their own.
# -Actions: buttons that open a link/protocol, e.g. @(@{ Text = 'اقفله الحين'; Uri = 'adhkar-kill:...' })
function Show-Toast([string]$Title, [string[]]$Lines, [switch]$Reminder, [string]$App = $AppId, [string]$IconFile = 'icon.png', [object[]]$Actions = @()) {
  $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
  $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
  $esc  = { param($s) [Security.SecurityElement]::Escape($s) }
  $icon = ([Uri](Join-Path $Root $IconFile)).AbsoluteUri
  $body =(@($Title) + $Lines | Select-Object -First 3 | ForEach-Object { "<text>$(& $esc $_)</text>" }) -join ''
  $head = if ($Reminder) { '<toast scenario="reminder">' } else { '<toast>' }
  $btns = @($Actions | Where-Object { $_ } | ForEach-Object { "<action content=`"$(& $esc $_.Text)`" arguments=`"$(& $esc $_.Uri)`" activationType=`"protocol`"/>" })
  if ($Reminder) { $btns += '<action content="تم" arguments="dismiss" activationType="system"/>' }
  $acts = if ($btns.Count -gt 0) { '<actions>' + ($btns -join '') + '</actions>' } else { '' }
  $snd  = if ($Reminder) { 'ms-winsoundevent:Notification.Reminder' } else { 'ms-winsoundevent:Notification.Default' }
  $xml  = $head + '<visual><binding template="ToastGeneric">' + $body +
          "<image placement=`"appLogoOverride`" hint-crop=`"circle`" src=`"$icon`"/>" +
          '</binding></visual>' + $acts + "<audio src=`"$snd`"/></toast>"
  $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
  $doc.LoadXml($xml)
  [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($App).Show([Windows.UI.Notifications.ToastNotification]::new($doc))
}

function Show-PrayerToast($Prayer, [string]$Dhikr, [switch]$IsTest) {
  $title = if ($IsTest) { "تجربة التنبيه: صلاة $($Prayer.Name) القادمة" } else { "حان الآن وقت صلاة $($Prayer.Name)" }
  Show-Toast $title @("$(Format-Time $Prayer.Time) · بتوقيت $City", $Dhikr) -Reminder
}

function Show-EventToast($Ev) {
  switch ($Ev.Kind) {
    'Prayer'  { Show-PrayerToast $Ev.Prayer (Get-RandomDhikr) }
    'Before'  { Show-Toast ("باقي " + (ConvertTo-ArabicDigits "$RemindBeforeMinutes") + " دقائق على صلاة " + $Ev.Prayer.Name) @("الأذان الساعة $(Format-Time $Ev.Prayer.Time) · بتوقيت $City") }
    'Morning' { Show-Toast 'حان وقت أذكار الصباح' @('أصبحنا وأصبح الملك لله، والحمد لله، لا إله إلا الله وحده لا شريك له') }
    'Evening' { Show-Toast 'حان وقت أذكار المساء' @('أمسينا وأمسى الملك لله، والحمد لله، لا إله إلا الله وحده لا شريك له') }
    'Friday'  { Show-Toast 'جمعة مباركة' @('لا تنسَ قراءة سورة الكهف، والإكثار من الصلاة على النبي صلى الله عليه وسلم') }
    'Duha'    { Show-Toast 'حان وقت صلاة الضحى' @('ركعتان أو أكثر، ووقتها إلى قبيل الظهر') }
    'Sleep'   { Show-Toast 'أذكار النوم' @('باسمك اللهم أموت وأحيا', 'ولا تنسَ قراءة سورة الملك قبل النوم') }
    'Fasting' { Show-Toast 'تذكير بالصيام' @($Ev.Text, 'تسحّروا فإن في السحور بركة') }
  }
}

# ---------------------------------------------------------------- scheduling
# Everything that should pop a notification on a given day. Window = minutes after its time it may still be shown
# (e.g. the PC was asleep), so a late heads-up never shows up after the prayer has already started.
function Get-Events([datetime]$Day) {
  $times = Get-PrayerTimes $Day
  $list = New-Object System.Collections.Generic.List[object]
  foreach ($p in ($times | Where-Object Prayer)) {
    $list.Add([pscustomobject]@{ Kind = 'Prayer'; Key = $p.Key; Time = $p.Time; Prayer = $p; Window = 15 })
    if ($RemindBeforeMinutes -gt 0) {
      $list.Add([pscustomobject]@{ Kind = 'Before'; Key = "before-$($p.Key)"; Time = $p.Time.AddMinutes(-$RemindBeforeMinutes); Prayer = $p; Window = [math]::Max(1, $RemindBeforeMinutes - 3) })
    }
  }
  if ($MorningEveningAdhkar) {
    $fajr = $times | Where-Object Key -eq 'Fajr'; $asr = $times | Where-Object Key -eq 'Asr'
    $list.Add([pscustomobject]@{ Kind = 'Morning'; Key = 'morning'; Time = $fajr.Time.AddMinutes(20); Prayer = $fajr; Window = 60 })
    $list.Add([pscustomobject]@{ Kind = 'Evening'; Key = 'evening'; Time = $asr.Time.AddMinutes(20); Prayer = $asr; Window = 60 })
  }
  if ($FridayReminder -and $Day.DayOfWeek -eq [DayOfWeek]::Friday) {
    $list.Add([pscustomobject]@{ Kind = 'Friday'; Key = 'friday'; Time = $Day.Date.AddHours(9); Prayer = $null; Window = 180 })
  }
  if ($DuhaReminder) {
    $sun = $times | Where-Object Key -eq 'Sunrise'
    $list.Add([pscustomobject]@{ Kind = 'Duha'; Key = 'duha'; Time = $sun.Time.AddMinutes(20); Prayer = $null; Window = 60 })
  }
  if ($SleepReminder) {
    $list.Add([pscustomobject]@{ Kind = 'Sleep'; Key = 'sleep'; Time = $Day.Date.AddHours(23); Prayer = $null; Window = 60 })
  }
  if ($FastingReminder) {
    # Tomorrow is Monday / Thursday / a white day - but never on days fasting is not allowed or already obligatory
    $tom = $Day.Date.AddDays(1)
    $cal = New-Object Globalization.UmAlQuraCalendar
    $hm = $cal.GetMonth($tom); $hd = $cal.GetDayOfMonth($tom)
    $skip = ($hm -eq 9) -or ($hm -eq 10 -and $hd -eq 1) -or ($hm -eq 12 -and $hd -ge 10 -and $hd -le 13)
    $dayName = if ($tom.DayOfWeek -eq [DayOfWeek]::Monday) { 'الاثنين' } elseif ($tom.DayOfWeek -eq [DayOfWeek]::Thursday) { 'الخميس' } else { $null }
    $white = $hd -ge 13 -and $hd -le 15
    if (-not $skip -and ($dayName -or $white)) {
      $text = if ($dayName -and $white) { "بكرة $dayName، ومن الأيام البيض" } elseif ($dayName) { "بكرة $dayName" } else { 'بكرة من الأيام البيض' }
      $list.Add([pscustomobject]@{ Kind = 'Fasting'; Key = 'fasting'; Time = $Day.Date.AddHours(21); Prayer = $null; Window = 120; Text = $text })
    }
  }
  $list | Sort-Object Time
}

function Update-AlertTriggers([datetime]$Now) {
  $events = @(Get-Events $Now.Date) + @(Get-Events $Now.Date.AddDays(1)) | Where-Object { $_.Time -gt $Now }
  $triggers = @(foreach ($e in $events) { New-ScheduledTaskTrigger -Once -At (ConvertTo-LocalTime $e.Time) })
  if ($triggers.Count -gt 0 -and (Get-ScheduledTask -TaskName $TaskAlert -ErrorAction SilentlyContinue)) {
    Set-ScheduledTask -TaskName $TaskAlert -Trigger $triggers | Out-Null
  }
}

function Get-NextPrayer([datetime]$Now) {
  $today = Get-PrayerTimes $Now.Date
  $next = $today | Where-Object { $_.Prayer -and $_.Time -gt $Now } | Select-Object -First 1
  if ($next) { return @{ Times = $today; Next = $next; Date = $Now.Date; IsTomorrow = $false } }
  $tomorrow = Get-PrayerTimes $Now.Date.AddDays(1)
  @{ Times = $tomorrow; Next = ($tomorrow | Where-Object Prayer | Select-Object -First 1); Date = $Now.Date.AddDays(1); IsTomorrow = $true }
}

# Prayer times (local clock) for the live countdown in adhkar-dua.exe
function Write-PrayerFile([datetime]$Now) {
  $lines = foreach ($d in @($Now.Date, $Now.Date.AddDays(1))) {
    foreach ($p in (Get-PrayerTimes $d | Where-Object Prayer)) { '{0:yyyy-MM-dd}T{0:HH:mm}|{1}' -f (ConvertTo-LocalTime $p.Time), $p.Name }
  }
  Set-Content -LiteralPath (Join-Path $Root 'next-prayers.txt') -Value $lines -Encoding UTF8
}

# Same picture on the lock screen (Windows keeps its own copy, so the PNG can be deleted afterwards)
function Set-LockScreenImage([string]$Path) {
  try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $null = [Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType = WindowsRuntime]
    $methods = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 }
    $asTaskOp  = $methods | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    $asTaskAct = $methods | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
    $getFile = $asTaskOp.MakeGenericMethod([Windows.Storage.StorageFile]).Invoke($null, @([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)))
    [void]$getFile.Wait(30000)
    $setImg = $asTaskAct.Invoke($null, @([Windows.System.UserProfile.LockScreen]::SetImageFileAsync($getFile.Result)))
    [void]$setImg.Wait(30000)
  } catch { Write-Log ('lock screen failed: ' + $_.Exception.Message) }
}

# Current Makkah weather for the desktop clock (open-meteo.com, free, no key). Keeps the last value on failure.
function Update-Weather {
  if (-not $Weather) { return }
  try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $u = "https://api.open-meteo.com/v1/forecast?latitude=$Lat&longitude=$Lng&current=temperature_2m,weather_code&timezone=Asia%2FRiyadh"
    $r = Invoke-RestMethod -Uri $u -TimeoutSec 15 -UseBasicParsing
    $c = [int]$r.current.weather_code
    $desc = if ($c -eq 0) { 'صافٍ' } elseif ($c -eq 1) { 'صافٍ غالبًا' } elseif ($c -eq 2) { 'غائم جزئيًا' } elseif ($c -eq 3) { 'غائم' }
            elseif ($c -in 45, 48) { 'ضباب' } elseif ($c -in 51, 53, 55, 56, 57) { 'رذاذ' } elseif ($c -in 61, 63, 66) { 'مطر' }
            elseif ($c -in 65, 67, 82) { 'مطر غزير' } elseif ($c -in 80, 81) { 'زخات مطر' } elseif ($c -in 95, 96, 99) { 'عاصفة رعدية' }
            elseif ($c -in 71, 73, 75, 77, 85, 86) { 'ثلج' } else { '' }
    $line = '{0}|{1}|{2:yyyy-MM-dd}T{2:HH:mm}|{3}' -f [math]::Round([double]$r.current.temperature_2m), $desc, (Get-Date), $CityShort
    Set-Content -LiteralPath (Join-Path $Root 'weather.txt') -Value $line -Encoding UTF8
  } catch { Write-Log ('weather failed: ' + $_.Exception.Message) }
}

# Auto-update: settings.ini "update_url" = a web folder holding version.txt (line 1 = version, line 2 = SHA-256 of the zip)
# and Adhkar-Desktop-Setup.zip. Checked at most twice a day; only installs a zip whose checksum matches.
function Invoke-UpdateCheck {
  $base = $Settings['update_url']
  if (-not $base) { return }
  $stamp = Join-Path $Root 'update-checked.txt'
  if ((Test-Path $stamp) -and ((Get-Date) - (Get-Item $stamp).LastWriteTime).TotalHours -lt 12) { return }
  Set-Content -LiteralPath $stamp -Value (Get-Date -Format o)
  try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $base = $base.TrimEnd('/')
    $info = "$((Invoke-WebRequest -Uri "$base/version.txt" -UseBasicParsing -TimeoutSec 20).Content)" -split "`r?`n"
    $remote = $info[0].Trim(); $sha = if ($info.Count -gt 1) { $info[1].Trim().ToUpper() } else { '' }
    if (-not $sha -or [version]$remote -le [version]$Version) { return }
    $tmp = Join-Path $env:TEMP ('adhkar-update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    $zip = Join-Path $tmp 'update.zip'
    Invoke-WebRequest -Uri "$base/Adhkar-Desktop-Setup.zip" -OutFile $zip -UseBasicParsing -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $sha) { Write-Log 'update: checksum mismatch, not installed'; return }
    Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
    $src = Get-ChildItem -LiteralPath $tmp -Directory | Select-Object -First 1
    # every source file in the package (so new files arrive too); adhkar.txt stays, it may hold your own duas
    $files = @(Get-ChildItem -LiteralPath $src.FullName -File | Where-Object { $_.Extension -eq '.cs' -or $_.Name -in 'adhkar.ps1', 'uninstall.cmd' })
    foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination $Root -Force; Unblock-File -LiteralPath (Join-Path $Root $f.Name) }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    # rebuild once this refresh (and the launcher running it) has finished
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-Command',
      "Start-Sleep -Seconds 8; & '$(Join-Path $Root 'adhkar.ps1')' -Mode Setup"
    Show-Toast "تم تحديث $AppName للنسخة $remote" @('فيه ميزات جديدة، الأداة بتعيد تشغيل نفسها خلال ثواني') -App $HelperAppId -IconFile 'shield.png'
    Write-Log "updated to $remote"
  } catch { Write-Log ('update check failed: ' + $_.Exception.Message) }
}

function Invoke-Refresh {
  Initialize-Registration
  $now  = Get-MakkahNow
  $info = Get-NextPrayer $now
  $png  = Join-Path $Root ('wallpaper-{0:yyyyMMdd-HHmmss}.png' -f (Get-Date))
  New-Wallpaper -Times $info.Times -NextKey $info.Next.Key -Date $info.Date -IsTomorrow $info.IsTomorrow -Dhikr (Get-RandomDhikr) -OutFile $png
  Set-Wallpaper $png
  Set-LockScreenImage $png
  Write-PrayerFile $now
  Update-Weather
  try { Set-RestScreen } catch { Write-Log ('rest screen: ' + $_.Exception.Message) }
  Invoke-UpdateCheck
  Get-ChildItem -LiteralPath $Root -Filter 'wallpaper-*.png' | Where-Object { $_.FullName -ne $png } | Remove-Item -Force -ErrorAction SilentlyContinue
  Update-AlertTriggers $now
  Write-Log "wallpaper updated, next: $($info.Next.Key) $($info.Next.Time)"
}

function Invoke-Alert {
  $now = Get-MakkahNow
  $due = @(Get-Events $now.Date | Where-Object { $m = ($now - $_.Time).TotalMinutes; $m -ge -2 -and $m -le $_.Window })
  $sf = Join-Path $Root 'shown-alerts.txt'
  $shown = @(if (Test-Path $sf) { Get-Content -LiteralPath $sf })
  $muted = $false                                              # "كتم التذكيرات ساعة" from the widget's control panel
  $mf = Join-Path $Root 'mute-until.txt'
  if (Test-Path $mf) { try { $muted = [DateTime]::new([long]"$(Get-Content -LiteralPath $mf -Raw)".Trim(), 'Utc') -gt [DateTime]::UtcNow } catch {} }
  foreach ($e in $due) {
    $stamp = '{0:yyyy-MM-dd}-{1}' -f $now.Date, $e.Key
    if ($shown -contains $stamp) { continue }
    if ($muted) { $shown += $stamp; Write-Log "muted: $stamp"; continue }
    Show-EventToast $e
    $shown += $stamp
    Write-Log "alert shown: $stamp"
  }
  if ($due.Count -eq 0) { Write-Log 'nothing due in the alert window' }
  $shown | Select-Object -Last 40 | Set-Content -LiteralPath $sf
  Invoke-Refresh
}

# Icon + notification sender name. Runs on every refresh (from the scheduled task), so it always lands in the real user registry.
function Initialize-Registration {
  $icon = Join-Path $Root 'icon.png'
  if (-not (Test-Path $icon)) { New-Icon $icon }
  $shield = Join-Path $Root 'shield.png'
  if (-not (Test-Path $shield)) { New-ShieldIcon $shield }
  # the sender names shown on every notification (rewritten each time, so a rename reaches old installs too)
  foreach ($a in @(@($AppId, $AppName, $icon), @($HelperAppId, "$AppName · حماية الجهاز", $shield))) {
    $k = "HKCU:\Software\Classes\AppUserModelId\$($a[0])"
    if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
    $cur = Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue
    if ($cur.DisplayName -ne $a[1]) { New-ItemProperty -Path $k -Name DisplayName -Value $a[1] -PropertyType String -Force | Out-Null }
    if ($cur.IconUri -ne $a[2]) { New-ItemProperty -Path $k -Name IconUri -Value $a[2] -PropertyType String -Force | Out-Null }
    if ($cur.ShowInSettings -ne 1) { New-ItemProperty -Path $k -Name ShowInSettings -Value 1 -PropertyType DWord -Force | Out-Null }
  }

  # keep the widget's control-panel icon visible next to the taskbar clock (not hidden under ^)
  Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue | ForEach-Object {
    $ip = Get-ItemProperty -LiteralPath $_.PSPath
    if ($ip.ExecutablePath -like '*\adhkar-dua.exe' -and $ip.IsPromoted -ne 1) { Set-ItemProperty -LiteralPath $_.PSPath -Name IsPromoted -Value 1 -Type DWord }
  }

  $cu = [Microsoft.Win32.Registry]::CurrentUser
  # adhkar-kill:<token> protocol = the "اقفله الحين" button on frozen-app alerts (adhkar-dua.exe checks the one-time token)
  $pk = $cu.CreateSubKey('Software\Classes\adhkar-kill'); $pk.SetValue('', 'URL:Adhkar kill'); $pk.SetValue('URL Protocol', ''); $pk.Close()
  $pc = $cu.CreateSubKey('Software\Classes\adhkar-kill\shell\open\command'); $pc.SetValue('', "`"$(Join-Path $Root 'adhkar-dua.exe')`" `"%1`""); $pc.Close()
  # right-click > "فحص الملف (حِصن)" on any file or folder
  $ico = Join-Path $Root 'shield.ico'
  if (-not (Test-Path $ico)) { ConvertTo-Ico $shield $ico }
  foreach ($base in 'Software\Classes\*\shell\AdhkarScan', 'Software\Classes\Directory\shell\AdhkarScan') {
    $v = $cu.CreateSubKey($base); $v.SetValue('', "فحص الملف ($AppName)"); $v.SetValue('Icon', $ico); $v.Close()
    $c = $cu.CreateSubKey("$base\command"); $c.SetValue('', "`"$(Join-Path $Root 'adhkar-run.exe')`" ScanFile `"%1`""); $c.Close()
  }
  # right-click > "جرّبه بأمان (صندوق معزول)" = open a copy inside Windows Sandbox with no internet
  foreach ($base in 'Software\Classes\*\shell\AdhkarSandbox', 'Software\Classes\Directory\shell\AdhkarSandbox') {
    $v = $cu.CreateSubKey($base); $v.SetValue('', 'جرّبه بأمان (صندوق معزول)'); $v.SetValue('Icon', $ico); $v.Close()
    $c = $cu.CreateSubKey("$base\command"); $c.SetValue('', "`"$(Join-Path $Root 'adhkar-run.exe')`" Sandbox `"%1`""); $c.Close()
  }
}

# Right-click > "جرّبه بأمان": copies the file into a staging folder and opens it in Windows Sandbox -
# a throw-away Windows with NO internet, no clipboard and no access to your files or passwords.
# Everything inside is wiped when the sandbox window is closed; the staging copy is deleted too.
function Invoke-Sandbox([string]$Path) {
  Initialize-Registration
  if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
  $exe = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
  if (-not (Test-Path $exe)) {
    Show-Toast 'الصندوق المعزول مو مفعّل' @('ابدأ ← اكتب «تشغيل ميزات Windows» ← ✓ Windows Sandbox ← أعد التشغيل') -Reminder -App $HelperAppId -IconFile 'shield.png'
    return
  }
  if (Get-Process -Name 'WindowsSandbox*' -ErrorAction SilentlyContinue) {
    Show-Toast 'فيه صندوق معزول مفتوح' @('Windows يسمح بصندوق واحد بس. سكّر اللي مفتوح وجرّب مرة ثانية') -App $HelperAppId -IconFile 'shield.png'
    return
  }
  $item  = Get-Item -LiteralPath $Path -Force
  $stage = Join-Path $Root 'sandbox\in'
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
  New-Item -ItemType Directory -Force $stage | Out-Null
  Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse -Force
  $inside = 'C:\Users\WDAGUtilityAccount\Desktop\Test'
  $wsb = Join-Path $Root 'sandbox\run.wsb'
  @"
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ProtectedClient>Enable</ProtectedClient>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$([Security.SecurityElement]::Escape($stage))</HostFolder>
      <SandboxFolder>$inside</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>explorer.exe $inside</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsb -Encoding UTF8
  Show-Toast "جاري فتح «$($item.Name)» في الصندوق المعزول" @('بدون إنترنت وما يشوف ملفاتك. لما تسكّر الصندوق ينمسح كل شي') -App $HelperAppId -IconFile 'shield.png'
  Write-Log "sandbox: $($item.Name)"
  Start-Process -FilePath $exe -ArgumentList "`"$wsb`""
  # wait for the sandbox to open and then close (max 24h), then delete the staging copy
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (-not (Get-Process -Name 'WindowsSandbox*' -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalMinutes -lt 3) { Start-Sleep -Seconds 3 }
  while ((Get-Process -Name 'WindowsSandbox*' -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalHours -lt 24) { Start-Sleep -Seconds 15 }
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# PNG -> .ico (a single 256px PNG frame, which Windows reads fine)
function ConvertTo-Ico([string]$Png, [string]$Ico) {
  $bytes = [IO.File]::ReadAllBytes($Png)
  $ms = New-Object IO.MemoryStream
  $bw = New-Object IO.BinaryWriter($ms)
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]22)
  $bw.Write($bytes); $bw.Flush()
  [IO.File]::WriteAllBytes($Ico, $ms.ToArray())
}

# Right-click > "فحص الملف": Defender scan + digital signature + where it was downloaded from
# + a VirusTotal button (only the file's hash goes to VirusTotal, and only if you press it).
function Invoke-ScanFile([string]$Path) {
  Initialize-Registration
  if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
  $item = Get-Item -LiteralPath $Path -Force
  $name = $item.Name
  Show-Toast "جاري فحص «$name»..." @('أعطيك النتيجة بعد شوي') -App $HelperAppId -IconFile 'shield.png'
  $null = & (Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe') -Scan -ScanType 3 -File $item.FullName
  $code = $LASTEXITCODE                                    # 0 = clean, 2 = threat found (Defender quarantines it)
  $lines = New-Object System.Collections.Generic.List[string]
  $acts = @()
  if ($code -eq 2) { $title = "⛔ «$name» فيه فيروس"; $lines.Add('Defender عزله عشان ما يضرّك. لا تشغّله ولا ترجّعه') }
  elseif ($code -eq 0) { $title = "✅ Defender ما لقى شي في «$name»" }
  else { $title = "⚠️ ما قدرت أكمل فحص «$name»"; $lines.Add('جرّب مرة ثانية، أو افحصه من أمن Windows') }
  if (-not $item.PSIsContainer -and $code -ne 2) {
    $runnable = '.exe', '.dll', '.msi', '.scr', '.com', '.bat', '.cmd', '.ps1', '.vbs', '.js', '.jar', '.sys', '.cpl', '.hta'
    if ($runnable -contains $item.Extension.ToLowerInvariant()) {
      $sig = Get-AuthenticodeSignature -LiteralPath $item.FullName
      if ($sig.Status -eq 'Valid') {
        $cn = ($sig.SignerCertificate.Subject -split ',' | Where-Object { $_ -match '^\s*CN=' } | Select-Object -First 1) -replace '^\s*CN=', ''
        $lines.Add("موقّع رسمياً من: $cn")
      } elseif ($sig.Status -eq 'NotSigned') {
        $lines.Add('⚠️ غير موقّع: ما نعرف مين سوّاه. أغلب الهاكات وسرّاقين المعلومات كذا')
      } else {
        $lines.Add("⛔ توقيعه خربان أو مزوّر ($($sig.Status)). لا تشغّله")
      }
    }
    try {
      $zone = Get-Content -LiteralPath $item.FullName -Stream Zone.Identifier -ErrorAction Stop
      $src = ($zone | Where-Object { $_ -match '^(HostUrl|ReferrerUrl)=' } | Select-Object -First 1) -replace '^\w+=', ''
      if ($src) { $lines.Add('منزّل من: ' + ([Uri]$src).Host) }
    } catch {}
    $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    $acts += @{ Text = 'تقرير VirusTotal'; Uri = "https://www.virustotal.com/gui/file/$hash" }
  }
  if ($code -eq 0) { $lines.Add('تذكير: ما فيه فحص يضمن 100٪، خصوصاً الهاكات الجديدة') }
  Show-Toast $title $lines.ToArray() -Reminder -App $HelperAppId -IconFile 'shield.png' -Actions $acts
  Write-Log "scan: $name -> $code"
}

function Install-Adhkar {

  # Tiny windowless launcher, so the scheduled runs never flash a console window (or pull you out of a game).
  # Usage: adhkar-run.exe <Mode> [path]   (the path is used by the "فحص الملف" right-click entry)
  $launcher = Join-Path $Root 'adhkar-run.exe'
  $src = @'
using System;
using System.Diagnostics;
using System.IO;
public static class AdhkarRun {
  public static int Main(string[] args) {
    string dir = AppDomain.CurrentDomain.BaseDirectory;
    string mode = args.Length > 0 ? args[0] : "Refresh";
    foreach (char c in mode) if (!char.IsLetter(c)) return 1;        // modes are plain words
    string cmd = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Path.Combine(dir, "adhkar.ps1") + "\" -Mode " + mode;
    if (args.Length > 1) {
      string target = args[1].Replace("\"", "");
      if (target.EndsWith("\\")) target += ".";                      // "C:\" would escape the closing quote
      cmd += " -Target \"" + target + "\"";
    }
    var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe"), cmd);
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;
    psi.WorkingDirectory = dir;
    using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
  }
}
'@
  try { if (Test-Path $launcher) { Remove-Item $launcher -Force -ErrorAction Stop } } catch { Start-Sleep -Seconds 2; Remove-Item $launcher -Force }
  Add-Type -TypeDefinition $src -OutputAssembly $launcher -OutputType WindowsApplication
  $mkAction = { param($m) New-ScheduledTaskAction -Execute $launcher -Argument $m -WorkingDirectory $Root }
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
  $user = "$env:USERDOMAIN\$env:USERNAME"
  $trLogon  = New-ScheduledTaskTrigger -AtLogOn -User $user
  $trHourly = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Hours 1)
  Register-ScheduledTask -TaskName $TaskRefresh -Action (& $mkAction 'Refresh') -Trigger @($trLogon, $trHourly) -Settings $settings -Description 'Adhkar wallpaper: Makkah prayer times + random dhikr (hourly)' -Force | Out-Null
  Register-ScheduledTask -TaskName $TaskAlert -Action (& $mkAction 'Alert') -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddDays(1)) -Settings $settings -Description 'Prayer-time notification (Makkah, Umm al-Qura)' -Force | Out-Null
  Start-ScheduledTask -TaskName $TaskRefresh   # first draw happens from Task Scheduler, outside any app sandbox

  # rotating dua on the desktop
  $widget = Join-Path $Root 'adhkar-dua.exe'
  Get-Process -Name 'adhkar-dua' -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  Add-Type -AssemblyName System.Xaml, System.Windows.Forms, System.Drawing
  # an install updated by an older version may lack a newer source file: fetch it from the project's src folder
  $sources = 'dua-widget.cs', 'widget-ui.cs', 'widget-extras.cs', 'game-library.cs', 'hidio.cs', 'batteries.cs'
  foreach ($f in $sources) {
    $p = Join-Path $Root $f
    if ((Test-Path $p) -or -not $Settings['update_url']) { continue }
    try {
      [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
      Invoke-WebRequest -Uri ($Settings['update_url'].TrimEnd('/') + "/src/$f") -OutFile $p -UseBasicParsing -TimeoutSec 60
      Unblock-File -LiteralPath $p; Write-Log "fetched missing $f"
    } catch { Write-Log "could not fetch ${f}: $($_.Exception.Message)" }
  }
  $refs = @([System.Windows.Window].Assembly.Location, [System.Windows.Media.Brush].Assembly.Location,
            [System.Windows.DependencyObject].Assembly.Location, [System.Xaml.XamlReader].Assembly.Location,
            [System.Linq.Enumerable].Assembly.Location, [System.Windows.Forms.NotifyIcon].Assembly.Location,
            [System.Drawing.Bitmap].Assembly.Location)
  Add-Type -Path @($sources | ForEach-Object { Join-Path $Root $_ }) -ReferencedAssemblies $refs -OutputAssembly $widget -OutputType WindowsApplication
  # the same exe is also the rest screen (Windows runs screensavers as <name>.scr /s)
  $rest = Join-Path $Root 'adhkar-rest.scr'
  try { Copy-Item -LiteralPath $widget -Destination $rest -Force -ErrorAction Stop } catch { Write-Log ('rest screen copy: ' + $_.Exception.Message) }
  $wSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName $TaskWidget -Action (New-ScheduledTaskAction -Execute $widget -Argument "$DuaSeconds" -WorkingDirectory $Root) -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) -Settings $wSettings -Description 'Rotating dua on the desktop' -Force | Out-Null
  Start-ScheduledTask -TaskName $TaskWidget
}

function Uninstall-Adhkar {
  $cu = [Microsoft.Win32.Registry]::CurrentUser
  foreach ($k in 'Software\Classes\adhkar-kill', 'Software\Classes\*\shell\AdhkarScan', 'Software\Classes\Directory\shell\AdhkarScan', 'Software\Classes\*\shell\AdhkarSandbox', 'Software\Classes\Directory\shell\AdhkarSandbox', "Software\Classes\AppUserModelId\$HelperAppId") {
    try { $cu.DeleteSubKeyTree($k, $false) } catch {}
  }
  Unregister-ScheduledTask -TaskName $TaskWidget -Confirm:$false -ErrorAction SilentlyContinue
  Get-Process -Name 'adhkar-dua' -ErrorAction SilentlyContinue | Stop-Process -Force
  Unregister-ScheduledTask -TaskName $TaskRefresh -Confirm:$false -ErrorAction SilentlyContinue
  Unregister-ScheduledTask -TaskName $TaskAlert -Confirm:$false -ErrorAction SilentlyContinue
  Remove-Item "HKCU:\Software\Classes\AppUserModelId\$AppId" -Recurse -Force -ErrorAction SilentlyContinue
  $Settings['rest_minutes'] = '0'; try { Set-RestScreen } catch {}            # remove our screensaver
  $default = Join-Path $env:WINDIR 'Web\Wallpaper\Windows\img0.jpg'          # back to the normal Windows wallpaper
  if (Test-Path $default) { Set-Wallpaper $default }
}

try {
  switch ($Mode) {
    'Setup'     { Install-Adhkar }
    'Refresh'   { Invoke-Refresh }
    'Alert'     { Invoke-Alert }
    'Test'      { $n = (Get-NextPrayer (Get-MakkahNow)).Next; Show-PrayerToast $n (Get-RandomDhikr) -IsTest }
    'Notify'    {
      Initialize-Registration
      $all  = @(Get-Content -LiteralPath (Join-Path $Root 'alert.txt') -Encoding UTF8 | Where-Object { $_.Trim() })
      $kill = $all | Where-Object { $_ -like 'kill:*' } | Select-Object -First 1
      $l    = @($all | Where-Object { $_ -notlike 'kill:*' })
      $acts = @(); if ($kill) { $acts += @{ Text = 'اقفله الحين'; Uri = 'adhkar-kill:' + $kill.Substring(5).Trim() } }
      if ($l.Count -gt 0) { Show-Toast $l[0] @($l | Select-Object -Skip 1) -Reminder -App $HelperAppId -IconFile 'shield.png' -Actions $acts; Write-Log "notify: $($l[0])" }
    }
    'Sandbox'   { Invoke-Sandbox $Target }
    'ScanFile'  { Invoke-ScanFile $Target
    }
    'Uninstall' { Uninstall-Adhkar }
    'Preview'   {
      $info = Get-NextPrayer (Get-MakkahNow)
      $info.Times | ForEach-Object { '{0,-8} {1:HH:mm}' -f $_.Key, $_.Time }
      New-Wallpaper -Times $info.Times -NextKey $info.Next.Key -Date $info.Date -IsTomorrow $info.IsTomorrow -Dhikr (Get-RandomDhikr) -OutFile (Join-Path $Root 'preview.png')
    }
  }
} catch {
  Write-Log ('ERROR line {0}: {1}' -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
  throw
}
