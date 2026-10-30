#requires -Version 5.1
<#
YuLiangToolkit verification trigger. See docs/dev/verification.md.

Default mode is intentionally narrow: look at the working tree, map every changed
path to the self-check group(s) it can affect, build once, then run only those
groups. Full `--all` is reserved for cross-layer / global changes and releases.

  .\scripts\check.ps1                  auto: git status -> affected groups -> build -> run
  .\scripts\check.ps1 -DryRun          print the selection and the command, run nothing
  .\scripts\check.ps1 -List            print self-check groups and the path -> group map
  .\scripts\check.ps1 -SelfTest        validate the mapping / wiring / docs (also runs on every call)
  .\scripts\check.ps1 -All             full self-check (release / cross-layer only)
  .\scripts\check.ps1 -Files a.cs,b.md pretend these paths changed (planning / after commit)
  .\scripts\check.ps1 -Groups core,theme   force groups (rare override)
  .\scripts\check.ps1 -SkipBuild       do not call build.ps1 (dotnet run will build)
  .\scripts\check.ps1 -FullAt 8        group-count threshold that escalates to --all

Exit codes: 0 = selected groups pass; 1 = build or self-check failed; 2 = mapping drift / bad args.
Output is ASCII-only on purpose: only FAIL lines and skip notices are echoed; the full
UTF-8 log is at .artifacts/selfcheck-last.log (read it with the file reader, not PowerShell).

