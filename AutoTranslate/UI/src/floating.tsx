import { useEffect, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import mod from "mod.json";
import { collectTexts } from "hook";

/*
 * The floating Auto Translate button (C#: Interface/FloatingControls.cs).
 *
 * - Drag it anywhere (its place is saved in the settings); a click opens its menu.
 * - Menu: translation on / off (the options page's master switch), the click mode, the windows and mods switched off
 *   (each can be brought back), hide the button.
 * - Click mode: hovering a window outlines it; clicking it sends its texts to C#, which switches that mod's (or that
 *   window's) translation off, or on again when it was off. Clicks in this mode never reach the window. Esc, a right
 *   click or the banner's button leave the mode.
 *
 * Everything here carries data-at-skip so the interface hook never translates it, and data-at-ui so the click mode
 * ignores it. Written for Coherent Gameface: no closest(), no pointer events, layout reads only on user input.
 */

const group = mod.id;

interface Entry {
  id: string;
  label: string;
  kind: "mod" | "panel";
}

interface Controls {
  arabic: boolean;
  show: boolean;
  x: number;
  y: number;
  enabled: boolean;
  interfaces: boolean;
  target: string;
  translated: number;
  waiting: number;
  entries: Entry[];
}

interface PickResult {
  nonce: number;
  action: "off" | "on" | "none" | "error";
  label: string;
}

const controls$ = bindValue<string>(group, "controls", "{}");
const pickResult$ = bindValue<string>(group, "pickResult", "");

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
  arabic: false, show: false, x: -1, y: -1, enabled: false, interfaces: false, target: "", translated: 0, waiting: 0, entries: [],
};

const TEXT = {
  en: {
    title: "Auto Translate",
    on: "Translation is on",
    off: "Translation is off",
    turnOff: "Turn off",
    turnOn: "Turn on",
    pick: "Click mode",
    pickHint: "Click a mod window to switch its translation off, or on again",
    pickOn: "Click mode is on",
    leave: "Leave",
    status: (t: number, w: number) => `Translated ${t} · waiting ${w}`,
    excluded: "Not translated",
    none: "Nothing switched off yet.",
    restore: "Translate",
    kindMod: "mod",
    kindPanel: "window",
    hide: "Hide button",
    hideHint: "The options page shows it again (Floating button).",
    close: "Close",
    resOff: (l: string) => `Translation off: ${l}`,
    resOn: (l: string) => `Translation back: ${l}`,
    resNone: "This is a game window, or it has nothing to translate.",
    resError: "Could not save exclusions.json (see the log).",
    banner: "Click a mod window: its translation turns off, click again to bring it back. Esc or right click to leave.",
    noTarget: "No target language (the game and Windows are in English).",
  },
  ar: {
    title: "المترجم التلقائي",
    on: "الترجمة مفعّلة",
    off: "الترجمة متوقفة",
    turnOff: "إيقاف",
    turnOn: "تشغيل",
    pick: "وضع التحكم بالضغط",
    pickHint: "اضغط على نافذة أي مود لإيقاف ترجمتها أو إرجاعها",
    pickOn: "وضع التحكم بالضغط شغّال",
    leave: "خروج",
    status: (t: number, w: number) => `مترجم ${t} · ينتظر ${w}`,
    excluded: "بدون ترجمة",
    none: "ما أوقفت ترجمة أي شيء بعد.",
    restore: "ترجمة",
    kindMod: "مود",
    kindPanel: "نافذة",
    hide: "إخفاء الأيقونة",
    hideHint: "ترجعها من صفحة الإعدادات (الأيقونة العائمة).",
    close: "إغلاق",
    resOff: (l: string) => `أُوقفت ترجمة: ${l}`,
    resOn: (l: string) => `رجعت ترجمة: ${l}`,
    resNone: "هذه نافذة من اللعبة نفسها، أو ما فيها نص يُترجم.",
    resError: "تعذّر حفظ ملف الاستثناءات (راجع السجل).",
    banner: "اضغط على نافذة مود: تتوقف ترجمتها، واضغط مرة ثانية ترجع. للخروج: Esc أو زر الماوس الأيمن.",
    noTarget: "لا توجد لغة هدف (اللعبة وويندوز بالإنجليزية).",
  },
};

