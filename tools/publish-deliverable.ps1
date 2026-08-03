<#
.SYNOPSIS
    Publishes a clean copy of this repo into the court delivery repo, with all
    AI-tooling artifacts removed.

.DESCRIPTION
    The court deliverable must contain no evidence of AI-assisted development.
    That means more than skipping files: some tracked files carry AI traces in
    their CONTENT (README references to CLAUDE.md and model labels, the drawio
    file's agent="claude-code" header). This script therefore does five passes:

      1. COPY      - robocopy the repo to the destination, excluding AI
                     config, this tools folder, git internals, and build
                     output.
      2. NORMALIZE - rewrite every copied text file's line endings to LF.
                     robocopy copies this Windows working tree's checked-out
                     CRLF bytes verbatim, but the destination repo's git
                     blobs are stored as LF (checked out as CRLF locally only
                     via core.autocrlf on demand) -- so a raw byte-for-byte
                     copy never matches what git considers clean. Without
                     this step, `git status` in the destination flags nearly
                     every unchanged file as modified (robocopy also gives
                     every file a fresh mtime, so git can't shortcut the stat
                     check), burying the files that actually changed.
      3. SCRUB     - apply targeted in-file replacements to remove AI
                     references, plus a pass that strips bare internal
                     GitHub issue-number references (the court/vendor team
                     has no access to this private repo's tracker, so a
                     dangling "#13" is meaningless noise). Each named scrub
                     warns if its target text was not found (drift detector:
                     if the README changes, update the scrub here).
      4. VERIFY    - scan every copied text file for banned terms and FAIL
                     LOUDLY if anything slipped through. This is the safety
                     net for future files this script doesn't know about yet.
      5. RECONCILE - `git add -A` (staging only, never a commit) so the
                     destination's index reflects reality; see the rationale
                     at that step for why this is necessary even after
                     NORMALIZE.

    The destination's .git folder is never touched, so the target can be a
    working clone of the court repo. Commit/push there manually (a manual git
    commit carries no AI attribution).

.PARAMETER Source
    Repo root to publish from. Defaults to the parent of this script's folder.

.PARAMETER Destination
    Court repo working copy. Defaults to C:\github\sanbernardinocourt\dash-2-demo-signalr-queue.

.PARAMETER Clean
    Delete everything in the destination except .git before copying, so the
    result is an exact snapshot (removes files deleted upstream). Recommended
    for every delivery after the first.

.EXAMPLE
    .\tools\publish-deliverable.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string]$Source = (Split-Path -Parent $PSScriptRoot),
    [string]$Destination = 'C:\github\sanbernardinocourt\dash-2-demo-signalr-queue',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# What never ships. Directories are matched at any depth (robocopy /XD).
# ---------------------------------------------------------------------------
$excludeDirs = @(
    '.git',            # our history (contains AI co-author trailers)
    '.claude',         # Claude Code permissions/config
    'tools',           # this script itself
    'bin', 'obj', '.vs', 'TestResults',          # .NET build output
    'node_modules', 'dist', '.angular'           # Angular build output
)
$excludeFiles = @(
    'CLAUDE.md', 'CLAUDE.local.md',              # AI contributor standards
    'Claude Code starting prompt*.md',           # prompt docs, if ever re-added
    '*.user',
    'architecture.drawio',                       # editable diagram source; ship the rendered .png only
    'architecture.md', 'decisions.md'            # internal engineering detail; README.md is the vendor-facing doc now
)

# ---------------------------------------------------------------------------
# In-file scrubs: exact literal text -> replacement. Empty replacement deletes
# the text. A scrub that finds nothing emits a WARNING so drift is visible.
# ---------------------------------------------------------------------------
# Global token scrubs: literal text -> replacement, applied to EVERY copied text
# file, not one named file. Use for identifiers that recur across the codebase --
# e.g. source-comment references to the AI contributor standards file (CLAUDE.md),
# which appear in many .cs and .md files. Naming each one as its own per-file
# $scrub would be brittle (every reworded comment breaks an exact match) and would
# silently leak the moment someone adds a new reference, which VERIFY then fails
# on. A global token replacement covers them all, including files this script has
# never seen. Applied after the per-file $scrubs (so their sentence-level deletes
# win) and before VERIFY.
$globalScrubs = @(
    # "CLAUDE.md" contains "claude", so every code/doc comment that cites the
    # standards file by name trips the VERIFY banned-term scan. CONTRIBUTING.md is
    # the neutral, conventional stand-in and reads naturally in every surrounding
    # phrasing ("per CONTRIBUTING.md", "CONTRIBUTING.md's C# style", "CONTRIBUTING.md
    # § Workflow"). The file itself is excluded from the deliverable either way, so
    # these were already dangling references to an unshipped doc -- this only
    # changes the name they dangle to.
    @{ Find = 'CLAUDE.md'; Replace = 'CONTRIBUTING.md' }
)