Keep this file ASCII-only so Windows PowerShell 5.1 parses it correctly.
#>
[CmdletBinding()]
param(
    [string[]]$Files = @(),
    [string[]]$Groups = @(),
    [switch]$All,
    [switch]$List,
    [switch]$SelfTest,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [int]$FullAt = 8
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $repoRoot 'tests\YuLiangToolkit.FrameworkSelfCheck\Program.cs'
$checkProject = Join-Path $repoRoot 'tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj'
$checkSourceDir = Join-Path $repoRoot 'tests\YuLiangToolkit.FrameworkSelfCheck'
$srcDir = Join-Path $repoRoot 'src'
$mainProject = Join-Path $repoRoot 'YuLiangTookit.csproj'
$solutionFile = Join-Path $repoRoot 'YuLiangToolkit.sln'
$verificationDoc = Join-Path $repoRoot 'docs\dev\verification.md'
$logDir = Join-Path $repoRoot '.artifacts'
$logPath = Join-Path $logDir 'selfcheck-last.log'

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# Chinese markers kept as .NET regex unicode escapes so this file stays ASCII.
$skipMarkerPattern = '\u8df3\u8fc7'   # "skip" / "skipped"

# ---------------------------------------------------------------------------
# Path -> self-check group mapping. First match wins, so specific rules go first.
#   L0 = docs/meta      : no build, no self-check
#   L1 = build only     : visual / no-assertion files, no self-check group
#   L2 = 1~2 groups     : single tool, or a file shared by two groups
#   L3 = 3~7 groups     : several modules / shared shell resources
#   L4 = full --all     : cross-layer, wiring, unknown impact, release
# ALL means "run the full suite"; it is never expanded silently for L2/L3 paths.
# NOTE: mapping coverage is not assertion coverage (see docs/dev/verification.md).
# ---------------------------------------------------------------------------
function New-Rule {
    param(
        [string]$Pattern,
        [string[]]$Groups,
        [string]$Tier,
        [string]$Note,
        [switch]$Fallback
    )
    return [pscustomobject]@{
        Pattern = $Pattern
        Groups = $Groups
        Tier = $Tier
        Note = $Note
        Fallback = [bool]$Fallback
    }
}

$mapping = @(
    # ---------- L0: docs / pure meta ----------
    (New-Rule 'docs/*'                      @() 'L0' 'docs')
    (New-Rule '*.md'                        @() 'L0' 'markdown only')
    (New-Rule 'Data/*'                      @() 'L0' 'repo Data README only')
    (New-Rule 'Config/*'                    @() 'L0' 'repo Config README only')
    (New-Rule 'LICENSE'                     @() 'L0' 'license text')
    (New-Rule 'scripts/*'                   @() 'L0' 'dev tooling: check.ps1 runs its own SelfTest')
    (New-Rule '.gitignore'                  @() 'L0' 'repo meta')
    (New-Rule '.gitattributes'              @() 'L0' 'repo meta')
    (New-Rule '.idea/*'                     @() 'L0' 'IDE meta')
    (New-Rule 'Folder.DotSettings.user'     @() 'L0' 'IDE meta')

    # ---------- L1: build only (no behavioral self-check group) ----------
    (New-Rule 'build.ps1'                   @() 'L1' 'build script: run build.ps1 to verify')
    (New-Rule 'publish.ps1'                 @() 'L1' 'publish script: also run publish before release')
    (New-Rule 'Directory.Build.*'           @() 'L1' 'global build props')
    (New-Rule '*.sln'                       @() 'L1' 'solution file')
    (New-Rule 'global.json'                 @() 'L1' 'SDK pin')
    (New-Rule 'AssemblyInfo.cs'             @() 'L1' 'assembly meta')
    (New-Rule 'Assets/*'                    @() 'L1' 'icons: build + tray/window screenshot')
    (New-Rule 'testdata/*'                  @() 'L1' 'test fixtures: build only')

    # ---------- tests: wiring changes run everything ----------
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/Program.cs'          @('ALL') 'L4' 'self-check wiring')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/SelfCheckScratch.cs' @('ALL') 'L4' 'shared self-check scratch helper')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/*.csproj'            @('ALL') 'L4' 'self-check project')

    # ---------- tests: one module -> its group(s) ----------
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/ThemeChecks.cs'           @('theme') 'L3' 'theme assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/HomeTagWallChecks.cs'     @('shell') 'L3' 'shell assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/NavigationChecks.cs'      @('shell') 'L3' 'shell assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/ParticleChecks.cs'        @('shell') 'L3' 'shell assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/ShellEffectsChecks.cs'    @('shell') 'L3' 'shell assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/BoardGamesChecks.cs'      @('board-games','board-net','board-room') 'L3' 'board rules + net + room')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/ScrollChainingChecks.cs'  @('core') 'L3' 'core scroll chain')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/ChatRoomChecks.cs'        @('chat-room') 'L3' 'chat-room assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/FlashDownloaderChecks.cs' @('flash-downloader') 'L3' 'flash-downloader assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/MapDownloaderChecks.cs'   @('map-downloader') 'L3' 'map-downloader assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/MediaPlayerChecks.cs'     @('media-player') 'L3' 'media-player assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/OsuManiaChecks.cs'        @('osu-mania') 'L3' 'osu-mania assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/StarRatingChecks.cs'      @('osu-mania') 'L3' 'osu-mania assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/PdfReaderChecks.cs'       @('pdf-reader') 'L3' 'pdf-reader assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/PdfTextChecks.cs'         @('pdf-reader') 'L3' 'pdf-reader assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/PdfAssociationChecks.cs'  @('pdf-reader') 'L3' 'pdf-reader assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/PictureViewerChecks.cs'   @('picture-viewer') 'L3' 'picture-viewer assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/KpsChecks.cs'             @('kps') 'L3' 'kps assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/KpsInputChecks.cs'        @('kps') 'L3' 'kps assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/SpugChecks.cs'            @('spug') 'L3' 'spug assertions')
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/MaintenanceChecks.cs'     @('maintenance') 'L3' 'maintenance assertions')

    # ---------- tests: anything else in the self-check project is unknown wiring ----------
    (New-Rule 'tests/YuLiangToolkit.FrameworkSelfCheck/*.cs' @('ALL') 'L4' 'unmapped self-check source' -Fallback)
    (New-Rule 'tests/*'                                      @('ALL') 'L4' 'unknown test source' -Fallback)

    # ---------- src: core public wheels are cross-layer ----------
    (New-Rule 'src/YuLiangToolkit.Core/*' @('ALL') 'L4' 'cross-layer change in Core public wheels')

    # ---------- src: XAML-only tool edits -> build + screenshot, no self-check group ----------
    # (code-behind *.xaml.cs does not match this rule and falls through to its tool group)
    (New-Rule 'src/YuLiangToolkit.Tools.*/Views/*.xaml' @() 'L1' 'tool XAML only: build + screenshot smoke')
    (New-Rule 'src/*/Views/*.xaml'                      @() 'L1' 'tool XAML only: build + screenshot smoke')

    # ---------- src: one tool -> its group(s) ----------
    (New-Rule 'src/YuLiangToolkit.Tools.BatchRename/*'     @('batch-rename') 'L2' 'batch-rename')
    (New-Rule 'src/YuLiangToolkit.Tools.FlashDownloader/*' @('flash-downloader') 'L2' 'flash-downloader')
    (New-Rule 'src/YuLiangToolkit.Tools.ChatRoom/*'        @('chat-room') 'L2' 'chat-room')
    (New-Rule 'src/YuLiangToolkit.Tools.MapDownloader/*'   @('map-downloader') 'L2' 'map-downloader')
    (New-Rule 'src/YuLiangToolkit.Tools.MediaPlayer/*'     @('media-player') 'L2' 'media-player')
    (New-Rule 'src/YuLiangToolkit.Tools.OsuMania/*'        @('osu-mania') 'L2' 'osu-mania')
    (New-Rule 'src/YuLiangToolkit.Tools.BoardGames/*'      @('board-games','board-net','board-room') 'L3' 'board rules + net + room share one project (kept conservative on purpose)')
    (New-Rule 'src/YuLiangToolkit.Tools.PictureViewer/*'   @('picture-viewer') 'L2' 'picture-viewer')
    (New-Rule 'src/YuLiangToolkit.Tools.PdfReader/*'       @('pdf-reader') 'L2' 'pdf-reader')
    (New-Rule 'src/YuLiangToolkit.Tools.Kps/*'             @('kps') 'L2' 'kps')
    (New-Rule 'src/YuLiangToolkit.Tools.SpugCaptcha/*'     @('spug') 'L2' 'spug')
    (New-Rule 'src/YuLiangToolkit.Tools.VideoToAudio/*'    @() 'L1' 'no dedicated group: build + manual smoke (e2e-convert.ps1)')
    (New-Rule 'src/YuLiangToolkit.Tools.Sample/*'          @() 'L1' 'framework template, no dedicated group: build + manual smoke')
    (New-Rule 'src/*'                                      @('ALL') 'L4' 'unmapped src project' -Fallback)

    # ---------- shell (main app): cross-group files MUST come before the broad rules ----------
    (New-Rule 'App.xaml'                  @('theme','shell') 'L3' 'global styles: zero assertions -> screenshot home + one tool page + both themes')
    (New-Rule 'App.xaml.cs'               @('shell') 'L3' 'app startup: no direct assertions -> screenshot smoke too')
    (New-Rule 'MainWindow.*'              @('shell') 'L2' 'main window: no direct assertions -> screenshot smoke too')
    (New-Rule 'Views/Themes/*'            @('theme') 'L2' 'theme tokens (theme group exists for this)')
    (New-Rule 'Views/MiniPlayerPanel.*'   @('shell','media-player') 'L2' 'shared with media-player assertions')
    (New-Rule 'Views/*'                   @('shell') 'L2' 'shell views')
    (New-Rule 'ViewModels/*'              @('shell') 'L2' 'shell view models')
    (New-Rule 'Models/ShellConfig.cs'     @('shell','theme') 'L2' 'shared with theme assertions')
    (New-Rule 'Models/ShellCategories.cs' @('shell','board-games') 'L2' 'shared with board-games assertions')
    (New-Rule 'Models/*'                  @('shell') 'L2' 'shell models')
    (New-Rule 'Converters/*'              @() 'L1' 'no assertions: build + screenshot smoke')
    (New-Rule 'SingleInstance.cs'         @('shell') 'L2' 'single instance')
    (New-Rule 'YuLiangTookit.csproj'      @('ALL') 'L4' 'main project wiring: full + manual checklist (refs / DefaultItemExcludes / no CopyToOutputDirectory / screenshot)')

    # ---------- unknown impact -> full ----------
    (New-Rule '*.xaml' @('shell') 'L2' 'unmapped XAML: shell group + screenshot smoke')
    (New-Rule '*'      @('ALL') 'L4' 'unmapped path' -Fallback)
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Get-ProgramGroups {
    if (-not (Test-Path -LiteralPath $programPath)) {
        Write-Host "ERROR: cannot find $programPath"
        exit 2
    }
    $result = @()
    $text = Get-Content -LiteralPath $programPath -Raw -Encoding UTF8
    $pattern = '(?m)^\s{4}\("([a-z0-9\-]+)",\s*"([^"]*)"\),'
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $result += [pscustomobject]@{ Id = $m.Groups[1].Value; Label = $m.Groups[2].Value }
    }
    return $result
}

function Test-IgnoredPath {
    param([string]$Path)
    if ($Path -match '(^|/)(bin|obj|\.artifacts|\.git|\.idea|\.vs|dist|shots|\.dotnet-cli-home)/') { return $true }
    if ($Path -match '^(FlashGameDownloader|PictureView|MapDownloader)/') { return $true }
    return $false
}

function Resolve-RepoPath {
    param([string]$Path)
    $p = $Path
    if ([System.IO.Path]::IsPathRooted($p)) {
        $rootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([char[]]@('\', '/'))
        $full = [System.IO.Path]::GetFullPath($p)
        if ($full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            $p = $full.Substring($rootFull.Length).TrimStart([char[]]@('\', '/'))
        }
        else {
            return $null
        }
    }
    $p = $p -replace '\\', '/'
    while ($p.StartsWith('./')) { $p = $p.Substring(2) }
    return $p
}

function Expand-InputPath {
    param([string]$Path)
    $p = Resolve-RepoPath $Path
    if (-not $p) { return @() }
    $abs = Join-Path $repoRoot ($p -replace '/', '\')
    if (Test-Path -LiteralPath $abs -PathType Container) {
        $items = @()
        foreach ($file in (Get-ChildItem -LiteralPath $abs -File -Recurse -Force)) {
            $rel = Resolve-RepoPath $file.FullName
            if (-not $rel) { continue }
            if (Test-IgnoredPath $rel) { continue }
            $items += $rel
        }
        return $items
    }
    return @($p)
}

function Get-ChangedFiles {
    if ($Files.Count -gt 0) {
        $list = @()
        foreach ($item in $Files) {
            foreach ($expanded in (Expand-InputPath $item)) { $list += $expanded }
        }
        return @($list | Where-Object { -not (Test-IgnoredPath $_) } | Select-Object -Unique)
    }

    $gitArgs = @('-C', $repoRoot, '-c', 'core.quotePath=false')
    # Native commands may write warnings to stderr (e.g. git core.autocrlf notices);
    # with $ErrorActionPreference = 'Stop' Windows PowerShell 5.1 turns that into a
    # terminating NativeCommandError, so relax it only around the git calls.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $tracked = & git @gitArgs diff --name-only HEAD 2>$null
        $gitExit = $LASTEXITCODE
        if ($gitExit -ne 0) {
            Write-Host 'ERROR: git diff failed; pass -Files <paths> or run this from the repo.'
            exit 2
        }
        $untracked = & git @gitArgs ls-files --others --exclude-standard 2>$null
    }
    finally {
        $ErrorActionPreference = $previousEap
    }
    $all = @()
    foreach ($item in (@($tracked) + @($untracked))) {
        if (-not $item -or $item.Trim().Length -eq 0) { continue }
        $rel = Resolve-RepoPath $item
        if (-not $rel) { continue }
        if (Test-IgnoredPath $rel) { continue }
        $all += $rel
    }
    return @($all | Select-Object -Unique)
}

function Resolve-Mapping {
    param([string]$Path)
    foreach ($rule in $mapping) {
        if ($Path -like $rule.Pattern) { return $rule }
    }
    return $null
}

function Get-Selection {
    param([string[]]$Paths, [switch]$ForceAll, [int]$EscalateAt = 8)
    $groups = @()
    $matched = @()
    $notes = @()
    $unmapped = @()
    $escalate = $false

    foreach ($path in $Paths) {
        $rule = Resolve-Mapping $path
        if (-not $rule) {
            $unmapped += $path
            continue
        }
        $matched += $rule
        if ($rule.Groups -contains 'ALL') {
            $escalate = $true
            $notes += ("{0} -> ALL ({1})" -f $path, $rule.Note)
            continue
        }
        foreach ($group in $rule.Groups) {
            if ($groups -notcontains $group) { $groups += $group }
        }
    }

    if ($ForceAll) { $escalate = $true }
    if ($unmapped.Count -gt 0) {
        $escalate = $true
        $notes += ("unmapped -> ALL: {0}" -f ($unmapped -join ', '))
    }
    if ($groups.Count -ge $EscalateAt) {
        $escalate = $true
        $notes += ("{0} groups affected (>= {1}) -> ALL is cheaper" -f $groups.Count, $EscalateAt)
    }

    $needBuild = $false
    foreach ($rule in $matched) {
        if ($rule.Tier -ne 'L0') { $needBuild = $true }
    }
    if ($escalate) { $needBuild = $true }

    return [pscustomobject]@{
        Groups = $groups
        Notes = $notes
        Unmapped = $unmapped
        Escalate = $escalate
        NeedBuild = $needBuild
    }
}

function Format-Groups {
    param($Groups, [bool]$RunAll)
    if ($RunAll) { return 'ALL' }
    if ($Groups.Count -eq 0) { return '(build only, no group)' }
    return (($Groups | Sort-Object) -join ',')
}

# ---------------------------------------------------------------------------
# Mapping / wiring / docs SelfTest (also runs automatically before every selection)
# ---------------------------------------------------------------------------
function Get-SelfTestErrors {
    $errors = @()
    $programText = Get-Content -LiteralPath $programPath -Raw -Encoding UTF8

    if ($knownIds.Count -lt 1) { $errors += 'no self-check groups parsed from Program.cs' }
    if (($knownIds | Select-Object -Unique).Count -ne $knownIds.Count) { $errors += 'duplicate self-check group ids in Program.cs' }

    # every group id used in any RunGroupModule/RunGroup/EnterGroup call must be known
    foreach ($m in [regex]::Matches($programText, '(?:RunGroupModule|RunGroup|EnterGroup)\("([a-z0-9\-]+)"')) {
        $id = $m.Groups[1].Value
        if ($knownIds -notcontains $id) { $errors += ("Program.cs wiring references unknown group id '{0}'" -f $id) }
    }
    # every group must be wired at least once
    foreach ($id in $knownIds) {
        $wired = [regex]::Matches($programText, ('(?:RunGroupModule|RunGroup|EnterGroup)\("' + [regex]::Escape($id) + '"')).Count
        if ($wired -eq 0) { $errors += ("group '{0}' is not wired into Program.cs at all" -f $id) }
    }

    # mapping ids <-> known ids
    $mappedIds = @($mapping | ForEach-Object { $_.Groups } | Where-Object { $_ -ne 'ALL' } | Select-Object -Unique)
    foreach ($id in $mappedIds) {
        if ($knownIds -notcontains $id) { $errors += ("mapping references unknown group id '{0}'" -f $id) }
    }
    foreach ($id in $knownIds) {
        $reachable = @($mapping | Where-Object { $_.Groups -contains $id }).Count
        if ($reachable -eq 0) { $errors += ("self-check group '{0}' is not reachable from any path mapping" -f $id) }
    }

    # cross-group expectations: files asserted by more than one module must map to all of them
    $expectations = @(
        @{ Path = 'Views/MiniPlayerPanel.xaml.cs';       Groups = @('shell','media-player') }
        @{ Path = 'Models/ShellConfig.cs';              Groups = @('shell','theme') }
        @{ Path = 'Models/ShellCategories.cs';          Groups = @('shell','board-games') }
        @{ Path = 'App.xaml';                           Groups = @('theme','shell') }
        @{ Path = 'Views/Themes/DefaultTheme.xaml';     Groups = @('theme') }
        @{ Path = 'Views/HomeView.xaml';                Groups = @('shell') }
        @{ Path = 'Converters/ColorToBrushConverter.cs'; Groups = @() }
        @{ Path = 'YuLiangTookit.csproj';               Groups = @('ALL') }
        @{ Path = 'tests/YuLiangToolkit.FrameworkSelfCheck/BoardGamesChecks.cs'; Groups = @('board-games','board-net','board-room') }
        @{ Path = 'src/YuLiangToolkit.Tools.VideoToAudio/Views/VideoToAudioView.xaml'; Groups = @() }
        @{ Path = 'src/YuLiangToolkit.Tools.BoardGames/Shared/BoardProtocol.cs'; Groups = @('board-games','board-net','board-room') }
    )
    foreach ($exp in $expectations) {
        $rule = Resolve-Mapping $exp.Path
        if (-not $rule) {
            $errors += ("expected mapping missing for '{0}'" -f $exp.Path)
            continue
        }
        $actual = @($rule.Groups | Sort-Object)
        $expected = @($exp.Groups | Sort-Object)
        if (($actual -join ',') -ne ($expected -join ',')) {
            $errors += ("'{0}' maps to [{1}] but expected [{2}]" -f $exp.Path, ($actual -join ','), ($expected -join ','))
        }
    }

    # every check module must be mapped explicitly and actually wired into Program.cs
    $moduleCount = 0
    foreach ($file in (Get-ChildItem -LiteralPath $checkSourceDir -File -Filter '*Checks.cs' -ErrorAction SilentlyContinue)) {
        $moduleCount++
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $probe = ('tests/YuLiangToolkit.FrameworkSelfCheck/{0}' -f $file.Name)
        $rule = Resolve-Mapping $probe
        if (-not $rule) { $errors += ("check module '{0}' has no mapping rule" -f $file.Name) }
        elseif ($rule.Fallback) { $errors += ("check module '{0}' only hits the fallback rule; map it explicitly" -f $file.Name) }
        if (-not $programText.Contains($stem + '.')) {
            $errors += ("check module '{0}' is not called from Program.cs (assertions would never run)" -f $file.Name)
        }
    }

    # every tool project must be mapped explicitly and referenced by main csproj + solution
    $toolCount = 0
    $mainText = Get-Content -LiteralPath $mainProject -Raw -Encoding UTF8
    $slnText = Get-Content -LiteralPath $solutionFile -Raw -Encoding UTF8
    $checkText = Get-Content -LiteralPath $checkProject -Raw -Encoding UTF8
    $noSelfCheckTools = @('VideoToAudio', 'Sample')
    foreach ($dir in (Get-ChildItem -LiteralPath $srcDir -Directory -ErrorAction SilentlyContinue)) {
        if ($dir.Name -notlike 'YuLiangToolkit.Tools.*') { continue }
        $toolCount++
        $name = $dir.Name.Substring('YuLiangToolkit.Tools.'.Length)
        $token = 'Tools.' + $name
        $probe = ('src/{0}/SomeFile.cs' -f $dir.Name)
        $rule = Resolve-Mapping $probe
        if (-not $rule) { $errors += ("tool project '{0}' has no mapping rule" -f $dir.Name) }
        elseif ($rule.Fallback) { $errors += ("tool project '{0}' only hits the fallback rule; add an explicit mapping" -f $dir.Name) }
        if (-not $mainText.Contains($token)) { $errors += ("tool project '{0}' is not referenced by YuLiangTookit.csproj" -f $name) }
        if (-not $slnText.Contains($token)) { $errors += ("tool project '{0}' is not in YuLiangToolkit.sln" -f $name) }
        if (($noSelfCheckTools -notcontains $name) -and (-not $checkText.Contains($token))) {
            $errors += ("tool project '{0}' has no self-check group but is not whitelisted" -f $name)
        }
    }

    # no-group projects must stay explicitly marked as manual-smoke in the mapping
    foreach ($name in $noSelfCheckTools) {
        $rule = Resolve-Mapping ('src/YuLiangToolkit.Tools.{0}/SomeFile.cs' -f $name)
        if (-not $rule -or $rule.Groups.Count -ne 0 -or $rule.Tier -ne 'L1') {
            $errors += ("tool project '{0}' must stay mapped to build-only (L1, no self-check group)" -f $name)
        }
    }

    # docs must mention every group id (keeps verification.md in sync with code)
    $docText = Get-Content -LiteralPath $verificationDoc -Raw -Encoding UTF8
    foreach ($id in $knownIds) {
        if (-not $docText.Contains('`' + $id + '`')) {
            $errors += ("docs/dev/verification.md does not mention group id '{0}'" -f $id)
        }
    }

    return [pscustomobject]@{
        Errors = $errors
        ToolCount = $toolCount
        ModuleCount = $moduleCount
    }
}

# ---------------------------------------------------------------------------
# Parse groups; -List / -SelfTest modes
# ---------------------------------------------------------------------------
$programGroups = @(Get-ProgramGroups)
$knownIds = @($programGroups | ForEach-Object { $_.Id })
$labels = @{}
foreach ($g in $programGroups) { $labels[$g.Id] = $g.Label }

if ($List) {
    Write-Host 'SELF-CHECK GROUPS (parsed from tests\YuLiangToolkit.FrameworkSelfCheck\Program.cs):'
    foreach ($g in $programGroups) {
        Write-Host (('  {0,-16} {1}' -f $g.Id, $g.Label))
    }
    Write-Host ''
    Write-Host 'PATH -> GROUP MAPPING (first match wins; L0=docs, L1=build only, L2=1~2 groups, L3=3~7, L4=full --all):'
    foreach ($rule in $mapping) {
        $groupText = if ($rule.Groups -contains 'ALL') { 'ALL' } elseif ($rule.Groups.Count -eq 0) { '(none)' } else { ($rule.Groups -join ',') }
        Write-Host (('  {0,-58} -> {1,-34} [{2}] {3}' -f $rule.Pattern, $groupText, $rule.Tier, $rule.Note))
    }
    Write-Host ''
    Write-Host 'Legend: ALL = full --all; (none) = build only, no self-check group.'
    Write-Host 'Reminder: mapping coverage is not assertion coverage; no-group files still need a manual/screenshot smoke.'
    exit 0
}

$selfTestResult = Get-SelfTestErrors
if ($SelfTest) {
    if ($selfTestResult.Errors.Count -eq 0) {
        Write-Host ("SELFTEST OK: {0} groups, {1} tool projects, {2} check modules, {3} mapping rules" -f $knownIds.Count, $selfTestResult.ToolCount, $selfTestResult.ModuleCount, $mapping.Count)
        exit 0
    }
    Write-Host 'SELFTEST DRIFT (fix scripts/check.ps1 mapping / Program.cs wiring / docs; see docs/dev/verification.md):'
    foreach ($err in $selfTestResult.Errors) { Write-Host ("  - {0}" -f $err) }
    exit 2
}
if ($selfTestResult.Errors.Count -gt 0) {
    Write-Host 'MAPPING DRIFT: scripts/check.ps1 is out of sync with Program.cs / src / docs.'
    foreach ($err in ($selfTestResult.Errors | Select-Object -First 10)) { Write-Host ("  - {0}" -f $err) }
    if ($selfTestResult.Errors.Count -gt 10) { Write-Host ("  ... and {0} more (run -SelfTest)" -f ($selfTestResult.Errors.Count - 10)) }
    exit 2
}

# ---------------------------------------------------------------------------
# Select groups
# ---------------------------------------------------------------------------
$changed = @(Get-ChangedFiles)
$selection = $null
$useAll = $false
$finalGroups = @()
$needBuild = $false

if ($All -and $Groups.Count -gt 0) {
    Write-Host 'ERROR: -All and -Groups cannot be combined.'
    exit 2
}

if ($Groups.Count -gt 0) {
    $requested = @()
    foreach ($raw in $Groups) {
        foreach ($part in ($raw -split '[,;]')) {
            $g = $part.Trim()
            if ($g.Length -eq 0) { continue }
            $canonical = @($knownIds | Where-Object { $_ -eq $g })
            if ($canonical.Count -eq 0) {
                Write-Host ("ERROR: unknown self-check group '{0}'. Known: {1}" -f $g, ($knownIds -join ', '))
                exit 2
            }
            if ($requested -notcontains $canonical[0]) { $requested += $canonical[0] }
        }
    }
    $finalGroups = @($knownIds | Where-Object { $requested -contains $_ })
    $needBuild = $true
}
elseif ($All) {
    $useAll = $true
    $needBuild = $true
}
else {
    if ($changed.Count -eq 0) {
        Write-Host '== changed: 0 file(s)'
        Write-Host '== nothing to verify (clean working tree). Use -All for a full run.'
        exit 0
    }
    $selection = Get-Selection -Paths $changed -EscalateAt $FullAt
    $useAll = $selection.Escalate
    $needBuild = $selection.NeedBuild
    if (-not $useAll) {
        $finalGroups = @($knownIds | Where-Object { $selection.Groups -contains $_ })
    }
}

# ---------------------------------------------------------------------------
# Report the selection
# ---------------------------------------------------------------------------
$tier = 'L0'
if ($needBuild) {
    if ($useAll) { $tier = 'L4' }
    elseif ($finalGroups.Count -eq 0) { $tier = 'L1' }
    elseif ($finalGroups.Count -le 2) { $tier = 'L2' }
    else { $tier = 'L3' }
}

Write-Host ("== repo: {0}" -f $repoRoot)
if ($changed.Count -gt 0) {
    Write-Host ("== changed: {0} file(s)" -f $changed.Count)
    $shown = 0
    foreach ($path in $changed) {
        if ($shown -ge 40) {
            Write-Host ("   ... and {0} more" -f ($changed.Count - 40))
            break
        }
        $rule = Resolve-Mapping $path
        $groupText = if (-not $rule) { 'UNMAPPED -> ALL' } elseif ($rule.Groups -contains 'ALL') { 'ALL' } elseif ($rule.Groups.Count -eq 0) { if ($rule.Tier -eq 'L0') { 'docs only' } else { 'build only' } } else { ($rule.Groups -join ',') }
        Write-Host ("   {0} -> {1}" -f $path, $groupText)
        $shown++
    }
}
if ($selection -and $selection.Notes.Count -gt 0) {
    foreach ($note in $selection.Notes) { Write-Host ("   ! {0}" -f $note) }
}
$buildText = if ($needBuild -and -not $SkipBuild) { 'yes' } elseif ($needBuild) { 'skipped (-SkipBuild)' } else { 'no' }
$checkText = if ($tier -eq 'L0') { 'none (docs only)' } else { Format-Groups $finalGroups $useAll }
Write-Host ("== tier: {0} | build: {1} | self-check: {2}" -f $tier, $buildText, $checkText)

$dotnetArgs = @('run', '--project', $checkProject)
if ($needBuild -and -not $SkipBuild) { $dotnetArgs += '--no-build' }
$dotnetArgs += '--'
if ($useAll) { $dotnetArgs += '--all' } else { $dotnetArgs += ($finalGroups -join ',') }
$preview = if (-not $useAll -and $finalGroups.Count -eq 0) { '(no self-check: build only)' } else { 'dotnet ' + ($dotnetArgs -join ' ') }

if ($DryRun) {
    if (-not $needBuild -and -not $useAll -and $finalGroups.Count -eq 0) {
        Write-Host '== dry run: docs-only change, no build and no self-check.'
        exit 0
    }
    if ($needBuild -and -not $SkipBuild) { Write-Host '== build: .\build.ps1' }
    Write-Host ("== command: {0}" -f $preview)
    Write-Host '== dry run: nothing executed.'
    exit 0
}

if (-not $needBuild -and -not $useAll -and $finalGroups.Count -eq 0) {
    # Docs-only change: nothing to build or run.
    Write-Host '== docs-only change: no build, no self-check.'
    exit 0
}

# ---------------------------------------------------------------------------
# Build once (cheap global compile signal), then run only the selected groups
# ---------------------------------------------------------------------------
if ($needBuild -and -not $SkipBuild) {
    Write-Host '== build: .\build.ps1'
    try {
        & (Join-Path $repoRoot 'build.ps1')
    }
    catch {
        Write-Host ("BUILD FAILED: {0}" -f $_.Exception.Message)
        exit 1
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("BUILD FAILED (exit {0})" -f $LASTEXITCODE)
        exit 1
    }
}

if (-not $useAll -and $finalGroups.Count -eq 0) {
    if ($needBuild -and $SkipBuild) {
        Write-Host '== build skipped (-SkipBuild); no self-check group for this change (compile NOT verified).'
    }
    else {
        Write-Host '== build OK; no self-check group for this change.'
    }
    Write-Host '== no-group/visual change: do a screenshot or manual smoke, then commit (see docs/dev/verification.md).'
    exit 0
}

Write-Host ("== self-check: {0}" -f (Format-Groups $finalGroups $useAll))

# Never let the self-check inherit a user YLT_HOME (it must not touch real Data/Config).
$previousYltHome = $env:YLT_HOME
$selfCheckHome = Join-Path $logDir ('selfcheck-home-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $selfCheckHome | Out-Null
$env:YLT_HOME = $selfCheckHome

# dotnet may write warnings to stderr; keep the preference relaxed while capturing.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $rawOutput = @(& dotnet @dotnetArgs 2>&1)
    $exit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousEap
    if ($null -eq $previousYltHome) { Remove-Item Env:YLT_HOME -ErrorAction SilentlyContinue }
    else { $env:YLT_HOME = $previousYltHome }
}

$output = @($rawOutput | ForEach-Object {
    if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() }
    else { [string]$_ }
})

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$output | Set-Content -LiteralPath $logPath -Encoding UTF8

$passCount = @($output | Where-Object { $_ -match '^PASS\s*\|' }).Count
$failLines = @($output | Where-Object { $_ -match '^FAIL\s*\|' })
$failCount = $failLines.Count

if ($failCount -gt 0) {
    Write-Host ("== FAIL lines ({0}):" -f $failCount)
    $printed = 0
    foreach ($line in $failLines) {
        if ($printed -ge 25) { Write-Host '   ... (see log for the rest)'; break }
        Write-Host ("   {0}" -f $line)
        $printed++
    }
}

# Fail-open cases (a "skipped" case is reported as PASS by some modules). Surface them
# for network/SendInput groups where skipping is a known possibility.
$networkGroups = @('map-downloader','flash-downloader','chat-room','board-net','board-room','kps')
$selectedNetwork = @($networkGroups | Where-Object { $useAll -or ($finalGroups -contains $_) })
if ($selectedNetwork.Count -gt 0) {
    $skipPassLines = @($output | Where-Object { $_ -match '^PASS\s*\|' -and $_ -match $skipMarkerPattern })
    if ($skipPassLines.Count -gt 0) {
        Write-Host ("NOTE: {0} PASS line(s) mention skipped cases (skipped != failed, but review the log)." -f $skipPassLines.Count)
        $printedSkip = 0
        foreach ($line in $skipPassLines) {
            if ($printedSkip -ge 5) { break }
            Write-Host ("   {0}" -f $line)
            $printedSkip++
        }
    }
}

Write-Host ("RESULT: exit={0} pass={1} fail={2} tier={3} groups={4}" -f $exit, $passCount, $failCount, $tier, (Format-Groups $finalGroups $useAll))
if ($exit -ne 0) {
    Write-Host ("LOG: {0}" -f $logPath)
}
exit $exit