const BUTTON_SIZE = 46;
const DRAG_THRESHOLD = 4;
const PICK_LIMIT = 400;

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
  layer: {
    position: "absolute", left: "0px", top: "0px", width: "100vw", height: "100vh", pointerEvents: "none",
  } as any,
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
    position: "absolute", width: "300rem", padding: "10rem 12rem 12rem 12rem", borderRadius: "8rem",
    backgroundColor: "rgba(24, 33, 48, 0.97)", color: "#ffffff", boxShadow: "0 4rem 16rem rgba(0,0,0,0.5)",
    pointerEvents: "auto",
  } as any,
  header: { display: "flex", flexDirection: "row", alignItems: "center", marginBottom: "8rem" } as any,
  title: { flex: 1, fontSize: "15rem", fontWeight: "bold", color: "#a6dcff" } as any,
  close: { width: "22rem", height: "22rem", fontSize: "16rem", textAlign: "center", color: "rgba(255,255,255,0.8)", cursor: "pointer" } as any,
  row: { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "6rem" } as any,
  label: { flex: 1, fontSize: "13rem" } as any,
  small: { fontSize: "11.5rem", color: "rgba(255,255,255,0.65)", marginTop: "4rem" } as any,
  btn: (primary: boolean, active?: boolean) => ({
    padding: "4rem 10rem", borderRadius: "4rem", fontSize: "12.5rem", cursor: "pointer",
    backgroundColor: active ? "rgba(255,170,0,0.9)" : primary ? "rgba(79,195,138,0.85)" : "rgba(255,255,255,0.14)",
    color: active ? "#1a1a1a" : "#ffffff",
  }) as any,
  wide: { marginTop: "8rem", padding: "6rem 10rem", borderRadius: "4rem", fontSize: "13rem", textAlign: "center", cursor: "pointer" } as any,
  section: { marginTop: "10rem", paddingTop: "8rem", borderTop: "1rem solid rgba(255,255,255,0.12)" } as any,
  list: { maxHeight: "220rem", overflowY: "scroll" } as any,
  item: { display: "flex", flexDirection: "row", alignItems: "center", marginTop: "4rem" } as any,
  itemLabel: { flex: 1, fontSize: "12.5rem", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } as any,
  kind: { fontSize: "11rem", color: "rgba(255,255,255,0.55)", marginLeft: "6rem", marginRight: "6rem" } as any,
  link: { marginTop: "10rem", fontSize: "12rem", color: "rgba(255,255,255,0.6)", cursor: "pointer" } as any,
  banner: {
    position: "absolute", left: "50%", top: "70rem", transform: "translateX(-50%)", maxWidth: "760rem",
    display: "flex", flexDirection: "row", alignItems: "center", padding: "8rem 12rem", borderRadius: "6rem",
    backgroundColor: "rgba(255, 170, 0, 0.95)", color: "#1a1a1a", fontSize: "13.5rem", pointerEvents: "auto",
  } as any,
  bannerBtn: { marginLeft: "12rem", marginRight: "12rem", padding: "4rem 10rem", borderRadius: "4rem", backgroundColor: "rgba(0,0,0,0.75)", color: "#ffffff", cursor: "pointer" } as any,
  highlight: {
    position: "absolute", border: "2rem solid #ffaa00", backgroundColor: "rgba(255,170,0,0.12)", borderRadius: "4rem",
    pointerEvents: "none",
  } as any,
  toast: {
    position: "absolute", left: "50%", top: "118rem", transform: "translateX(-50%)", maxWidth: "640rem",
    padding: "8rem 14rem", borderRadius: "6rem", backgroundColor: "rgba(24,33,48,0.96)", color: "#ffffff",
    fontSize: "13.5rem", pointerEvents: "none",
  } as any,
};

interface Rect { left: number; top: number; width: number; height: number; }

// ---------------------------------------------------------------------------------------------------------------------

