<#
.SYNOPSIS
  ビルド番号の自動インクリメント + publish + デスクトップへのコピーを1コマンドで行うツール(2026-07-31)。

.DESCRIPTION
  使い方(リポジトリ直下から):
    .\tools\bump_and_publish.ps1
  実行ポリシーでブロックされる場合:
    powershell -ExecutionPolicy Bypass -File .\tools\bump_and_publish.ps1

  仕様はNode版(tools/bump_and_publish.mjs)と同一(Node.jsが使える環境ではそちらでも可):
  - バージョン番号は "Major.Minor.Patch.Build" 形式。Major.Minor.Patchは
    src/DanoniEditor.App/DanoniEditor.App.csproj の <Version> タグで人力管理する
    (このスクリプトは一切書き換えない)。
  - Build番号だけは tools/build_number.txt (整数1つだけを書いたテキストファイル)を
    正とし、実行のたびに読み込んで+1したものを新しいBuild番号として使う
    (csprojに既に4桁目が残っていても、それは無視してこのファイルの値を使う)。
  - 実行順序: ①build_number.txtをインクリメント → ②csprojの<Version>を
    "Major.Minor.Patch.新Build" へ書き換え → ③dotnet publish実行 →
    ④publish出力フォルダを丸ごとデスクトップの新規フォルダ
    "publish_Major-Minor-Patch-Build" へコピー、の順。
  - dotnet publishが失敗した場合はデスクトップへのコピーは行わない
    (build_number.txt自体は既にインクリメント済みのままになる。番号を戻したい場合は
    手動で tools/build_number.txt を編集すること)。

.NOTES
  2026-07-31追記: このファイル自体をUTF-8(BOM付き)で保存すること。Windows PowerShell 5.1は
  BOMの無いUTF-8スクリプトを既定のシステムコードページ(日本語環境ではShift-JIS系)として読むため、
  BOMが無いとスクリプト中の日本語リテラルが文字化けする(実害はメッセージ表示のみだが紛らわしい)。
  同じ理由で、csproj/build_number.txt の読み込みも明示的に -Encoding UTF8 を指定している
  (指定を落とすと、対象ファイルにBOMが無い場合に文字化けした内容で上書きしてしまう事故につながる)。
#>

$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $RepoRoot "src\DanoniEditor.App\DanoniEditor.App.csproj"
$BuildNumberPath = Join-Path $ScriptDir "build_number.txt"
# dotnet publishの既定出力先(csproj側の設定に追随。TargetFramework/RuntimeIdentifiersを
# 変更した場合はここも合わせて変更すること)
$PublishDir = Join-Path $RepoRoot "src\DanoniEditor.App\bin\Release\net8.0-windows\win-x64\publish"

function Fail([string]$message) {
    Write-Host "[bump_and_publish] エラー: $message" -ForegroundColor Red
    exit 1
}

# --- ①②: build_number.txtをインクリメントし、csprojの<Version>を書き換える ---

if (-not (Test-Path -LiteralPath $CsprojPath)) { Fail "csprojが見つかりません: $CsprojPath" }
$csprojText = Get-Content -LiteralPath $CsprojPath -Raw -Encoding UTF8

$versionMatch = [regex]::Match($csprojText, '<Version>\s*(\d+)\.(\d+)\.(\d+)(?:\.\d+)?\s*</Version>')
if (-not $versionMatch.Success) {
    Fail "$CsprojPath から <Version>Major.Minor.Patch</Version> を検出できませんでした"
}
$major = $versionMatch.Groups[1].Value
$minor = $versionMatch.Groups[2].Value
$patch = $versionMatch.Groups[3].Value

if (-not (Test-Path -LiteralPath $BuildNumberPath)) {
    Fail "$BuildNumberPath が見つかりません(整数1つだけを書いたファイルを用意してください)"
}
$rawBuild = (Get-Content -LiteralPath $BuildNumberPath -Raw -Encoding UTF8).Trim()
$currentBuild = 0
if (-not [int]::TryParse($rawBuild, [ref]$currentBuild) -or $currentBuild -lt 0) {
    Fail "$BuildNumberPath の内容 ""$rawBuild"" を整数として読み取れませんでした"
}
$build = $currentBuild + 1
Set-Content -LiteralPath $BuildNumberPath -Value $build -Encoding UTF8

$version = "$major.$minor.$patch.$build"
$updatedCsproj = [regex]::Replace($csprojText, '<Version>\s*\d+\.\d+\.\d+(?:\.\d+)?\s*</Version>', "<Version>$version</Version>")
Set-Content -LiteralPath $CsprojPath -Value $updatedCsproj -NoNewline -Encoding UTF8

Write-Host "[bump_and_publish] バージョンを $version に更新しました ($CsprojPath)"

# --- ③: dotnet publish ---
# -c Release: 配布用の最適化ビルド
# -r win-x64 --self-contained true: .NET未インストール環境でも動く自己完結配布
# -p:PublishSingleFile=true: 単一exe化(WPFネイティブ相互運用DLL数個は仕様上どうしても同階層に残る、
#   詳細はdocs/progress_and_tbd_2026-07-22.md 4章参照)
$publishArgs = @(
    "publish",
    "src\DanoniEditor.App\DanoniEditor.App.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true"
)
Write-Host "[bump_and_publish] dotnet $($publishArgs -join ' ') を実行します..."

Push-Location $RepoRoot
try {
    & dotnet @publishArgs
    $publishExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($publishExitCode -ne 0) {
    Fail "dotnet publish が失敗しました(終了コード: $publishExitCode)。デスクトップへのコピーは行いません"
}

# --- ④: publish出力をデスクトップの新規フォルダへコピー ---

if (-not (Test-Path -LiteralPath $PublishDir)) {
    Fail "publish出力フォルダが見つかりません: $PublishDir"
}

$desktopRoot = [Environment]::GetFolderPath("Desktop")
$desktopDir = Join-Path $desktopRoot "publish_$major-$minor-$patch-$build"
if (Test-Path -LiteralPath $desktopDir) {
    Remove-Item -LiteralPath $desktopDir -Recurse -Force
}
New-Item -ItemType Directory -Path $desktopDir -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $desktopDir -Recurse -Force

Write-Host "[bump_and_publish] 完了: $version"
Write-Host "  publish出力: $PublishDir"
Write-Host "  デスクトップへコピー: $desktopDir"