# ---------------------------------------------------------------------------
# Bare internal GitHub issue-number references (docs/decisions.md and friends
# narrate the POC's build history as "issue #13 needed to decide...", "(#17)",
# "#9-#11"). The vendor/court team reading the deliverable has no access to
# this private repo's issue tracker, so a dangling "#13" is meaningless noise
# at best and an accidental hint at internal process at worst. This is a
# regex (not a literal-text $globalScrubs entry) because the phrasing varies
# per instance; it's restricted to *.md so it can never touch a CSS hex color
# (#1a2b3c), a C# preprocessor directive (#region), or a TS private field
# (#fieldName) elsewhere in the tree. The negative lookahead excludes
# markdown anchor links, which share the "#digit" shape but are followed by
# a hyphenated slug, e.g. "(#4-signalrqueuedemoweb--blazor-server...)" --
# real issue refs are followed by punctuation, whitespace, or another "#"
# (a range like "#9-#11"), never by "-<letter>".
$issueRefPattern = '#(\d{1,4})(?!-[a-zA-Z])'


$scrubs = @(
    @{
        File    = 'README.md'
        Find    = " See [CLAUDE.md](CLAUDE.md) for the coding and documentation standards every contributor (human or AI) follows."
        Replace = ''
    }
    @{
        File    = 'README.md'
        Find    = "| ``CLAUDE.md`` | Coding + documentation standards for all contributors. |`n"
        Replace = ''
    }
    # docs/architecture.md, docs/architecture.drawio, and docs/decisions.md are all
    # excluded from the deliverable (see $excludeFiles above) -- internal engineering
    # detail, not vendor-facing. Their "Current repo status" table rows would
    # otherwise describe files that don't exist in the delivered tree.
    @{
        File    = 'README.md'
        Find    = "| ``docs/architecture.md`` | Living architecture doc (Mermaid diagrams, trust boundaries, reconnect protocol). |`n"
        Replace = ''
    }
    @{
        File    = 'README.md'
        Find    = "| ``docs/architecture.drawio`` | Editable diagram source (export to ``architecture.drawio.png`` — workflow in the doc). |`n"
        Replace = ''
    }
    @{
        File    = 'README.md'
        Find    = "| ``docs/decisions.md`` | Dated log of implementation decisions not fully covered by the original spec. |`n"
        Replace = ''
    }
)

# ---------------------------------------------------------------------------
# Verification scan: if ANY of these appear in a copied text file, the publish
# fails. Extend this list before extending the scrubs above.
# ---------------------------------------------------------------------------
$bannedPattern = 'claude|anthropic|copilot|chatgpt|openai|co-authored-by|ai-generated|ai-assisted|model:haiku|model:sonnet|model:opus'
$textExtensions = @(
    '.cs', '.razor', '.csproj', '.slnx', '.props', '.targets',
    '.md', '.json', '.http', '.drawio', '.xml', '.yml', '.yaml',
    '.ts', '.js', '.html', '.css', '.scss',
    '.gitignore', '.dockerignore', '.editorconfig', '.ps1', '.sh', ''
)

# ---------------------------------------------------------------------------
# 1. COPY
# ---------------------------------------------------------------------------
if (-not (Test-Path $Source)) { throw "Source not found: $Source" }
New-Item -ItemType Directory -Force $Destination | Out-Null

if ($Clean) {
    Write-Host "Cleaning $Destination (preserving .git)..." -ForegroundColor Yellow
    Get-ChildItem -Force $Destination |
        Where-Object { $_.Name -ne '.git' } |
        Remove-Item -Recurse -Force -Confirm:$false
}

Write-Host "Copying $Source -> $Destination" -ForegroundColor Cyan
# /E copy subdirs incl. empty; /NFL /NDL quiet output; exit codes 0-7 = success.
robocopy $Source $Destination /E /NFL /NDL /NJH /NJS /NP `
    /XD @($excludeDirs | ForEach-Object { Join-Path $Source $_ }) `
    /XD $excludeDirs `
    /XF $excludeFiles | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
$destinationFiles = Get-ChildItem -Recurse -File $Destination | Where-Object FullName -NotMatch '\\\.git\\'
$script:copied = $destinationFiles.Count
Write-Host "Copied $copied files." -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 2. NORMALIZE - CRLF -> LF on every copied text file (see rationale above).
# *.sh is skipped: .gitattributes already forces `eol=lf` on checkout, so the
# source working tree copy robocopy read was already LF.
# ---------------------------------------------------------------------------
$textFiles = $destinationFiles | Where-Object { $textExtensions -contains $_.Extension -and $_.Extension -ne '.sh' }
$normalized = 0
foreach ($file in $textFiles) {
    $content = Get-Content -Raw $file.FullName
    if ($null -eq $content) { continue }
    $lf = $content -replace "`r`n", "`n" -replace "`r", "`n"
    if ($lf -ne $content) {
        Set-Content -Path $file.FullName -Value $lf -NoNewline
        $normalized++
    }
}
Write-Host "Normalized $normalized file(s) to LF." -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 3. SCRUB
# ---------------------------------------------------------------------------
foreach ($scrub in $scrubs) {
    $path = Join-Path $Destination $scrub.File
    if (-not (Test-Path $path)) {
        Write-Warning "Scrub target file missing: $($scrub.File) - skipped."
        continue
    }
    $content = Get-Content -Raw $path
    # Normalize the Find text's line endings to match the file on disk (git
    # checks out CRLF on Windows; the scrub definitions above use LF).
    $find = $scrub.Find -replace "`r?`n", "`r`n"
    if ($content.Contains($find)) {
        Set-Content -Path $path -Value $content.Replace($find, $scrub.Replace) -NoNewline
        Write-Host "Scrubbed: $($scrub.File)" -ForegroundColor Green
    }
    elseif ($content.Contains($scrub.Find)) {
        Set-Content -Path $path -Value $content.Replace($scrub.Find, $scrub.Replace) -NoNewline
        Write-Host "Scrubbed: $($scrub.File)" -ForegroundColor Green
    }
    else {
        Write-Warning "Scrub text NOT FOUND in $($scrub.File) - the source file changed. Update this script's scrub list."
    }
}

