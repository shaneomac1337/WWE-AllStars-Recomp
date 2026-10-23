# CPU use of each thread of a running game, by host thread name (online-latency ticket 02).
# Samples every thread's processor time twice, -Seconds apart, and prints the busiest ones
# in percent of one core. Names come from GetThreadDescription: guest threads named
# through SetThreadName show as "<name> (<handle>)", e.g. "Transport Thread (F80000A4)".
#   pwsh scripts/thread-cpu.ps1 -ProcessId <pid> [-Seconds 10] [-Top 12] [-Match 'Transport']
# A session's pid is in analysis/session-<name>/session.json.
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [double]$Seconds = 10,
    [int]$Top = 12,
    # Only print threads whose name matches this regex.
    [string]$Match = ''
)
$ErrorActionPreference = 'Stop'

Add-Type -Namespace WweThreadCpu -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern System.IntPtr OpenThread(uint access, bool inherit, uint id);
[DllImport("kernel32.dll")]
public static extern bool CloseHandle(System.IntPtr handle);
[DllImport("kernel32.dll")]
public static extern int GetThreadDescription(System.IntPtr thread, out System.IntPtr description);
[DllImport("kernel32.dll")]
public static extern System.IntPtr LocalFree(System.IntPtr memory);
'@

function Get-ThreadName([int]$Id) {
    $handle = [WweThreadCpu.Native]::OpenThread(0x0800, $false, [uint32]$Id)  # THREAD_QUERY_LIMITED_INFORMATION
    if ($handle -eq [IntPtr]::Zero) { return '' }
    try {
        $text = [IntPtr]::Zero
        if ([WweThreadCpu.Native]::GetThreadDescription($handle, [ref]$text) -lt 0) { return '' }
        try { [Runtime.InteropServices.Marshal]::PtrToStringUni($text) } finally { [void][WweThreadCpu.Native]::LocalFree($text) }
    } finally { [void][WweThreadCpu.Native]::CloseHandle($handle) }
}

function Get-ThreadTimes {
    $times = @{}
    foreach ($thread in (Get-Process -Id $ProcessId).Threads) {
        try { $times[$thread.Id] = $thread.TotalProcessorTime.TotalMilliseconds } catch { }  # exited meanwhile
    }
    $times
}

$before = Get-ThreadTimes
$clock = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Milliseconds ([int]($Seconds * 1000))
$after = Get-ThreadTimes
$elapsed = $clock.Elapsed.TotalMilliseconds

$rows = foreach ($id in $after.Keys) {
    if (!$before.ContainsKey($id)) { continue }
    [pscustomobject]@{
        Thread = $id
        Name = Get-ThreadName $id
        CorePercent = [math]::Round(100 * ($after[$id] - $before[$id]) / $elapsed, 1)
    }
}
if ($Match) { $rows = $rows | Where-Object Name -Match $Match }
$all = ($rows | Measure-Object CorePercent -Sum).Sum
"pid $ProcessId over $([math]::Round($elapsed / 1000, 1)) s: $([math]::Round($all, 1)) % of one core in $(@($rows).Count) threads" +
    $(if ($Match) { " matching '$Match'" } else { '' })
$rows | Sort-Object CorePercent -Descending | Select-Object -First $Top | Format-Table -AutoSize | Out-String -Width 200
