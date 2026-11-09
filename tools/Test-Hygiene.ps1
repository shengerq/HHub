$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $files = @(& git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE) { throw 'Cannot enumerate repository files' }
    foreach ($file in $files) {
        if ($file -match '(^|/)(bin|obj|artifacts|node_modules|\.git|\.cache|\.deps|\.work|\.codex|\.sol-orchestrator|_deps|build|out)/' -or
            $file -match '(?i)\.(pfx|p12|key|pem|exe|dll|sys|cat|zip|7z|rar|nupkg|hhub|log|dmp|db|sqlite|user)$' -or
            $file -match '(^|/)\.env($|\.)|Sponsor-(qr|design)-') { throw "Forbidden repository path: $file" }
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $file).Length -gt 8MB) { throw "File exceeds 8 MiB: $file" }
        if ($file -match '\.(cs|json|md|txt|ps1|ya?ml|xml|props|config|js|html|css)$') {
            $text = [IO.File]::ReadAllText((Join-Path $root $file))
            if ($text -match 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----') {
                throw "Potential credential in $file; inspect locally (contents withheld)"
            }
        }
    }
    Write-Output "PASS: repository hygiene ($($files.Count) files); pattern checks are not a complete secret audit"
} finally { Pop-Location }
