// ユーザー製テンプレート(人力キュレーション)をベースに、本体ソース由来の値で補正し、
// 不足キー種を同じ流儀で生成する折衷スクリプト
import fs from 'fs';

const USER_DIR = '/home/claude/user-templates';
const ENGINE_DIR = '/home/claude/danoni-editor/template';       // 本体ソース由来(前回生成)
const OUT_DIR = '/home/claude/danoni-editor/template-merged';
fs.mkdirSync(OUT_DIR, { recursive: true });

const load = (d, k) => JSON.parse(fs.readFileSync(`${d}/temp_${k}.json`, 'utf-8'));
const ALL_KEYS = ['5','7','7i','8','9A','9B','9d','9h','9i','11','11L','11W','11i','11j','12','12i','13','14','14i','15A','15B','16i','17','23'];
const userKeys = new Set(fs.readdirSync(USER_DIR).map(f => f.slice(5, -5)));

// 角度の同値判定(-135 と 225 等は同じ)
const sameAngle = (a, b) => ((a - b) % 360 + 360) % 360 === 0;

// --- keyAssignラベル変換(本体キーコード→ユーザー版流儀の表示ラベル) ---
const LABEL = { Left:'←', Down:'↓', Up:'↑', Right:'→', Comma:'<', Period:'>', Semicolon:';' };
const toLabel = k => LABEL[k] ?? (/^D\d$/.test(k) ? k.slice(1) : k);
const toKeyAssign = arr => arr.length === 1 ? toLabel(arr[0]) : arr.map(toLabel);

// --- scrollDirection規則(ユーザー版20ファイルから抽出した規則):
//     折返しあり(いずれかのpos > divideCnt)なら pos≤divideCnt→"up"、pos>divideCnt→"down"。折返し無し→全て"down"
const scrollOf = (pos, divideCnt, hasFold) => !hasFold ? 'down' : (pos <= divideCnt ? 'up' : 'down');

// --- displayOrder規則(ユーザー版から抽出): 矢印キー割当レーンが半数未満なら末尾へ移動、それ以外はchara順 ---
const isArrowKey = ka => (Array.isArray(ka) ? ka : [ka]).every(k => '←↓↑→'.includes(k));

const report = [];

for (const key of ALL_KEYS) {
  const engine = load(ENGINE_DIR, key);

  if (userKeys.has(key)) {
    // ユーザー版ベース + 本体値で補正
    const user = load(USER_DIR, key);
    const engMap = Object.fromEntries(engine.lanes.map(l => [l.dataName, l]));
    for (const lane of user.lanes) {
      const e = engMap[lane.dataName];
      if (!sameAngle(lane.rotationAngle, e.rotationAngle)) {
        report.push(`${key}: ${lane.dataName}.rotationAngle ${lane.rotationAngle} → ${e.rotationAngle} (本体stepRtn値で補正)`);
        lane.rotationAngle = e.rotationAngle;
      }
      if (lane.colorGroup !== e.colorGroup) {
        report.push(`${key}: ${lane.dataName}.colorGroup ${lane.colorGroup} → ${e.colorGroup} (本体color値で補正)`);
        lane.colorGroup = e.colorGroup;
      }
    }
    fs.writeFileSync(`${OUT_DIR}/temp_${key}.json`, JSON.stringify(user, null, 2) + '\n');
  } else {
    // 不足キー種をユーザー版の流儀で新規生成
    const hasFold = engine.lanes.some(l => l.posIndex > engine.divideCnt);
    let lanes = engine.lanes.map(l => ({
      ...l,
      keyAssign: toKeyAssign(String(l.keyAssign).split('/')),
      scrollDirection: scrollOf(l.posIndex, engine.divideCnt, hasFold),
    }));
    // displayOrder: 矢印キーレーンが半数未満なら末尾へ
    const arrows = lanes.filter(l => isArrowKey(l.keyAssign));
    if (arrows.length > 0 && arrows.length < lanes.length / 2)
      lanes = [...lanes.filter(l => !isArrowKey(l.keyAssign)), ...arrows];
    lanes.forEach((l, i) => l.displayOrder = i);

    const tpl = { ...engine, comment: '', lanes };
    fs.writeFileSync(`${OUT_DIR}/temp_${key}.json`, JSON.stringify(tpl, null, 2) + '\n');
    report.push(`${key}: ユーザー版流儀で新規生成(displayOrder/keyAssignラベル/scrollDirection文字列)`);
  }
}
console.log(report.join('\n'));
console.log(`\n生成: ${fs.readdirSync(OUT_DIR).length} ファイル`);
