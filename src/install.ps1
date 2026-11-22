# Installer for "حِصن" (Hisn): pick your city, then everything installs for the current Windows user
# into %USERPROFILE%\Adhkar. No admin rights needed. Remove it any time with uninstall.cmd.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$src  = $PSScriptRoot
$dest = Join-Path $env:USERPROFILE 'Adhkar'

# Saudi cities (all on Arabia Standard Time, Umm al-Qura prayer times)
$cities = [ordered]@{
  'مكة المكرمة' = '21.4225|39.8262'; 'المدينة المنورة' = '24.4672|39.6111'; 'الرياض' = '24.7136|46.6753'; 'جدة' = '21.4858|39.1925'
  'الدمام' = '26.4207|50.0888'; 'الخبر' = '26.2172|50.1971'; 'الطائف' = '21.2703|40.4158'; 'أبها' = '18.2164|42.5053'
  'خميس مشيط' = '18.3000|42.7333'; 'تبوك' = '28.3838|36.5550'; 'بريدة' = '26.3260|43.9750'; 'حائل' = '27.5114|41.7208'
  'جازان' = '16.8892|42.5511'; 'نجران' = '17.5656|44.2289'; 'الأحساء' = '25.3833|49.5833'; 'ينبع' = '24.0890|38.0618'
  'الجبيل' = '27.0174|49.6225'; 'الباحة' = '20.0129|41.4677'; 'عرعر' = '30.9753|41.0381'; 'سكاكا' = '29.9697|40.2064'
}

$font = New-Object System.Drawing.Font('Segoe UI', 11)
$form = New-Object System.Windows.Forms.Form
$AppName = 'حِصن'
$form.Text = "تثبيت $AppName"
$form.RightToLeft = 'Yes'; $form.RightToLeftLayout = $true
$form.ClientSize = New-Object System.Drawing.Size(440, 250)
$form.StartPosition = 'CenterScreen'; $form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false; $form.MinimizeBox = $false
$form.Font = $font
$form.BackColor = [System.Drawing.Color]::FromArgb(18, 18, 18); $form.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)

$title = New-Object System.Windows.Forms.Label
$title.Text = $AppName; $title.Font = New-Object System.Drawing.Font('Segoe UI', 18, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::FromArgb(212, 175, 106); $title.AutoSize = $true; $title.Location = New-Object System.Drawing.Point(20, 16)

$about = New-Object System.Windows.Forms.Label
$about.Text = 'خلفية مواقيت الصلاة والأذكار، تنبيهات الصلاة، الساعة والطقس، حرارة الجهاز، شحن الماوس ويد السوني، وفحص الملفات.'
$about.Size = New-Object System.Drawing.Size(400, 48); $about.Location = New-Object System.Drawing.Point(20, 56)

$lbl = New-Object System.Windows.Forms.Label
$lbl.Text = 'اختر مدينتك:'; $lbl.AutoSize = $true; $lbl.Location = New-Object System.Drawing.Point(20, 116)

$combo = New-Object System.Windows.Forms.ComboBox
$combo.DropDownStyle = 'DropDownList'; $combo.Size = New-Object System.Drawing.Size(250, 30); $combo.Location = New-Object System.Drawing.Point(20, 142)
foreach ($k in $cities.Keys) { [void]$combo.Items.Add($k) }
$combo.SelectedIndex = 0

$btn = New-Object System.Windows.Forms.Button
$btn.Text = 'تثبيت'; $btn.Size = New-Object System.Drawing.Size(120, 38); $btn.Location = New-Object System.Drawing.Point(20, 194)
$btn.BackColor = [System.Drawing.Color]::FromArgb(212, 175, 106); $btn.ForeColor = [System.Drawing.Color]::Black; $btn.FlatStyle = 'Flat'
$btn.DialogResult = 'OK'

$status = New-Object System.Windows.Forms.Label
$status.AutoSize = $true; $status.Location = New-Object System.Drawing.Point(160, 203); $status.ForeColor = [System.Drawing.Color]::FromArgb(160, 160, 160)
$status.Text = 'ما يحتاج صلاحيات مسؤول'

$form.Controls.AddRange(@($title, $about, $lbl, $combo, $btn, $status))
$form.AcceptButton = $btn
if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
$city = [string]$combo.SelectedItem

# copy the tool (keep a friend's own duas if they are reinstalling)
New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($f in 'adhkar.ps1', 'dua-widget.cs', 'widget-ui.cs', 'widget-extras.cs', 'game-library.cs', 'steam-arabic.cs', 'hidio.cs', 'batteries.cs', 'uninstall.cmd') { Copy-Item -LiteralPath (Join-Path $src $f) -Destination $dest -Force }
if (-not (Test-Path (Join-Path $dest 'adhkar.txt'))) { Copy-Item -LiteralPath (Join-Path $src 'adhkar.txt') -Destination $dest }
Get-ChildItem -LiteralPath $dest -File | Unblock-File
Set-Content -LiteralPath (Join-Path $dest 'city.txt') -Value "$city|$($cities[$city])" -Encoding UTF8
# auto-update from the project's GitHub page (keeps any other settings a friend already has)
$ini = Join-Path $dest 'settings.ini'
$lines = @(if (Test-Path $ini) { Get-Content -LiteralPath $ini -Encoding UTF8 | Where-Object { $_ -notmatch '^\s*update_url\s*=' } })
$lines += 'update_url=https://raw.githubusercontent.com/j40f15/adhkar-desktop/main'
Set-Content -LiteralPath $ini -Value $lines -Encoding UTF8

$wait = New-Object System.Windows.Forms.Form
$wait.Text = $AppName; $wait.RightToLeft = 'Yes'; $wait.RightToLeftLayout = $true; $wait.StartPosition = 'CenterScreen'
$wait.ClientSize = New-Object System.Drawing.Size(360, 90); $wait.FormBorderStyle = 'FixedDialog'; $wait.ControlBox = $false; $wait.Font = $font
$wait.BackColor = $form.BackColor; $wait.ForeColor = $form.ForeColor
$wl = New-Object System.Windows.Forms.Label; $wl.Text = 'جاري التثبيت... (أقل من دقيقة)'; $wl.AutoSize = $true; $wl.Location = New-Object System.Drawing.Point(20, 32)
$wait.Controls.Add($wl); $wait.Show(); $wait.Refresh()

$p = Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$dest\adhkar.ps1`"", '-Mode', 'Setup' -WindowStyle Hidden -Wait -PassThru
$wait.Close()

if ($p.ExitCode -eq 0) {
  [System.Windows.Forms.MessageBox]::Show("تم التثبيت ✅`n`nمدينتك: $city`nأيقونة الهلال جنب ساعة شريط المهام فيها كل الإعدادات.`nوللإزالة: شغّل uninstall.cmd", $AppName,
    [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information,
    [System.Windows.Forms.MessageBoxDefaultButton]::Button1, [System.Windows.Forms.MessageBoxOptions]::RtlReading) | Out-Null
} else {
  [System.Windows.Forms.MessageBox]::Show("صار خطأ أثناء التثبيت. شوف الملف:`n$dest\log.txt", $AppName,
    [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error,
    [System.Windows.Forms.MessageBoxDefaultButton]::Button1, [System.Windows.Forms.MessageBoxOptions]::RtlReading) | Out-Null
}
