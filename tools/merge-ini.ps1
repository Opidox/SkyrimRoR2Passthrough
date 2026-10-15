# 0.71 : ajoute a ton SkyrimRoR2.ini les reglages des nouvelles versions, SANS toucher a tes valeurs.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\merge-ini.ps1 -Modele skse\SkyrimRoR2.ini -Cible "<ton ini>"
#
# - Une section qui manque est ajoutee en entier (avec ses explications) a la fin du fichier.
# - Un reglage qui manque dans une section est ajoute a la fin de cette section, avec ses lignes d'explication.
# - Tout ce qui existe deja chez toi reste tel quel. Une copie de l'ancien fichier est gardee (.avant-0.71).
param(
    [Parameter(Mandatory = $true)][string]$Modele,
    [Parameter(Mandatory = $true)][string]$Cible
)

$ErrorActionPreference = 'Stop'

function Get-Header([string]$line) {
    if ($line -match '^\s*\[([^\]]+)\]\s*$') { return $Matches[1].Trim().ToLowerInvariant() }
    return $null
}

function Get-Key([string]$line) {
    if ($line -match '^\s*([^;#=\[\s][^=]*?)\s*=') { return $Matches[1].Trim().ToLowerInvariant() }
    return $null
}

$model = @(Get-Content -LiteralPath $Modele -Encoding Default)
$user = @(Get-Content -LiteralPath $Cible -Encoding Default)

# Ce que tu as deja : sections et reglages.
$userKeys = @{}
$section = ''
$userKeys[$section] = @{}
foreach ($line in $user) {
    $h = Get-Header $line
    if ($h -ne $null) { $section = $h; if (-not $userKeys.ContainsKey($section)) { $userKeys[$section] = @{} }; continue }
    $k = Get-Key $line
    if ($k -ne $null) { $userKeys[$section][$k] = $true }
}

# Le modele, section par section.
$sections = New-Object System.Collections.ArrayList
$current = @{ Name = ''; Lines = (New-Object System.Collections.ArrayList) }
[void]$sections.Add($current)
foreach ($line in $model) {
    $h = Get-Header $line
    if ($h -ne $null) {
        $current = @{ Name = $h; Lines = (New-Object System.Collections.ArrayList) }
        [void]$sections.Add($current)
    }
    [void]$current.Lines.Add($line)
}

$additions = @{}      # section -> lignes a ajouter a la fin de la section
$newSections = New-Object System.Collections.ArrayList
$added = 0
foreach ($s in $sections) {
    if ($s.Name -eq '') { continue }
    if (-not $userKeys.ContainsKey($s.Name)) {
        # Section absente : en entier, avec les lignes de commentaire qui la precedent dans le modele.
        # (sans les lignes de fin qui annoncent deja la section suivante du modele)
        $last = $s.Lines.Count - 1
        while ($last -gt 0 -and (Get-Key $s.Lines[$last]) -eq $null -and (Get-Header $s.Lines[$last]) -eq $null) { $last-- }
        [void]$newSections.Add('')
        for ($j = 0; $j -le $last; $j++) { [void]$newSections.Add($s.Lines[$j]) }
        $added++
        Write-Host "  + section [$($s.Name)]"
        continue
    }
    $lines = $s.Lines
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $k = Get-Key $lines[$i]
        if ($k -eq $null -or $userKeys[$s.Name].ContainsKey($k)) { continue }
        # Les explications juste au-dessus du reglage.
        $start = $i
        while ($start -gt 0 -and $lines[$start - 1] -match '^\s*;') { $start-- }
        if (-not $additions.ContainsKey($s.Name)) { $additions[$s.Name] = New-Object System.Collections.ArrayList }
        [void]$additions[$s.Name].Add('')
        for ($j = $start; $j -le $i; $j++) { [void]$additions[$s.Name].Add($lines[$j]) }
        $userKeys[$s.Name][$k] = $true
        $added++
        Write-Host "  + [$($s.Name)] $k"
    }
}

if ($added -eq 0) { Write-Host "  Ton SkyrimRoR2.ini est deja a jour."; exit 0 }

# On reconstruit ton fichier : les ajouts vont a la fin de leur section (avant les lignes vides de fin).
$out = New-Object System.Collections.ArrayList
function Flush([string]$name) {
    if (-not $additions.ContainsKey($name)) { return }
    $blank = 0
    while ($out.Count - $blank -gt 0 -and $out[$out.Count - 1 - $blank].Trim() -eq '') { $blank++ }
    $at = $out.Count - $blank
    $out.InsertRange($at, $additions[$name])
    $additions.Remove($name)
}
$section = ''
foreach ($line in $user) {
    $h = Get-Header $line
    if ($h -ne $null) { Flush $section; $section = $h }
    [void]$out.Add($line)
}
Flush $section
foreach ($l in $newSections) { [void]$out.Add($l) }

Copy-Item -LiteralPath $Cible -Destination ($Cible + '.avant-0.71') -Force
Set-Content -LiteralPath $Cible -Value $out -Encoding Default
Write-Host "  $added reglage(s) ajoute(s). Ancien fichier garde : SkyrimRoR2.ini.avant-0.71"
