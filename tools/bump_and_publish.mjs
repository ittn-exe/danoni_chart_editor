// ビルド番号の自動インクリメント + publish + デスクトップへのコピーを1コマンドで行うツール(2026-07-31)。
//
// 使い方(リポジトリ直下から):
//   node tools/bump_and_publish.mjs
//
// 前提・仕様:
// - バージョン番号は "Major.Minor.Patch.Build" 形式。Major.Minor.Patchは
//   src/DanoniEditor.App/DanoniEditor.App.csproj の <Version> タグで人力管理する
//   (このスクリプトは一切書き換えない)。
// - Build番号だけは tools/build_number.txt (整数1つだけを書いたテキストファイル)を
//   正とし、実行のたびに読み込んで+1したものを新しいBuild番号として使う
//   (csprojの<Version>に既に4桁目が残っていても、それは無視してこのファイルの値を使う)。
// - 実行順序: ①build_number.txtをインクリメント → ②csprojの<Version>を
//   "Major.Minor.Patch.新Build" へ書き換え → ③dotnet publish実行 →
//   ④publish出力フォルダを丸ごとデスクトップの新規フォルダ
//   "publish_Major-Minor-Patch-Build" へコピー、の順。
// - dotnet publishが失敗した場合はデスクトップへのコピーは行わない
//   (build_number.txt自体は既にインクリメント済みのままになる。次回実行時は
//   さらに+1されるだけで実害はないが、失敗時に番号を戻したい場合は手動で
//   tools/build_number.txt を編集すること)。

import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';
import { spawnSync } from 'node:child_process';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(SCRIPT_DIR, '..');

const CSPROJ_PATH = path.join(REPO_ROOT, 'src', 'DanoniEditor.App', 'DanoniEditor.App.csproj');
const BUILD_NUMBER_PATH = path.join(SCRIPT_DIR, 'build_number.txt');

// dotnet publishの既定出力先(csproj側の設定に追随。TargetFramework/RuntimeIdentifiersを
// 変更した場合はここも合わせて変更すること)
const PUBLISH_DIR = path.join(
  REPO_ROOT, 'src', 'DanoniEditor.App', 'bin', 'Release', 'net8.0-windows', 'win-x64', 'publish',
);

function fail(message) {
  console.error(`[bump_and_publish] エラー: ${message}`);
  process.exit(1);
}

// --- ①②: build_number.txtをインクリメントし、csprojの<Version>を書き換える ---

function readCurrentVersionParts() {
  const csproj = fs.readFileSync(CSPROJ_PATH, 'utf8');
  const m = csproj.match(/<Version>\s*(\d+)\.(\d+)\.(\d+)(?:\.\d+)?\s*<\/Version>/);
  if (!m) fail(`${CSPROJ_PATH} から <Version>Major.Minor.Patch</Version> を検出できませんでした`);
  return { csproj, major: m[1], minor: m[2], patch: m[3] };
}

function readAndIncrementBuildNumber() {
  if (!fs.existsSync(BUILD_NUMBER_PATH)) {
    fail(`${BUILD_NUMBER_PATH} が見つかりません(整数1つだけを書いたファイルを用意してください)`);
  }
  const raw = fs.readFileSync(BUILD_NUMBER_PATH, 'utf8').trim();
  const current = Number.parseInt(raw, 10);
  if (!Number.isFinite(current) || current < 0) {
    fail(`${BUILD_NUMBER_PATH} の内容 "${raw}" を整数として読み取れませんでした`);
  }
  const next = current + 1;
  fs.writeFileSync(BUILD_NUMBER_PATH, `${next}\n`, 'utf8');
  return next;
}

function writeVersion(csproj, major, minor, patch, build) {
  const newVersion = `${major}.${minor}.${patch}.${build}`;
  const updated = csproj.replace(
    /<Version>\s*\d+\.\d+\.\d+(?:\.\d+)?\s*<\/Version>/,
    `<Version>${newVersion}</Version>`,
  );
  fs.writeFileSync(CSPROJ_PATH, updated, 'utf8');
  return newVersion;
}

const { csproj, major, minor, patch } = readCurrentVersionParts();
const build = readAndIncrementBuildNumber();
const version = writeVersion(csproj, major, minor, patch, build);
console.log(`[bump_and_publish] バージョンを ${version} に更新しました(${path.relative(REPO_ROOT, CSPROJ_PATH)})`);

// --- ③: dotnet publish ---
// -c Release: 配布用の最適化ビルド
// -r win-x64 --self-contained true: .NET未インストール環境でも動く自己完結配布
// -p:PublishSingleFile=true: 単一exe化(WPFネイティブ相互運用DLL数個は仕様上どうしても同階層に残る、
//   詳細はdocs/progress_and_tbd_2026-07-22.md 4章参照)
const publishArgs = [
  'publish',
  path.join('src', 'DanoniEditor.App', 'DanoniEditor.App.csproj'),
  '-c', 'Release',
  '-r', 'win-x64',
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
];
console.log(`[bump_and_publish] dotnet ${publishArgs.join(' ')} を実行します...`);

const result = spawnSync('dotnet', publishArgs, { cwd: REPO_ROOT, stdio: 'inherit', shell: true });
if (result.status !== 0) {
  fail(`dotnet publish が失敗しました(終了コード: ${result.status ?? '不明'})。デスクトップへのコピーは行いません`);
}

// --- ④: publish出力をデスクトップの新規フォルダへコピー ---

if (!fs.existsSync(PUBLISH_DIR)) {
  fail(`publish出力フォルダが見つかりません: ${PUBLISH_DIR}`);
}

const desktopDir = path.join(os.homedir(), 'Desktop', `publish_${major}-${minor}-${patch}-${build}`);
if (fs.existsSync(desktopDir)) {
  fs.rmSync(desktopDir, { recursive: true, force: true });
}
fs.cpSync(PUBLISH_DIR, desktopDir, { recursive: true });

console.log(`[bump_and_publish] 完了: ${version}`);
console.log(`  publish出力: ${PUBLISH_DIR}`);
console.log(`  デスクトップへコピー: ${desktopDir}`);
