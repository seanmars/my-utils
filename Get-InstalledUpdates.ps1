# Get-InstalledUpdates.ps1
# 列出目前 OS 所有已安裝的更新檔案編號

param(
    [switch]$ExportCsv,
    [string]$CsvPath = ".\InstalledUpdates_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
)

# 強制 console 輸出為 UTF-8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "正在查詢已安裝的 Windows 更新..." -ForegroundColor Cyan

# 方法1: 透過 WMI 查詢
$wmiUpdates = Get-WmiObject -Class Win32_QuickFixEngineering |
    Select-Object HotFixID, Description, InstalledOn, InstalledBy |
    Sort-Object InstalledOn -Descending

# 方法2: 透過 Windows Update Session (含更多資訊)
try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $historyCount = $searcher.GetTotalHistoryCount()
    $wuHistory = $searcher.QueryHistory(0, $historyCount) |
        Where-Object { $_.ResultCode -eq 2 } |  # 2 = Succeeded
        ForEach-Object {
            $kbMatch = [regex]::Match($_.Title, 'KB\d+')
            [PSCustomObject]@{
                KB          = if ($kbMatch.Success) { $kbMatch.Value } else { 'N/A' }
                Title       = $_.Title
                Date        = $_.Date
                Description = $_.Description
            }
        } |
        Where-Object { $_.KB -ne 'N/A' } |
        Sort-Object Date -Descending
} catch {
    $wuHistory = $null
    Write-Warning "無法存取 Windows Update 歷史記錄: $_"
}

# 輸出 WMI 結果
Write-Host "`n=== 已安裝的 Hotfix / 更新 (Win32_QuickFixEngineering) ===" -ForegroundColor Yellow
Write-Host ("共找到 {0} 筆" -f $wmiUpdates.Count) -ForegroundColor Green
$wmiUpdates | Format-Table -AutoSize

# 輸出 Windows Update 歷史
if ($wuHistory) {
    Write-Host "`n=== Windows Update 安裝歷史 (成功安裝) ===" -ForegroundColor Yellow
    Write-Host ("共找到 {0} 筆" -f $wuHistory.Count) -ForegroundColor Green
    $wuHistory | Format-Table KB, Date, Title -AutoSize
}

# 匯出 CSV
if ($ExportCsv) {
    $wmiUpdates | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "`n已匯出至: $CsvPath" -ForegroundColor Green
}

# 純 KB 編號清單 (分來源顯示)
Write-Host "`n=== KB 編號清單 ===" -ForegroundColor Yellow

$wmiKBs = $wmiUpdates.HotFixID | Sort-Object -Unique
Write-Host "`n--- WMI (Win32_QuickFixEngineering) ---" -ForegroundColor Cyan
$wmiKBs | ForEach-Object { Write-Host $_ }

$wuKBs = if ($wuHistory) { $wuHistory.KB | Sort-Object -Unique } else { @() }
Write-Host "`n--- Windows Update 歷史 ---" -ForegroundColor Cyan
$wuKBs | ForEach-Object { Write-Host $_ }

$allKBs = ($wmiKBs + $wuKBs) | Sort-Object -Unique
Write-Host "`n共 $($allKBs.Count) 個唯一 KB 編號 (WMI: $($wmiKBs.Count), WU: $($wuKBs.Count))" -ForegroundColor Green
