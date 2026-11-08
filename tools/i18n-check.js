// Checks this mod's interface translations: node tools/i18n-check.js [--init]
//   Each mod's i18n/_source.json holds { "English text": "Arabic text (reference)" }; each i18n/<code>.json must hold
//   { "English text": "translation" } for every key, with the same {placeholders}. --init creates missing files as {}.
const fs = require("fs"), path = require("path");
const MODS = ["AutoTranslate/i18n"];
const CODES = ["de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-HANS", "zh-HANT"];
const root = path.join(__dirname, "..");
const placeholders = (s) => (s.match(/\{\w+\}/g) || []).sort().join(" ");
let problems = 0;
for (const dir of MODS) {
  const full = path.join(root, dir);
  const source = JSON.parse(fs.readFileSync(path.join(full, "_source.json"), "utf8"));
  const keys = Object.keys(source);
  for (const code of CODES) {
    const file = path.join(full, code + ".json");
    if (!fs.existsSync(file)) {
      if (process.argv.includes("--init")) fs.writeFileSync(file, "{}\n");
      else { console.log(`${dir}/${code}.json: missing`); problems++; }
      continue;
    }
    let table;
    try { table = JSON.parse(fs.readFileSync(file, "utf8")); }
    catch (e) { console.log(`${dir}/${code}.json: invalid JSON (${e.message})`); problems++; continue; }
    const missing = keys.filter((k) => typeof table[k] !== "string" || table[k].trim() === "");
    const extra = Object.keys(table).filter((k) => !(k in source));
    const badPh = keys.filter((k) => typeof table[k] === "string" && table[k] && placeholders(k) !== placeholders(table[k]));
    if (missing.length || extra.length || badPh.length) {
      problems++;
      console.log(`${dir}/${code}.json: ${missing.length} missing, ${extra.length} unknown keys, ${badPh.length} placeholder mismatches`);
      for (const k of missing.slice(0, 5)) console.log("   missing: " + k.slice(0, 80));
      for (const k of extra.slice(0, 5)) console.log("   unknown: " + k.slice(0, 80));
      for (const k of badPh.slice(0, 5)) console.log("   placeholders: " + k.slice(0, 80));
    } else console.log(`${dir}/${code}.json: OK (${keys.length})`);
  }
}
console.log(problems ? `${problems} file(s) with problems` : "All translations complete.");
process.exitCode = problems ? 1 : 0;