export const FloatingButton = () => {
  const controls = parse<Controls>(useValue(controls$), DEFAULT_CONTROLS);
  const pickJson = useValue(pickResult$);
  const text = controls.arabic ? TEXT.ar : TEXT.en;
  const dir = controls.arabic ? "rtl" : "ltr";

  const [menuOpen, setMenuOpen] = useState(false);
  const [picking, setPicking] = useState(false);
  const [dragPos, setDragPos] = useState<{ x: number; y: number } | null>(null);
  const [highlight, setHighlight] = useState<Rect | null>(null);
  const [toast, setToast] = useState<string>("");
  const anchorRef = useRef<HTMLDivElement | null>(null);
  // The answer present when the component mounted is an old one: only newer answers are shown.
  const initial = parse<PickResult | null>(pickJson, null);
  const lastNonce = useRef<number>(initial && typeof initial.nonce === "number" ? initial.nonce : 0);

  // The result of a click in click mode, shown for a few seconds.
  useEffect(() => {
    const result = parse<PickResult | null>(pickJson, null);
    if (!result || typeof result.nonce !== "number" || result.nonce === lastNonce.current) return;
    lastNonce.current = result.nonce;
    const label = result.label || "";
    const message =
      result.action === "off" ? text.resOff(label) :
      result.action === "on" ? text.resOn(label) :
      result.action === "none" ? text.resNone : text.resError;
    setToast(message);
    const timer = setTimeout(() => setToast(""), 3500);
    return () => clearTimeout(timer);
  }, [pickJson]);

  // Click mode: capture every mouse event before the windows see it.
  useEffect(() => {
    if (!picking) {
      setHighlight(null);
      return;
    }
    const onMove = (e: MouseEvent) => {
      const el = elementOf(e.target);
      if (!el || isOwnUi(el)) {
        setHighlight(null);
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
      if (e.button === 2) {
        setPicking(false);
        return;
      }
      if (e.button !== 0) return;
      const el = elementOf(e.target);
      if (!el) return;
      const panel = findPanel(el);
      const texts = collectTexts(panel, PICK_LIMIT);
      trigger(group, "pick", JSON.stringify({ texts, label: texts.length > 0 ? texts[0] : "" }));
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" || e.keyCode === 27) {
        e.preventDefault();
        e.stopPropagation();
        setPicking(false);
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
  const anchorStyle = pos
    ? { ...S.anchor, left: pos.x + "px", top: pos.y + "px" }
    : { ...S.anchor, right: "24rem", top: "140rem" };

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
  let anchorX = width - 70;
  let anchorY = 140;
  if (pos) {
    anchorX = pos.x;
    anchorY = pos.y;
  }
  const menuStyle: any = { ...S.menu };
  if (anchorX > width / 2) menuStyle.right = "0px";
  else menuStyle.left = "0px";
  if (anchorY > height / 2) menuStyle.bottom = BUTTON_SIZE + 8 + "rem";
  else menuStyle.top = BUTTON_SIZE + 8 + "rem";

  const statusColor = !controls.enabled ? "#8a8f98" : controls.waiting > 0 ? "#ffb347" : "#4fc38a";

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

            <div style={S.row}>
              <div style={S.label}>{controls.enabled ? text.on : text.off}</div>
              <div style={S.btn(!controls.enabled)} onClick={() => trigger(group, "toggleEnabled")}>
                {controls.enabled ? text.turnOff : text.turnOn}
              </div>
            </div>
            {controls.enabled && controls.target === "" && <div style={S.small}>{text.noTarget}</div>}
            {controls.enabled && controls.target !== "" && <div style={S.small}>{text.status(controls.translated, controls.waiting)}</div>}

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
              <div style={{ ...S.label, fontWeight: "bold" }}>{text.excluded}</div>
              {controls.entries.length === 0 && <div style={S.small}>{text.none}</div>}
              <div style={S.list}>
                {controls.entries.map((entry) => (
                  <div key={entry.id} style={S.item}>
                    <div style={S.itemLabel}>{entry.label}</div>
                    <div style={S.kind}>{entry.kind === "mod" ? text.kindMod : text.kindPanel}</div>
                    <div style={S.btn(true)} onClick={() => trigger(group, "restore", entry.id)}>{text.restore}</div>
                  </div>
                ))}
              </div>
            </div>

            <div style={S.link} title={text.hideHint} onClick={() => { setMenuOpen(false); trigger(group, "hideButton"); }}>
              {text.hide}
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
