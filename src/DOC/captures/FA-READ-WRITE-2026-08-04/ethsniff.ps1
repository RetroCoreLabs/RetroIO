# Passive observer on the COSMOS Ethernet hub.
#
# The hub (xmsghub.exe, 127.0.0.1:5010) forwards every frame to every other client, so a client
# that never transmits sees the whole segment - including D100 <-> D102 traffic. Wire format is
# RetroCore's: a 5-byte handshake "RETH" + version, then [u16 big-endian length][frame]...
#
# We only WRITE the handshake and never a frame, so nothing we do can disturb the live machines.
param(
    [string]$RemoteHost = "127.0.0.1",
    [int]$Port = 5010,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Seconds = 3600
)

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($RemoteHost, $Port)
$client.NoDelay = $true
$stream = $client.GetStream()

# Handshake: magic 'R','E','T','H' then version 1.
$hello = [byte[]](0x52, 0x45, 0x54, 0x48, 0x01)
$stream.Write($hello, 0, 5)
$stream.Flush()

function Read-Exact([int]$count) {
    $buf = New-Object byte[] $count
    $read = 0
    while ($read -lt $count) {
        $got = $stream.Read($buf, $read, $count - $read)
        if ($got -le 0) { return $null }
        $read += $got
    }
    return $buf
}

$peer = Read-Exact 5
if ($null -eq $peer) { Write-Output "handshake failed"; exit 1 }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$writer = New-Object System.IO.StreamWriter($Out, $false)
$writer.AutoFlush = $true
$writer.WriteLine("# eth sniffer started " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"))

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $prefix = Read-Exact 2
    if ($null -eq $prefix) { break }
    $len = ([int]$prefix[0] -shl 8) -bor [int]$prefix[1]
    if ($len -le 0 -or $len -gt 4096) { break }
    $frame = Read-Exact $len
    if ($null -eq $frame) { break }
    $hex = [System.BitConverter]::ToString($frame).Replace("-", "")
    $writer.WriteLine((Get-Date -Format "HH:mm:ss.fff") + " " + $len + " " + $hex)
}

$writer.WriteLine("# eth sniffer stopped " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"))
$writer.Close()
$stream.Close()
$client.Close()
