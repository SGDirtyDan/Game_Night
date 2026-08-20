param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

$resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
$hash = Get-FileHash -LiteralPath $resolved.Path -Algorithm SHA256

[PSCustomObject]@{
    Path = $hash.Path
    Algorithm = $hash.Algorithm
    Hash = $hash.Hash.ToLowerInvariant()
}
