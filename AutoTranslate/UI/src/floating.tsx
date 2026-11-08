import { useEffect, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import mod from "mod.json";
import { collectTexts, panelTitle } from "hook";
import de from "../../i18n/de.json";
import es from "../../i18n/es.json";
import fr from "../../i18n/fr.json";
import it from "../../i18n/it.json";
import ja from "../../i18n/ja.json";
import ko from "../../i18n/ko.json";
import pl from "../../i18n/pl.json";
import ptBR from "../../i18n/pt-BR.json";
import ru from "../../i18n/ru.json";
import zhHans from "../../i18n/zh-HANS.json";
import zhHant from "../../i18n/zh-HANT.json";

/*
 * The floating Auto Translate button (C#: Interface/FloatingControls.cs).
 *
 * - Drag it anywhere (its place is saved in the settings); a click opens its menu.
 * - Menu: translation on / off, the target language (automatic, game, Windows, or a chosen one), the click mode, every
 *   mod or window with its own setting (each can go back to the main translation), hide the button.
 * - Click mode: hovering a window outlines it; clicking it asks how to show that mod (or window): with the main
 *   translation, in its original text, or translated into another language. Clicks in this mode never reach the
 *   window. Esc, a right click or the banner's button leave the mode.
 *
 * Everything here carries data-at-skip so the interface hook never translates it, and data-at-ui so the click mode
 * ignores it. Written for Coherent Gameface: no closest(), no pointer events, layout reads only on user input.
 */

const group = mod.id;

interface Entry {
  id: string;
  label: string;
  kind: "mod" | "panel";
  language: string;
  languageName: string;
}

interface Controls {
  arabic: boolean;
  /** The interface language code ("en", "ar", "de", "zh-HANS"…). */
  lang: string;
  show: boolean;
  x: number;
  y: number;
  enabled: boolean;
  interfaces: boolean;
  mode: "Auto" | "GameLanguage" | "SystemLanguage" | "Manual";
  scope: "ChosenOnly" | "Everything";
  manual: string;
  target: string;
  targetName: string;
  translated: number;
  waiting: number;
  entries: Entry[];
}

interface Language {
  code: string;
  name: string;
}

interface PickResult {
  nonce: number;
  action: "choose" | "applied" | "none" | "error";
  token?: number;
  label?: string;
  kind?: "mod" | "panel";
  current?: string;
  choice?: string;
  languageName?: string;
}

const controls$ = bindValue<string>(group, "controls", "{}");
const pickResult$ = bindValue<string>(group, "pickResult", "");
const languages$ = bindValue<string>(group, "languages", "[]");

const parse = <T,>(json: string, fallback: T): T => {
  if (!json) return fallback;
  try {
    const value = JSON.parse(json);
    return value && typeof value === "object" ? (value as T) : fallback;
  } catch (e) {
    return fallback;
  }
};

const DEFAULT_CONTROLS: Controls = {
  arabic: false, lang: "en", scope: "ChosenOnly", show: false, x: -1, y: -1, enabled: false, interfaces: false, mode: "Auto", manual: "", target: "",
  targetName: "", translated: 0, waiting: 0, entries: [],
};

type Tr = (en: string) => string;
const fill = (template: string, values: Record<string, string | number>) =>
  template.replace(/\{(\w+)\}/g, (m, k) => (k in values ? String(values[k]) : m));

/** The menu texts through tr (English source; other languages from i18n/<code>.json, keyed by the English text). */
const buildText = (tr: Tr) => ({
    title: tr("Auto Translate"),
    on: tr("Translation is on"),
    off: tr("Translation is off"),
    turnOff: tr("Turn off"),
    turnOn: tr("Turn on"),
    language: tr("Translate into"),
    scopeTitle: tr("What to translate"),
    scopes: { ChosenOnly: tr("Only the mods I choose"), Everything: tr("Everything") },
    scopeHint: tr("Use the click mode on a mod's window or options page to choose it."),
    optTranslate: (name: string) => (name ? fill(tr("Translate ({name})"), { name }) : tr("Translate")),
    modes: { Auto: tr("Automatic"), GameLanguage: tr("Game language"), SystemLanguage: tr("Windows language"), Manual: tr("Choose") },
    current: (name: string) => (name ? fill(tr("Now: {name}"), { name }) : tr("No target language (the game and Windows are in English).")),
    pick: tr("Click mode"),
    pickHint: tr("Click a mod window to choose its language: translated, original, or another language"),
    pickOn: tr("Click mode is on"),
    leave: tr("Leave"),
    status: (t: number, w: number) => fill(tr("Translated {translated} · waiting {waiting}"), { translated: t, waiting: w }),
    custom: tr("Mods and windows with their own setting"),
    none: tr("None yet: every mod uses the main translation."),
    restore: tr("Reset"),
    kindMod: tr("mod"),
    kindPanel: tr("window"),
    originalTag: tr("original"),
    hide: tr("Hide button"),
    hideHint: tr("The options page shows it again (Floating button)."),
    close: tr("Close"),
    chooseTitle: (kind: string) => (kind === "mod" ? tr("Show this mod:") : tr("Show this window:")),
    optDefault: (name: string) => (name ? fill(tr("Main translation ({name})"), { name }) : tr("Main translation")),
    optOriginal: tr("Original (not translated)"),
    optOther: tr("Another language:"),
    resDefault: (l: string) => fill(tr("Back to the main translation: {label}"), { label: l }),
    resOriginal: (l: string) => fill(tr("Original text: {label}"), { label: l }),
    resLanguage: (l: string, n: string) => `${l} → ${n}`,
    resNone: tr("This is a game window, or it has nothing to translate."),
    offNote: tr("Translation is off: your choice is saved and shows once you turn it on."),
    offSaved: tr("Saved. Turn translation on to see it."),
    resError: tr("Could not save exclusions.json (see the log)."),
    banner: tr("Click a mod window to choose how it is shown. Esc or right click to leave."),
});

const TABLES: Record<string, Record<string, string>> = {
  de, es, fr, it, ja, ko, pl, "pt-BR": ptBR, ru, "zh-HANS": zhHans, "zh-HANT": zhHant,
};
const BUILT: Record<string, ReturnType<typeof buildText>> = {};
/** The menu texts in an interface language code (English for an unknown one). */
const textFor = (code: string) => {
  if (code === "ar") return TEXT.ar;
  const table = TABLES[code];
  if (!table) return TEXT.en;
  return BUILT[code] || (BUILT[code] = buildText((en) => table[en] || en));
};

const TEXT = {
  en: buildText((en) => en),
  ar: {
    title: "المترجم التلقائي",
    on: "الترجمة مفعّلة",
    off: "الترجمة متوقفة",
    turnOff: "إيقاف",
    turnOn: "تشغيل",
    language: "الترجمة إلى",
    scopeTitle: "وش يترجم",
    scopes: { ChosenOnly: "بس المودات اللي أختارها", Everything: "كل شيء" },
    scopeHint: "اضغط بوضع التحكم على نافذة المود أو صفحة إعداداته عشان تختاره.",
    optTranslate: (name: string) => (name ? `ترجمه (${name})` : "ترجمه"),
    modes: { Auto: "تلقائي", GameLanguage: "لغة اللعبة", SystemLanguage: "لغة ويندوز", Manual: "اختيار" },
    current: (name: string) => (name ? `الحالية: ${name}` : "لا توجد لغة هدف (اللعبة وويندوز بالإنجليزية)."),
    pick: "وضع التحكم بالضغط",
    pickHint: "اضغط على نافذة أي مود واختر لغتها: مترجمة، أو الأصل، أو لغة ثانية",
    pickOn: "وضع التحكم بالضغط شغّال",
    leave: "خروج",
    status: (t: number, w: number) => `مترجم ${t} · ينتظر ${w}`,
    custom: "مودات ونوافذ لها إعداد خاص",
    none: "ما فيه للحين: كل المودات على الترجمة العامة.",
    restore: "إرجاع",
    kindMod: "مود",
    kindPanel: "نافذة",
    originalTag: "الأصل",
    hide: "إخفاء الأيقونة",
    hideHint: "ترجعها من صفحة الإعدادات (الأيقونة العائمة).",
    close: "إغلاق",
    chooseTitle: (kind: string) => (kind === "mod" ? "اعرض هذا المود:" : "اعرض هذه النافذة:"),
    optDefault: (name: string) => (name ? `الترجمة العامة (${name})` : "الترجمة العامة"),
    optOriginal: "الأصل (بدون ترجمة)",
    optOther: "لغة ثانية:",
    resDefault: (l: string) => `رجع للترجمة العامة: ${l}`,
    resOriginal: (l: string) => `بدون ترجمة: ${l}`,
    resLanguage: (l: string, n: string) => `${l} ← ${n}`,
    resNone: "هذه نافذة من اللعبة نفسها، أو ما فيها نص يُترجم.",
    offNote: "الترجمة متوقفة: اختيارك ينحفظ ويظهر أول ما تشغّلها.",
    offSaved: "انحفظ. شغّل الترجمة عشان يظهر.",
    resError: "تعذّر حفظ ملف الاستثناءات (راجع السجل).",
    banner: "اضغط على نافذة أي مود واختر كيف تظهر. للخروج: Esc أو زر الماوس الأيمن.",
  },
};

const BUTTON_SIZE = 46;
const DRAG_THRESHOLD = 4;
const PICK_LIMIT = 400;
const MODES: Controls["mode"][] = ["Auto", "GameLanguage", "SystemLanguage", "Manual"];
const SCOPES: Controls["scope"][] = ["ChosenOnly", "Everything"];

// ---------------------------------------------------------------------------------------------------------------------
// DOM helpers (Gameface: no closest)

const isOwnUi = (node: Node | null): boolean => {
  let current: Node | null = node;
  while (current && current.nodeType === 1) {
    if ((current as Element).hasAttribute("data-at-ui")) return true;
    current = current.parentNode;
  }
  return false;
};

const elementOf = (target: EventTarget | null): Element | null => {
  let node = target as Node | null;
  while (node && node.nodeType !== 1) node = node.parentNode;
  return node as Element | null;
};

/**
 * The window an element belongs to: its outermost ancestor that does not cover the whole screen (the layers that
 * hold the game's and the mods' panels are full screen; a panel is not).
 */
const findPanel = (start: Element): Element => {
  const width = window.innerWidth || 1920;
  const height = window.innerHeight || 1080;
  let candidate: Element = start;
  let current: Element | null = start;
  while (current && current !== document.body && current.nodeType === 1) {
    const rect = current.getBoundingClientRect();
    if (rect.width >= width * 0.92 && rect.height >= height * 0.92) break;
    if (rect.width > 0 && rect.height > 0) candidate = current;
    current = current.parentNode as Element | null;
  }
  return candidate;
};

// ---------------------------------------------------------------------------------------------------------------------
// Styles (inline: one small component, no stylesheet to load)

const S = {
  layer: { position: "absolute", left: "0px", top: "0px", width: "100vw", height: "100vh", pointerEvents: "none", zIndex: 9999 } as any,
  anchor: { position: "absolute", width: BUTTON_SIZE + "rem", height: BUTTON_SIZE + "rem", pointerEvents: "auto" } as any,
  button: (on: boolean, picking: boolean) => ({
    width: BUTTON_SIZE + "rem", height: BUTTON_SIZE + "rem", borderRadius: "50%",
    backgroundColor: picking ? "rgba(255, 170, 0, 0.95)" : "rgba(24, 33, 48, 0.94)",
    border: "2rem solid " + (picking ? "#ffd27a" : on ? "#4fc38a" : "rgba(255,255,255,0.35)"),
    display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "row",
    boxShadow: "0 2rem 8rem rgba(0,0,0,0.45)", cursor: "pointer",
  }) as any,
  glyphA: { fontSize: "17rem", fontWeight: "bold", color: "#ffffff" } as any,
  glyphB: { fontSize: "15rem", fontWeight: "bold", color: "#a6dcff", marginLeft: "2rem" } as any,
  dot: (color: string) => ({
    position: "absolute", right: "1rem", top: "1rem", width: "11rem", height: "11rem", borderRadius: "50%",
    backgroundColor: color, border: "2rem solid rgba(24,33,48,1)",
  }) as any,
  menu: {
    position: "absolute", width: "280rem", padding: "8rem 10rem 10rem 10rem", borderRadius: "8rem",
    backgroundColor: "rgba(24, 33, 48, 0.97)", color: "#ffffff", boxShadow: "0 4rem 16rem rgba(0,0,0,0.5)",
    pointerEvents: "auto",
  } as any,
  header: { display: "flex", flexDirection: "row", alignItems: "center", marginBottom: "6rem" } as any,
  /** The menu's scrolling part (everything under the title). */
  body: (maxHeight: number) => ({ maxHeight: maxHeight + "px", overflowY: "scroll", paddingRight: "4rem", paddingLeft: "4rem" }) as any,
  title: { flex: 1, fontSize: "15rem", fontWeight: "bold", color: "#a6dcff" } as any,
  close: { width: "22rem", height: "22rem", fontSize: "16rem", textAlign: "center", color: "rgba(255,255,255,0.8)", cursor: "pointer" } as any,
  row: { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "6rem" } as any,
  wrapRow: { display: "flex", flexDirection: "row", flexWrap: "wrap", marginTop: "4rem" } as any,
  label: { flex: 1, fontSize: "13rem" } as any,
  bold: { fontSize: "13rem", fontWeight: "bold" } as any,
  small: { fontSize: "11.5rem", color: "rgba(255,255,255,0.65)", marginTop: "4rem" } as any,
  btn: (primary: boolean, active?: boolean) => ({
    padding: "4rem 10rem", borderRadius: "4rem", fontSize: "12.5rem", cursor: "pointer",
    backgroundColor: active ? "rgba(255,170,0,0.9)" : primary ? "rgba(79,195,138,0.85)" : "rgba(255,255,255,0.14)",
    color: active ? "#1a1a1a" : "#ffffff",
  }) as any,
  chip: (active: boolean) => ({
    padding: "3rem 8rem", margin: "2rem", borderRadius: "4rem", fontSize: "12rem", cursor: "pointer",
    backgroundColor: active ? "rgba(79,195,138,0.9)" : "rgba(255,255,255,0.12)", color: "#ffffff",
  }) as any,
  wide: { marginTop: "8rem", padding: "6rem 10rem", borderRadius: "4rem", fontSize: "13rem", textAlign: "center", cursor: "pointer" } as any,
  section: { marginTop: "8rem", paddingTop: "6rem", borderTop: "1rem solid rgba(255,255,255,0.12)" } as any,
  list: (height: number) => ({ maxHeight: height + "rem", overflowY: "scroll", marginTop: "4rem" }) as any,
  item: { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "4rem" } as any,
  itemLabel: { flex: 1, fontSize: "12.5rem", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } as any,
  kind: { fontSize: "11rem", color: "rgba(255,255,255,0.6)", marginLeft: "6rem", marginRight: "6rem" } as any,
  option: (active: boolean) => ({
    padding: "5rem 8rem", marginTop: "3rem", borderRadius: "4rem", fontSize: "12.5rem", cursor: "pointer",
    backgroundColor: active ? "rgba(79,195,138,0.85)" : "rgba(255,255,255,0.08)", color: "#ffffff",
  }) as any,
  link: { marginTop: "10rem", fontSize: "12rem", color: "rgba(255,255,255,0.6)", cursor: "pointer" } as any,
  banner: {
    position: "absolute", left: "50%", top: "70rem", transform: "translateX(-50%)", maxWidth: "760rem",
    display: "flex", flexDirection: "row", alignItems: "center", padding: "8rem 12rem", borderRadius: "6rem",
    backgroundColor: "rgba(255, 170, 0, 0.95)", color: "#1a1a1a", fontSize: "13.5rem", pointerEvents: "auto",
  } as any,
  bannerBtn: { marginLeft: "12rem", marginRight: "12rem", padding: "4rem 10rem", borderRadius: "4rem", backgroundColor: "rgba(0,0,0,0.75)", color: "#ffffff", cursor: "pointer" } as any,
  highlight: { position: "absolute", border: "2rem solid #ffaa00", backgroundColor: "rgba(255,170,0,0.12)", borderRadius: "4rem", pointerEvents: "none" } as any,
  chooser: {
    position: "absolute", width: "300rem", padding: "10rem 12rem 12rem 12rem", borderRadius: "8rem",
    backgroundColor: "rgba(24, 33, 48, 0.98)", color: "#ffffff", boxShadow: "0 4rem 18rem rgba(0,0,0,0.6)",
    border: "2rem solid #ffaa00", pointerEvents: "auto",
  } as any,
  toast: {
    position: "absolute", left: "50%", top: "118rem", transform: "translateX(-50%)", maxWidth: "640rem",
    padding: "8rem 14rem", borderRadius: "6rem", backgroundColor: "rgba(24,33,48,0.96)", color: "#ffffff",
    fontSize: "13.5rem", pointerEvents: "none",
  } as any,
};

interface Rect { left: number; top: number; width: number; height: number; }

/** The choice popup's size in pixels (300rem wide, about 440rem high at 1080p). */
const chooserSize = () => {
  const scale = (window.innerHeight || 1080) / 1080;
  return { w: 330 * scale, h: 390 * scale };
};

/**
 * Where the choice popup opens: beside the clicked window (right, else left, else above or below it), never on top of
 * it, because mod windows can be drawn above this layer and would hide the popup.
 */
const chooserPlace = (rect: Rect | null, click: { x: number; y: number }) => {
  const width = window.innerWidth || 1920;
  const height = window.innerHeight || 1080;
  const size = chooserSize();
  const gap = 10;
  if (!rect) return { x: click.x + 12, y: click.y - 20 };
  const y = Math.max(8, Math.min(height - size.h, rect.top));
  if (rect.left + rect.width + gap + size.w <= width) return { x: rect.left + rect.width + gap, y };
  if (rect.left - gap - size.w >= 0) return { x: rect.left - gap - size.w, y };
  const x = Math.max(8, Math.min(width - size.w, rect.left));
  if (rect.top + rect.height + gap + size.h <= height) return { x, y: rect.top + rect.height + gap };
  if (rect.top - gap - size.h >= 0) return { x, y: rect.top - gap - size.h };
  // The window fills the screen: the side with the most room.
  return { x: rect.left > width - rect.left - rect.width ? 8 : width - size.w - 8, y };
};

interface Chooser {
  token: number;
  label: string;
  kind: string;
  current: string;
  x: number;
  y: number;
}

// ---------------------------------------------------------------------------------------------------------------------

export const FloatingButton = () => {
  const controls = parse<Controls>(useValue(controls$), DEFAULT_CONTROLS);
  const languages = parse<Language[]>(useValue(languages$), []);
  const pickJson = useValue(pickResult$);
  const text = textFor(controls.lang || (controls.arabic ? "ar" : "en"));
  const dir = controls.arabic ? "rtl" : "ltr";

  const [menuOpen, setMenuOpen] = useState(false);
  const [picking, setPicking] = useState(false);
  const [dragPos, setDragPos] = useState<{ x: number; y: number } | null>(null);
  const [highlight, setHighlight] = useState<Rect | null>(null);
  const [chooser, setChooser] = useState<Chooser | null>(null);
  const [toast, setToast] = useState<string>("");
  const anchorRef = useRef<HTMLDivElement | null>(null);
  const lastClick = useRef<{ x: number; y: number }>({ x: 0, y: 0 });
  const lastRect = useRef<Rect | null>(null);
  const chooserRef = useRef<Chooser | null>(null);
  const enabledRef = useRef<boolean>(false);
  enabledRef.current = controls.enabled;
  chooserRef.current = chooser;

  // The answer present when the component mounted is an old one: only newer answers are shown.
  const initial = parse<PickResult | null>(pickJson, null);
  const lastNonce = useRef<number>(initial && typeof initial.nonce === "number" ? initial.nonce : 0);

  const showToast = (message: string) => {
    setToast(message);
    setTimeout(() => setToast((current) => (current === message ? "" : current)), 3500);
  };

  // Answers from C#: the choice to make for a clicked window, or the outcome.
  useEffect(() => {
    const result = parse<PickResult | null>(pickJson, null);
    if (!result || typeof result.nonce !== "number" || result.nonce === lastNonce.current) return;
    lastNonce.current = result.nonce;
    const label = result.label || "";
    if (result.action === "choose" && typeof result.token === "number") {
      const place = chooserPlace(lastRect.current, lastClick.current);
      setChooser({ token: result.token, label, kind: result.kind || "panel", current: result.current || "default", x: place.x, y: place.y });
      return;
    }
    if (result.action === "applied") {
      const choice = result.choice || "default";
      const done = choice === "default" ? text.resDefault(label) : choice === "original" ? text.resOriginal(label) : text.resLanguage(label, result.languageName || choice);
      showToast(enabledRef.current || choice === "original" ? done : done + " — " + text.offSaved);
      return;
    }
    showToast(result.action === "none" ? text.resNone : text.resError);
  }, [pickJson]);

  // Click mode: capture every mouse event before the windows see it.
  useEffect(() => {
    if (!picking) {
      setHighlight(null);
      setChooser(null);
      return;
    }
    const onMove = (e: MouseEvent) => {
      const el = elementOf(e.target);
      if (!el || isOwnUi(el) || chooserRef.current) {
        if (!chooserRef.current) setHighlight(null);
        return;
      }
      const panel = findPanel(el);
      const r = panel.getBoundingClientRect();
      setHighlight({ left: r.left, top: r.top, width: r.width, height: r.height });
    };
    const block = (e: MouseEvent) => {
      if (isOwnUi(elementOf(e.target))) return;
      e.preventDefault();
      e.stopPropagation();
      if ((e as any).stopImmediatePropagation) e.stopImmediatePropagation();
    };
    const onDown = (e: MouseEvent) => {
      if (isOwnUi(elementOf(e.target))) return;
      block(e);
      if (chooserRef.current) {
        // A click outside the open choice closes it.
        setChooser(null);
        return;
      }
      if (e.button === 2) {
        setPicking(false);
        return;
      }
      if (e.button !== 0) return;
      const el = elementOf(e.target);
      if (!el) return;
      lastClick.current = { x: e.clientX, y: e.clientY };
      const panel = findPanel(el);
      const r = panel.getBoundingClientRect();
      lastRect.current = { left: r.left, top: r.top, width: r.width, height: r.height };
      const texts = collectTexts(panel, PICK_LIMIT);
      trigger(group, "pick", JSON.stringify({ texts, label: panelTitle(panel) || (texts.length > 0 ? texts[0] : "") }));
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" || e.keyCode === 27) {
        e.preventDefault();
        e.stopPropagation();
        if (chooserRef.current) setChooser(null);
        else setPicking(false);
      }
    };
    document.addEventListener("mousemove", onMove, true);
    document.addEventListener("mousedown", onDown, true);
    document.addEventListener("mouseup", block, true);
    document.addEventListener("click", block, true);
    document.addEventListener("dblclick", block, true);
    document.addEventListener("contextmenu", block, true);
    document.addEventListener("keydown", onKey, true);
    return () => {
      document.removeEventListener("mousemove", onMove, true);
      document.removeEventListener("mousedown", onDown, true);
      document.removeEventListener("mouseup", block, true);
      document.removeEventListener("click", block, true);
      document.removeEventListener("dblclick", block, true);
      document.removeEventListener("contextmenu", block, true);
      document.removeEventListener("keydown", onKey, true);
    };
  }, [picking]);

  if (!controls.show) return null;

  const width = window.innerWidth || 1920;
  const height = window.innerHeight || 1080;
  // Saved place, kept on screen; default: right side, below the game's top bar.
  const savedX = controls.x >= 0 ? controls.x : -1;
  const savedY = controls.y >= 0 ? controls.y : -1;
  const pos = dragPos || (savedX >= 0 && savedY >= 0 ? { x: Math.min(savedX, width - 50), y: Math.min(savedY, height - 50) } : null);
  const anchorStyle = pos ? { ...S.anchor, left: pos.x + "px", top: pos.y + "px" } : { ...S.anchor, right: "24rem", top: "140rem" };

  const onButtonDown = (e: any) => {
    if (e.button !== 0) return;
    e.stopPropagation();
    const anchor = anchorRef.current;
    if (!anchor) return;
    const rect = anchor.getBoundingClientRect();
    const startX = e.clientX;
    const startY = e.clientY;
    let moved = false;
    let last = { x: rect.left, y: rect.top };
    const onMove = (ev: MouseEvent) => {
      const dx = ev.clientX - startX;
      const dy = ev.clientY - startY;
      if (!moved && Math.abs(dx) + Math.abs(dy) < DRAG_THRESHOLD) return;
      moved = true;
      last = {
        x: Math.max(0, Math.min(width - rect.width, rect.left + dx)),
        y: Math.max(0, Math.min(height - rect.height, rect.top + dy)),
      };
      setDragPos(last);
    };
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      if (moved) {
        trigger(group, "moveButton", JSON.stringify({ x: Math.round(last.x), y: Math.round(last.y) }));
        // Keep showing the dropped place until the saved value comes back.
        setTimeout(() => setDragPos(null), 1500);
      } else {
        setMenuOpen((open) => !open);
      }
    };
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  };

  // The menu opens towards the middle of the screen.
  const anchorX = pos ? pos.x : width - 70;
  const anchorY = pos ? pos.y : 140;
  const menuStyle: any = { ...S.menu };
  if (anchorX > width / 2) menuStyle.right = "0px";
  else menuStyle.left = "0px";
  if (anchorY > height / 2) menuStyle.bottom = BUTTON_SIZE + 8 + "rem";
  else menuStyle.top = BUTTON_SIZE + 8 + "rem";
  // The menu never leaves the screen: its body scrolls within the room between the button and the screen edge
  // (at most about 440 at 1080p, so it stays a compact box even on a tall screen).
  const unit = height / 1080;
  const room = anchorY > height / 2 ? anchorY - (8 + 16) * unit : height - anchorY - (BUTTON_SIZE + 8 + 16) * unit;
  const bodyHeight = Math.max(160 * unit, Math.min(440 * unit, room - 44 * unit));

  // The choice popup beside the clicked window (placed by chooserPlace), kept on screen; its title bar drags it.
  const chooserStyle: any = { ...S.chooser };
  if (chooser) {
    const size = chooserSize();
    chooserStyle.left = Math.max(8, Math.min(width - size.w, chooser.x)) + "px";
    chooserStyle.top = Math.max(8, Math.min(height - size.h, chooser.y)) + "px";
  }
  const onChooserDown = (e: any) => {
    if (e.button !== 0 || !chooser) return;
    e.stopPropagation();
    e.preventDefault();
    const startX = e.clientX;
    const startY = e.clientY;
    const fromX = chooser.x;
    const fromY = chooser.y;
    const onMove = (ev: MouseEvent) => {
      const x = fromX + ev.clientX - startX;
      const y = fromY + ev.clientY - startY;
      setChooser((c) => (c ? { ...c, x, y } : c));
    };
    // window (not document): the click mode blocks mouse events on document while it is on.
    const onUp = () => {
      window.removeEventListener("mousemove", onMove, true);
      window.removeEventListener("mouseup", onUp, true);
    };
    window.addEventListener("mousemove", onMove, true);
    window.addEventListener("mouseup", onUp, true);
  };
  const choose = (choice: string) => {
    if (!chooser) return;
    trigger(group, "applyPick", JSON.stringify({ token: chooser.token, choice }));
    setChooser(null);
  };

  const statusColor = !controls.enabled ? "#8a8f98" : controls.waiting > 0 ? "#ffb347" : "#4fc38a";
  const otherLanguages = languages.filter((l) => l.code.toLowerCase() !== (controls.target || "").toLowerCase());

  return (
    <div style={S.layer} data-at-skip="" data-at-ui="">
      {picking && highlight && (
        <div style={{ ...S.highlight, left: highlight.left + "px", top: highlight.top + "px", width: highlight.width + "px", height: highlight.height + "px" }} />
      )}
      {picking && (
        <div style={S.banner} dir={dir}>
          <div>{text.banner}</div>
          <div style={S.bannerBtn} onClick={() => setPicking(false)}>{text.leave}</div>
        </div>
      )}
      {toast !== "" && <div style={S.toast} dir={dir}>{toast}</div>}

      {picking && chooser && (
        <div style={chooserStyle} dir={dir}>
          <div style={{ ...S.header, cursor: "move" }} onMouseDown={onChooserDown}>
            <div style={S.title}>{text.chooseTitle(chooser.kind)}</div>
            <div style={S.close} title={text.close} onClick={() => setChooser(null)}>×</div>
          </div>
          <div style={{ ...S.bold, marginBottom: "6rem" }}>{chooser.label}</div>
          <div style={S.option(chooser.current === "default")} onClick={() => choose("default")}>{controls.scope === "ChosenOnly" ? text.optTranslate(controls.targetName) : text.optDefault(controls.targetName)}</div>
          <div style={S.option(chooser.current === "original")} onClick={() => choose("original")}>{text.optOriginal}</div>
          {!controls.enabled && (
            <div style={{ ...S.row, marginTop: "6rem", marginBottom: "4rem" }}>
              <div style={{ ...S.small, flex: 1, marginTop: "0rem", color: "#ffb347" }}>{text.offNote}</div>
              <div style={S.btn(true)} onClick={() => trigger(group, "toggleEnabled")}>{text.turnOn}</div>
            </div>
          )}
          <div style={{ ...S.small, marginTop: "8rem" }}>{text.optOther}</div>
          <div style={S.list(170)}>
            {otherLanguages.map((l) => (
              <div key={l.code} style={S.option(chooser.current.toLowerCase() === l.code.toLowerCase())} onClick={() => choose(l.code)}>
                {l.name}
              </div>
            ))}
          </div>
        </div>
      )}

      <div style={anchorStyle} ref={anchorRef}>
        <div style={S.button(controls.enabled, picking)} title={text.title} onMouseDown={onButtonDown}>
          <div style={S.glyphA}>A</div>
          <div style={S.glyphB}>ع</div>
        </div>
        <div style={S.dot(statusColor)} />

        {menuOpen && (
          <div style={menuStyle} dir={dir} onMouseDown={(e: any) => e.stopPropagation()}>
            <div style={S.header}>
              <div style={S.title}>{text.title}</div>
              <div style={S.close} title={text.close} onClick={() => setMenuOpen(false)}>×</div>
            </div>
            <div style={S.body(bodyHeight)}>

            <div style={S.row}>
              <div style={S.label}>{controls.enabled ? text.on : text.off}</div>
              <div style={S.btn(!controls.enabled)} onClick={() => trigger(group, "toggleEnabled")}>
                {controls.enabled ? text.turnOff : text.turnOn}
              </div>
            </div>
            {controls.enabled && controls.target !== "" && <div style={S.small}>{text.status(controls.translated, controls.waiting)}</div>}

            <div style={S.section}>
              <div style={S.bold}>{text.scopeTitle}</div>
              <div style={S.wrapRow}>
                {SCOPES.map((sc) => (
                  <div key={sc} style={S.chip(controls.scope === sc)} onClick={() => trigger(group, "setScope", sc)}>{text.scopes[sc]}</div>
                ))}
              </div>
              {controls.scope === "ChosenOnly" && <div style={S.small}>{text.scopeHint}</div>}
            </div>

            <div style={S.section}>
              <div style={S.bold}>{text.language}</div>
              <div style={S.wrapRow}>
                {MODES.map((m) => (
                  <div key={m} style={S.chip(controls.mode === m)} onClick={() => trigger(group, "setTargetMode", m)}>{text.modes[m]}</div>
                ))}
              </div>
              {controls.mode === "Manual" && (
                <div style={S.list(120)}>
                  {languages.map((l) => (
                    <div key={l.code} style={S.option(controls.manual.toLowerCase() === l.code.toLowerCase())} onClick={() => trigger(group, "setManualLanguage", l.code)}>
                      {l.name}
                    </div>
                  ))}
                </div>
              )}
              <div style={S.small}>{text.current(controls.targetName)}</div>
            </div>

            <div
              style={{ ...S.wide, ...S.btn(false, picking) }}
              onClick={() => {
                setPicking(!picking);
                if (!picking) setMenuOpen(false);
              }}>
              {picking ? text.pickOn + " — " + text.leave : text.pick}
            </div>
            <div style={S.small}>{text.pickHint}</div>

            <div style={S.section}>
              <div style={S.bold}>{text.custom}</div>
              {controls.entries.length === 0 && <div style={S.small}>{text.none}</div>}
              <div style={S.list(130)}>
                {controls.entries.map((entry) => (
                  <div key={entry.id} style={S.item}>
                    <div style={S.itemLabel}>{entry.label}</div>
                    <div style={S.kind}>
                      {(entry.kind === "mod" ? text.kindMod : text.kindPanel) + " · " + (entry.language ? entry.languageName || entry.language : text.originalTag)}
                    </div>
                    <div style={S.btn(true)} onClick={() => trigger(group, "restore", entry.id)}>{text.restore}</div>
                  </div>
                ))}
              </div>
            </div>

            <div style={S.link} title={text.hideHint} onClick={() => { setMenuOpen(false); trigger(group, "hideButton"); }}>
              {text.hide}
            </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