# Global token scrubs across every copied text file (see $globalScrubs above).
$globalScrubFiles = Get-ChildItem -Recurse -File $Destination |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and $textExtensions -contains $_.Extension }
foreach ($g in $globalScrubs) {
    $matched = 0
    foreach ($file in $globalScrubFiles) {
        $content = Get-Content -Raw $file.FullName
        if ($null -eq $content -or -not $content.Contains($g.Find)) { continue }
        Set-Content -Path $file.FullName -Value $content.Replace($g.Find, $g.Replace) -NoNewline
        $matched++
    }
    if ($matched -gt 0) {
        Write-Host "Global-scrubbed '$($g.Find)' -> '$($g.Replace)' in $matched file(s)." -ForegroundColor Green
    }
    else {
        Write-Warning "Global scrub token '$($g.Find)' not found in any file - it may be obsolete; review this script's global scrub list."
    }
}

# Bare issue-number reference scrub (see $issueRefPattern above), *.md only.
$issueRefFiles = $globalScrubFiles | Where-Object Extension -eq '.md'
$issueRefHits = 0
foreach ($file in $issueRefFiles) {
    $content = Get-Content -Raw $file.FullName
    if ($null -eq $content) { continue }
    $replaced = [regex]::Replace($content, $issueRefPattern, '$1')
    if ($replaced -ne $content) {
        Set-Content -Path $file.FullName -Value $replaced -NoNewline
        $issueRefHits += [regex]::Matches($content, $issueRefPattern).Count
    }
}
if ($issueRefHits -gt 0) {
    Write-Host "Stripped $issueRefHits internal issue-number reference(s) from $($issueRefFiles.Count) *.md file(s)." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 4. VERIFY - the safety net. Fails the publish if anything slipped through.
# ---------------------------------------------------------------------------
Write-Host "Verifying no banned terms remain..." -ForegroundColor Cyan
$hits = Get-ChildItem -Recurse -File $Destination |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and $textExtensions -contains $_.Extension } |
    Select-String -Pattern $bannedPattern

if ($hits) {
    Write-Host ""
    Write-Host "PUBLISH FAILED - AI references remain in the deliverable:" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
    Write-Host "Fix by adding a scrub rule or an exclusion above, then re-run with -Clean." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Deliverable is clean: $copied files in $Destination" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 5. RECONCILE - `git add -A`, staging only, no commit.
# ---------------------------------------------------------------------------
# robocopy gives every copied file a fresh mtime, which invalidates git's
# cached stat info for the whole tree ("racy git"). `git status`, run cold
# right after a bulk copy like this, falls back to a cheaper stat-based check
# for files it can't trust the cache for and reports them modified even when
# the content (post NORMALIZE step above) is byte-for-byte identical to what
# HEAD already has -- confirmed by comparing against `git diff`/`hash-object`,
# which do the full clean-filter-aware comparison and show nothing. `git add
# -A` forces that same full comparison for every file and persists the
# result to the index, so a `git status` run immediately afterward reports
# only real changes. This is why the deliverable used to look like it needed
# committing every file on every publish, even when nothing had changed.
# Staging isn't committing -- `git reset` undoes it trivially -- so this is
# safe to do unconditionally; it only runs if the destination is already a
# git working copy (skipped on a from-scratch destination with no .git yet).
if (Test-Path (Join-Path $Destination '.git')) {
    git -C $Destination add -A 2>$null
    Write-Host "Staged the deliverable (git add -A) -- 'git status'/'git diff --cached' now show only real changes." -ForegroundColor Cyan
}

Write-Host "Next: review 'git status'/'git diff --cached' in the destination repo and commit/push manually." -ForegroundColor Green
# Explicit success exit - otherwise robocopy's non-zero success codes (1 = files
# copied) leak out as this script's exit code and look like a failure in CI.
exit 0
