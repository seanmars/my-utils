#requires -Modules PSReadLine
<#
.SYNOPSIS
    Interactively select and delete entries from PSReadLine command history,
    so bad/typo'd commands stop showing up on the up/down arrow keys.
.DESCRIPTION
    Reads the PSReadLine history file, lets you multi-select entries to remove,
    then rewrites the file. Deletion is by line index, so duplicate commands are
    handled precisely (only the ones you pick are removed).
#>
[CmdletBinding()]
param(
    [string]$HistoryPath = (Get-PSReadLineOption).HistorySavePath
)

# WinForms checklist picker. Independent checkboxes (CheckOnClick) mean a single
# click toggles one row, so a half-built selection can't be wiped by a stray
# click the way Ctrl/Shift-select in Out-GridView can. Returns checked entries.
function Select-HistoryEntry {
    param([object[]]$Items)
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop

    $form = [System.Windows.Forms.Form]@{
        Text          = 'Select history to DELETE (check = remove; Shift-click = range)'
        StartPosition = 'CenterScreen'
        ClientSize    = [System.Drawing.Size]::new(900, 600)
    }

    $list = [System.Windows.Forms.CheckedListBox]@{
        Dock = 'Fill'; CheckOnClick = $true; HorizontalScrollbar = $true
    }
    $list.Font = [System.Drawing.Font]::new('Consolas', 10)
    $list.BeginUpdate()
    $list.Items.AddRange([object[]]($Items | ForEach-Object { $_.Command }))
    $list.EndUpdate()

    # Shift-click range check: paint anchor->clicked to the clicked row's new state.
    # $bulk guards the reentrant ItemCheck raised by SetItemChecked below.
    $script:anchor = -1
    $script:bulk = $false
    $list.Add_ItemCheck({
        param($s, $e)
        if ($script:bulk) { return }
        $shift = [System.Windows.Forms.Control]::ModifierKeys -band [System.Windows.Forms.Keys]::Shift
        if ($shift -and $script:anchor -ge 0) {
            $script:bulk = $true
            $lo = [Math]::Min($script:anchor, $e.Index)
            $hi = [Math]::Max($script:anchor, $e.Index)
            for ($i = $lo; $i -le $hi; $i++) {
                $s.SetItemChecked($i, $e.NewValue -eq [System.Windows.Forms.CheckState]::Checked)
            }
            $script:bulk = $false
        } else {
            $script:anchor = $e.Index   # fixed anchor until the next plain click
        }
    })

    $uncheck = [System.Windows.Forms.Button]@{ Text = 'Uncheck All'; Dock = 'Top'; Height = 32 }
    $uncheck.Add_Click({ 0..($list.Items.Count - 1) | ForEach-Object { $list.SetItemChecked($_, $false) } }.GetNewClosure())

    $ok = [System.Windows.Forms.Button]@{ Text = 'Delete checked'; Dock = 'Bottom'; Height = 40; DialogResult = 'OK' }

    # Add the Fill control first so the Top/Bottom buttons claim their edges around it.
    $form.Controls.AddRange(@($list, $uncheck, $ok))
    $form.AcceptButton = $ok

    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return @() }
    foreach ($i in $list.CheckedIndices) { $Items[$i] }
}

if (-not (Test-Path -LiteralPath $HistoryPath)) {
    Write-Error "History file not found: $HistoryPath"
    return
}

$lines = @(Get-Content -LiteralPath $HistoryPath)

# Keep the original line index so duplicates can be deleted individually.
$entries = for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].Trim()) { [pscustomobject]@{ Index = $i; Command = $lines[$i] } }
}

$ordered = @($entries | Sort-Object Index -Descending)

# Checklist GUI on Windows; fzf terminal fallback elsewhere.
# ($IsWindows is undefined on Windows PowerShell 5.1, hence the $null check.)
$selected =
    if ($null -eq $IsWindows -or $IsWindows) {
        Select-HistoryEntry -Items $ordered
    } elseif (Get-Command fzf -ErrorAction SilentlyContinue) {
        $picked = $ordered |
            ForEach-Object { "$($_.Index)`t$($_.Command)" } |
            fzf --multi --prompt 'delete> ' --header 'Tab = mark, Enter = confirm'
        foreach ($row in $picked) {
            $idx = [int]($row -split "`t", 2)[0]
            $entries | Where-Object Index -EQ $idx
        }
    } else {
        Write-Error 'Need Windows Forms or fzf for multi-select.'
        return
    }

if (-not $selected) { Write-Host 'Nothing selected. History unchanged.'; return }

$kill = @($selected.Index)
[string[]]$kept = for ($i = 0; $i -lt $lines.Count; $i++) { if ($i -notin $kill) { $lines[$i] } }

# WriteAllLines = UTF-8 without BOM, matching what PSReadLine writes (avoids
# the BOM that Set-Content -Encoding UTF8 adds on Windows PowerShell 5.1).
[System.IO.File]::WriteAllLines($HistoryPath, $kept)

Write-Host "Deleted $($kill.Count) entry(ies) from $HistoryPath" -ForegroundColor Green
Write-Host 'Takes effect in new shells (current session still holds the old in-memory history).' -ForegroundColor DarkGray

# ponytail: line-based, so a multi-line pasted command shows as separate rows;
#           fine for the typo-command case. Add newline-reassembly if you paste blocks.
