// 本体ソース(danoni_constants.js)のg_keyObjから標準テンプレート temp_{key}.json を生成する
import fs from 'fs';

const src = fs.readFileSync('/tmp/dp/js/lib/danoni_constants.js', 'utf-8');

// g_keyObj のオブジェクトリテラルを波括弧バランスで切り出す
const start = src.indexOf('const g_keyObj = {');
let i = src.indexOf('{', start), depth = 0, end = -1;
for (let p = i; p < src.length; p++) {
  const c = src[p];
  if (c === '{') depth++;
  else if (c === '}') { depth--; if (depth === 0) { end = p; break; } }
}
const literal = src.slice(i, end + 1);
const C_FLG_HYPHEN = '-';
const g_keyObj = eval('(' + literal + ')');

// g_copyKeyPtn も抽出(15B_0: '15A_0' 等)
const cpStart = src.indexOf('const g_copyKeyPtn');
let ci = src.indexOf('{', cpStart), cdepth = 0, cend = -1;
for (let p = ci; p < src.length; p++) {
  const c = src[p];
  if (c === '{') cdepth++;
  else if (c === '}') { cdepth--; if (cdepth === 0) { cend = p; break; } }
}
const g_copyKeyPtn = eval('(' + src.slice(ci, cend + 1) + ')');

// setKeyDfVal 相当: chara/pos/div/divMax の未指定補完 + パターンコピー
const KEYS = ['5','7','7i','8','9A','9B','9i','9d','9h','11','11L','11W','11i','11j','12','12i','13','14','14i','15A','15B','16i','17','23'];

function resolveProp(name, ptn) {
  // 直接定義 → copyKeyPtn参照 の順で解決
  if (g_keyObj[`${name}${ptn}`] !== undefined) return g_keyObj[`${name}${ptn}`];
  const cp = g_copyKeyPtn[ptn];
  if (cp) return resolveProp(name, cp);
  return undefined;
}

// frz名の導出(本体 g_escapeStr.frzName + フォールバック規則を完全再現)
const FRZ_REPLACE = [
  ['leftdia','frzLdia'],['rightdia','frzRdia'],
  ['left','frzLeft'],['down','frzDown'],['up','frzUp'],['right','frzRight'],
  ['space','frzSpace'],['iyo','frzIyo'],['gor','frzGor'],['oni','foni'],
];
const toCapitalize = s => s.charAt(0).toUpperCase() + s.slice(1);
function frzName(chara) {
  let n = chara;
  for (const [a,b] of FRZ_REPLACE) n = n.split(a).join(b);
  if (!n.includes('frz') && !n.includes('foni')) n = `frz${toCapitalize(n)}`;
  return n;
}

// stepRtn → noteGraphic / rotationAngle
function graphicOf(rtn) {
  if (typeof rtn === 'number') return { noteGraphic: 'arrow', rotationAngle: rtn };
  return { noteGraphic: String(rtn), rotationAngle: 0 };
}

const outDir = '/home/claude/danoni-editor/template';
fs.mkdirSync(outDir, { recursive: true });

for (const key of KEYS) {
  const ptn = `${key}_0`;
  const chara   = resolveProp('chara', ptn);
  const keyCtrl = resolveProp('keyCtrl', ptn) ?? resolveProp('keyCtrl', g_copyKeyPtn[ptn] ?? '');
  // keyCtrl15B_0 は個別定義があるため resolveProp('keyCtrl', ptn) が直接ヒットする
  // color/stepRtnは「{key}_{ptn}_{colorPtn}」形式。コピー派生(15B_0→15A_0等)はベースptn解決後にサフィックスを付ける
  const basePtn = (p) => g_keyObj[`chara${p}`] !== undefined ? p : (g_copyKeyPtn[p] ? basePtn(g_copyKeyPtn[p]) : p);
  const bp = basePtn(ptn);
  const color   = g_keyObj[`color${bp}_0`] ?? resolveProp('color', `${ptn}_0`);
  const stepRtn = g_keyObj[`stepRtn${bp}_0`] ?? resolveProp('stepRtn', `${ptn}_0`);
  if (!chara || !keyCtrl || !color || !stepRtn) { console.error(`MISSING data for ${key}`, {chara:!!chara,keyCtrl:!!keyCtrl,color:!!color,stepRtn:!!stepRtn}); continue; }

  const keyNum = keyCtrl.length;
  // setKeyDfVal相当の補完
  const pos    = resolveProp('pos', ptn) ?? [...Array(keyNum).keys()];
  const div    = resolveProp('div', ptn) ?? (Math.max(...pos) + 1);
  const divMax = resolveProp('divMax', ptn) ?? (Math.max(...pos) + 1);
  const blank  = g_keyObj[`blank${ptn}`] ?? g_keyObj.blank; // 55

  const divideCnt = div - 1;   // danoni_main.js L11870
  const posMax    = divMax;    // danoni_main.js L11869

  const lanes = chara.map((laneId, j) => {
    const fn = frzName(laneId);
    const defaultFrz = `frz${toCapitalize(laneId)}`;
    const g = graphicOf(stepRtn[j]);
    return {
      laneId,
      dataName: laneId,
      frzDataNameOverride: fn === defaultFrz ? null : fn,
      displayOrder: j,
      keyAssign: keyCtrl[j].join('/'),
      colorGroup: color[j],
      posIndex: pos[j],
      scrollDirection: pos[j] <= divideCnt ? 1 : -1,  // 本体のデフォルトscrollDir導出と同一
      noteGraphic: g.noteGraphic,
      rotationAngle: g.rotationAngle,
      engineLaneNum: j,
    };
  });

  const tpl = {
    keyTypeId: key,
    keyTypeName: `${key}key`,
    keyCount: keyNum,
    comment: `danoniplus本体 g_keyObj(${ptn})より自動生成`,
    blank, divideCnt, posMax,
    lanes,
  };
  fs.writeFileSync(`${outDir}/temp_${key}.json`, JSON.stringify(tpl, null, 2) + '\n');
  console.log(`temp_${key}.json  keyCount=${keyNum} blank=${blank} divideCnt=${divideCnt} posMax=${posMax}`);
}
