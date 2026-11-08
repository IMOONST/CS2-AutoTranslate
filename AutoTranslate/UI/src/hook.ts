import { ValueSubscription } from "cs2/api";
import {
  HookConfig,
  OFF_CONFIG,
  config$,
  parseConfig,
  parseDelta,
  requestTexts,
  sayHello,
  translations$,
} from "bindings";

/*
 * Runtime translation of text that mod interfaces draw directly into the page (not through the game's localization).
 *
 * Flow: one MutationObserver on document.body collects changed nodes; a frame later they are examined (text nodes and
 * the title / placeholder attributes). Texts not seen before are sent to C# in batches; C# drops the game's own texts,
 * numbers and texts already in the target language, answers cache hits at once and queues the rest for translation;
 * answers come back as deltas and are written into the nodes that are still waiting for them. Originals are kept so
 * everything can be restored when translation is turned off or starts over (new generation).
 *
 * Scope: the game's own interface must stay untouched. Its class names are CSS-module hashes just like the mods', so
 * the DOM cannot tell them apart reliably; the main guard is C# (TranslationService.IsGameText answers "leave as is"
 * for every text of the game's localization). Any element (and its subtree) can opt out with data-at-skip,
 * translate="no" or the class "notranslate".
 *
 * Written for Coherent Gameface: ES2017, no layout reads, no TreeWalker / closest / isConnected reliance.
 */

/** Waits this long after the first new text before sending a request batch. */
const REQUEST_DELAY_MS = 150;
/** Most texts in one request batch. */
const REQUEST_BATCH = 200;
/** Longest text considered (C# applies the same limit). */
const MAX_TEXT_LENGTH = 500;
/** Nodes remembered per waiting text; disconnected ones are pruned before this is enforced. */
const WAITING_PRUNE_AT = 32;
const WAITING_MAX = 64;
/** The set of translated nodes is pruned of removed nodes when it grows past this (then twice its live size). */
const APPLIED_PRUNE_START = 2000;
/** Rescan interval when the engine has no MutationObserver. */
const FALLBACK_SCAN_MS = 2000;
/** Texts still on screen without an answer are asked again after this long (an update may have been coalesced)... */
const RETRY_MS = 10000;
/** ...at most this many times per generation. */
const MAX_RETRIES = 3;

const ATTRIBUTES = ["title", "placeholder"];

/** Elements whose content is never examined at all. */
const SKIP_ALL: Record<string, true> = { SCRIPT: true, STYLE: true, NOSCRIPT: true, TEMPLATE: true };
/** Elements whose text is typed by the player (their title / placeholder attributes are still translated). */
const SKIP_TEXT: Record<string, true> = { INPUT: true, TEXTAREA: true };

const TEXT_NODE = 3;
const ELEMENT_NODE = 1;

/** At least two Latin letters (ASCII or Latin-1 / Latin Extended-A/B). */
const LATIN_PAIR = /[A-Za-zÀ-ÖØ-öø-ɏ][^]*[A-Za-zÀ-ÖØ-öø-ɏ]/;
const LEADING_SPACE = /^\s*/;
const TRAILING_SPACE = /\s*$/;

interface Applied {
  /** The full value before translation (with its surrounding whitespace). */
  orig: string;
  /** What was written in its place. */
  shown: string;
}

interface AttrRef {
  el: Element;
  name: string;
}

type WaitTarget = Text | AttrRef;

// ---------------------------------------------------------------------------------------------------------------------
// State

let config: HookConfig = OFF_CONFIG;
let active = false;
let started = false;

let configSub: ValueSubscription<string> | null = null;
let translationsSub: ValueSubscription<string> | null = null;
let observer: MutationObserver | null = null;
let fallbackTimer: number | null = null;

/** Trimmed text -> translation ("" = leave as is) for the current generation. */
let translations = new Map<string, string>();
/** Texts asked for (or queued) in this generation; never asked twice. */
let requested = new Set<string>();
/** Texts to send in the next batch. */
let queue: string[] = [];
/** Nodes showing a text whose answer has not arrived. */
let waiting = new Map<string, WaitTarget[]>();

/** Text nodes we rewrote. */
let textState = new WeakMap<Text, Applied>();
/** Elements whose attributes we rewrote: attribute name -> state. */
let attrState = new WeakMap<Element, Record<string, Applied>>();
/** Every node / element we changed (text, attributes or dir), for restoring. */
let applied = new Set<Node>();
let appliedPruneAt = APPLIED_PRUNE_START;
/** Elements on which we set dir="auto". */
let dirSet = new Set<Element>();

/** Changed nodes waiting for the next flush. */
let dirtyNodes = new Set<Node>();
/** Elements whose watched attributes changed. */
let dirtyAttrs = new Set<Element>();
let flushScheduled = false;
let requestTimer: number | null = null;
let retryTimer: number | null = null;
/** Times each text was asked again. */
let retries = new Map<string, number>();

// ---------------------------------------------------------------------------------------------------------------------
// Scheduling

/** Longest wait for a flush when animation frames are not delivered (hidden view). */
const FLUSH_FALLBACK_MS = 100;

const runFlush = () => {
  if (flushScheduled) flush();
};

/** Next frame, or after 100 ms at the latest: whichever comes first runs the flush, the other finds nothing to do. */
const scheduleFlush = () => {
  if (flushScheduled) return;
  flushScheduled = true;
  if (typeof requestAnimationFrame === "function") requestAnimationFrame(runFlush);
  setTimeout(runFlush, FLUSH_FALLBACK_MS);
};

const scheduleRequest = () => {
  if (requestTimer !== null) return;
  requestTimer = setTimeout(sendRequests, REQUEST_DELAY_MS) as unknown as number;
};

const sendRequests = () => {
  requestTimer = null;
  if (!active || queue.length === 0) return;
  const batch = queue.length <= REQUEST_BATCH ? queue : queue.slice(0, REQUEST_BATCH);
  queue = queue.length <= REQUEST_BATCH ? [] : queue.slice(REQUEST_BATCH);
  try {
    requestTexts(batch);
  } catch (e) {
    // The binding is gone (C# side not loaded); allow these to be asked again later.
    for (let i = 0; i < batch.length; i++) requested.delete(batch[i]);
  }
  if (queue.length > 0) scheduleRequest();
  scheduleRetry();
};

const scheduleRetry = () => {
  if (retryTimer !== null || waiting.size === 0) return;
  retryTimer = setTimeout(retryWaiting, RETRY_MS) as unknown as number;
};

/** Asks again for texts still shown without an answer; forgets waiting texts whose nodes are all gone. */
const retryWaiting = () => {
  retryTimer = null;
  if (!active || waiting.size === 0) return;
  const again: string[] = [];
  waiting.forEach((targets, key) => {
    const live = targets.filter((t) => isConnected((t as AttrRef).el || (t as Text)));
    if (live.length === 0) {
      waiting.delete(key);
      return;
    }
    if (live.length !== targets.length) waiting.set(key, live);
    const count = retries.get(key) || 0;
    if (count >= MAX_RETRIES) return;
    retries.set(key, count + 1);
    again.push(key);
  });
  if (again.length > 0) {
    for (let i = 0; i < again.length; i++) queue.push(again[i]);
    // sendRequests schedules the next pass.
    scheduleRequest();
  }
  // Otherwise every waiting text used up its retries: stay idle until new texts are asked for.
};

// ---------------------------------------------------------------------------------------------------------------------
// Helpers

const isCandidate = (key: string) => key.length >= 2 && key.length <= MAX_TEXT_LENGTH && LATIN_PAIR.test(key);

const isConnected = (node: Node): boolean => {
  const flag = (node as any).isConnected;
  if (typeof flag === "boolean") return flag;
  let current: Node | null = node;
  while (current) {
    if (current.nodeType === 9) return true; // document
    current = current.parentNode;
  }
  return false;
};

/** An element that opts its subtree out (data-at-skip, translate="no", class "notranslate", contenteditable). */
const optsOut = (el: Element): boolean => {
  if (el.hasAttribute("data-at-skip")) return true;
  const translate = el.getAttribute("translate");
  if (translate === "no") return true;
  const editable = el.getAttribute("contenteditable");
  if (editable !== null && editable !== "false") return true;
  const cls = el.getAttribute("class");
  return cls !== null && cls.indexOf("notranslate") >= 0;
};

const tagOf = (el: Element) => {
  const tag = el.tagName || "";
  return tag.toUpperCase();
};

/**
 * Whether content at this place must be left alone. Walks the ancestors of `start` (inclusive).
 * `forAttributes`: checking an element's own attributes, where input / textarea themselves are fine.
 */
const isSkipped = (start: Node | null, forAttributes: boolean): boolean => {
  let current: Node | null = start;
  let first = true;
  while (current && current.nodeType === ELEMENT_NODE) {
    const el = current as Element;
    const tag = tagOf(el);
    if (SKIP_ALL[tag]) return true;
    if (SKIP_TEXT[tag] && !(first && forAttributes)) return true;
    if (optsOut(el)) return true;
    first = false;
    current = current.parentNode;
  }
  // Not attached under an element (detached fragment or the document itself): only body content is handled.
  return !current || current.nodeType !== 9;
};

const remember = (node: Node) => {
  applied.add(node);
  if (applied.size > appliedPruneAt) {
    applied.forEach((n) => {
      if (!isConnected(n)) applied.delete(n);
    });
    dirSet.forEach((el) => {
      if (!isConnected(el)) dirSet.delete(el);
    });
    appliedPruneAt = Math.max(APPLIED_PRUNE_START, applied.size * 2);
  }
};

/** Right-to-left letters (Hebrew, Arabic and their presentation forms). */
const RTL_CHAR = /[֐-ࣿיִ-﷿ﹰ-﻿]/;

/** Lets the browser lay out right-to-left text: for an RTL target, or any translation written in an RTL script. */
const setDir = (el: Node | null, translation?: string) => {
  if (!(config.rtl || (translation !== undefined && RTL_CHAR.test(translation))) || !el || el.nodeType !== ELEMENT_NODE) return;
  const element = el as Element;
  if (element.hasAttribute("dir")) return;
  element.setAttribute("dir", "auto");
  dirSet.add(element);
};

const wait = (key: string, target: WaitTarget) => {
  let list = waiting.get(key);
  if (!list) {
    list = [];
    waiting.set(key, list);
  }
  for (let i = 0; i < list.length; i++) if (list[i] === target) return;
  if (list.length >= WAITING_PRUNE_AT) {
    list = list.filter((t) => isConnected((t as AttrRef).el || (t as Text)));
    waiting.set(key, list);
  }
  if (list.length >= WAITING_MAX) list.shift();
  list.push(target);
};

const ask = (key: string) => {
  if (requested.has(key)) return;
  requested.add(key);
  queue.push(key);
  scheduleRequest();
};

const withSpacing = (value: string, translation: string) => {
  const lead = (LEADING_SPACE.exec(value) || [""])[0];
  const trail = value.length > lead.length ? (TRAILING_SPACE.exec(value) || [""])[0] : "";
  return lead + translation + trail;
};

// ---------------------------------------------------------------------------------------------------------------------
// Processing

/** Examines one text node; `checkAncestors` = false when the caller already knows the place is allowed. */
const processText = (node: Text, checkAncestors: boolean) => {
  const value = node.nodeValue;
  if (!value) return;

  const state = textState.get(node);
  if (state) {
    // Our own write (or a mutation record of it): nothing to do.
    if (value === state.shown) return;
    // The interface replaced the text (re-render): this is a new original.
    textState.delete(node);
  }

  const key = value.trim();
  if (!isCandidate(key)) return;
  if (checkAncestors && isSkipped(node.parentNode, false)) return;

  const translation = translations.get(key);
  if (translation === undefined) {
    ask(key);
    wait(key, node);
    return;
  }
  if (translation === "" || translation === key) return;

  const shown = withSpacing(value, translation);
  textState.set(node, { orig: value, shown });
  remember(node);
  node.nodeValue = shown;
  setDir(node.parentNode, translation);
};

const processAttribute = (el: Element, name: string) => {
  const value = el.getAttribute(name);
  let states = attrState.get(el);
  const state = states ? states[name] : undefined;
  if (value === null || value === "") {
    if (states && state) delete states[name];
    return;
  }
  if (states && state) {
    if (value === state.shown) return;
    delete states[name];
  }

  const key = value.trim();
  if (!isCandidate(key)) return;

  const translation = translations.get(key);
  if (translation === undefined) {
    ask(key);
    wait(key, { el, name });
    return;
  }
  if (translation === "" || translation === key) return;

  const shown = withSpacing(value, translation);
  if (!states) {
    states = {};
    attrState.set(el, states);
  }
  states[name] = { orig: value, shown };
  remember(el);
  el.setAttribute(name, shown);
};

const processAttributes = (el: Element) => {
  for (let i = 0; i < ATTRIBUTES.length; i++) {
    if (el.hasAttribute(ATTRIBUTES[i])) processAttribute(el, ATTRIBUTES[i]);
  }
};

/** Examines a subtree whose root place is already known to be allowed. Iterative (deep UIs). */
const scanSubtree = (root: Element) => {
  const stack: Node[] = [root];
  while (stack.length > 0) {
    const node = stack.pop() as Node;
    if (node.nodeType === TEXT_NODE) {
      processText(node as Text, false);
      continue;
    }
    if (node.nodeType !== ELEMENT_NODE) continue;
    const el = node as Element;
    const tag = tagOf(el);
    if (SKIP_ALL[tag] || optsOut(el)) continue;
    processAttributes(el);
    if (SKIP_TEXT[tag]) continue;
    const children = el.childNodes;
    for (let i = children.length - 1; i >= 0; i--) stack.push(children[i]);
  }
};

const processDirtyNode = (node: Node) => {
  if (node.nodeType === TEXT_NODE) {
    processText(node as Text, true);
    return;
  }
  if (node.nodeType !== ELEMENT_NODE) return;
  const el = node as Element;
  // The element itself is checked by scanSubtree; its ancestors here.
  if (isSkipped(el.parentNode, false)) return;
  scanSubtree(el);
};

const flush = () => {
  flushScheduled = false;
  if (!active) {
    dirtyNodes.clear();
    dirtyAttrs.clear();
    return;
  }
  if (dirtyNodes.size > 0) {
    const nodes = dirtyNodes;
    dirtyNodes = new Set<Node>();
    nodes.forEach(processDirtyNode);
  }
  if (dirtyAttrs.size > 0) {
    const elements = dirtyAttrs;
    dirtyAttrs = new Set<Element>();
    elements.forEach((el) => {
      if (!isSkipped(el, true)) processAttributes(el);
    });
  }
};

const onMutations = (records: MutationRecord[]) => {
  if (!active) return;
  for (let i = 0; i < records.length; i++) {
    const record = records[i];
    if (record.type === "characterData") {
      dirtyNodes.add(record.target);
    } else if (record.type === "childList") {
      const added = record.addedNodes;
      for (let j = 0; j < added.length; j++) dirtyNodes.add(added[j]);
    } else if (record.type === "attributes" && record.target.nodeType === ELEMENT_NODE) {
      const name = record.attributeName;
      if (name === "title" || name === "placeholder") dirtyAttrs.add(record.target as Element);
    }
  }
  if (dirtyNodes.size > 0 || dirtyAttrs.size > 0) scheduleFlush();
};

// ---------------------------------------------------------------------------------------------------------------------
// Answers from C#

const applyDelta = (json: string) => {
  if (!active) return;
  const delta = parseDelta(json);
  if (!delta) return;
  if (delta.generation !== config.generation) {
    // The config may not have reached us yet: take it first, then see if the answers belong to it.
    if (configSub) onConfig(configSub.value);
    if (!active || delta.generation !== config.generation) return;
  }

  const map = delta.map;
  for (const key in map) {
    if (!Object.prototype.hasOwnProperty.call(map, key)) continue;
    const value = map[key];
    translations.set(key, typeof value === "string" ? value : "");
    requested.add(key);
    const targets = waiting.get(key);
    if (!targets) continue;
    waiting.delete(key);
    for (let i = 0; i < targets.length; i++) {
      const target = targets[i];
      if ((target as AttrRef).el) {
        const ref = target as AttrRef;
        if (isConnected(ref.el) && !isSkipped(ref.el, true)) processAttribute(ref.el, ref.name);
      } else {
        const node = target as Text;
        if (isConnected(node)) processText(node, true);
      }
    }
  }
};

// ---------------------------------------------------------------------------------------------------------------------
// Lifecycle

/** Puts every original text back and forgets everything of the current generation. */
const restoreAll = () => {
  applied.forEach((node) => {
    if (node.nodeType === TEXT_NODE) {
      const state = textState.get(node as Text);
      if (state && node.nodeValue === state.shown) node.nodeValue = state.orig;
    } else if (node.nodeType === ELEMENT_NODE) {
      const el = node as Element;
      const states = attrState.get(el);
      if (!states) return;
      for (const name in states) {
        if (!Object.prototype.hasOwnProperty.call(states, name)) continue;
        const state = states[name];
        if (el.getAttribute(name) === state.shown) el.setAttribute(name, state.orig);
      }
    }
  });
  dirSet.forEach((el) => {
    if (el.getAttribute("dir") === "auto") el.removeAttribute("dir");
  });

  translations = new Map<string, string>();
  requested = new Set<string>();
  queue = [];
  waiting = new Map<string, WaitTarget[]>();
  textState = new WeakMap<Text, Applied>();
  attrState = new WeakMap<Element, Record<string, Applied>>();
  applied = new Set<Node>();
  appliedPruneAt = APPLIED_PRUNE_START;
  dirSet = new Set<Element>();
  dirtyNodes = new Set<Node>();
  dirtyAttrs = new Set<Element>();
  retries = new Map<string, number>();
  if (requestTimer !== null) {
    clearTimeout(requestTimer);
    requestTimer = null;
  }
  if (retryTimer !== null) {
    clearTimeout(retryTimer);
    retryTimer = null;
  }
};

const fullScan = () => {
  const body = document.body;
  if (body && !isSkipped(body.parentNode, false)) scanSubtree(body);
};

const observe = () => {
  const body = document.body;
  if (!body) return;
  if (typeof MutationObserver === "function") {
    observer = new MutationObserver(onMutations);
    try {
      observer.observe(body, {
        childList: true,
        subtree: true,
        characterData: true,
        attributes: true,
        attributeFilter: ATTRIBUTES,
      });
    } catch (e) {
      // The engine rejects attribute filtering: watch text only (attributes are still read when elements appear).
      observer.observe(body, { childList: true, subtree: true, characterData: true });
    }
  } else {
    // No MutationObserver: rescan now and then (texts already examined cost a map lookup each).
    fallbackTimer = setInterval(() => {
      if (active) fullScan();
    }, FALLBACK_SCAN_MS) as unknown as number;
  }
};

const disconnect = () => {
  if (observer) {
    observer.disconnect();
    observer = null;
  }
  if (fallbackTimer !== null) {
    clearInterval(fallbackTimer);
    fallbackTimer = null;
  }
};

const startTranslating = () => {
  active = true;
  sayHello();
  observe();
  fullScan();
};

const stopTranslating = () => {
  active = false;
  disconnect();
  restoreAll();
};

const onConfig = (json: string) => {
  const next = parseConfig(json);
  const on = next.enabled && next.target !== "";
  const previous = config;
  config = next;

  if (!on) {
    if (active) stopTranslating();
    return;
  }
  if (!active) {
    startTranslating();
    return;
  }
  if (next.generation !== previous.generation || next.target !== previous.target || next.rtl !== previous.rtl) {
    // Start over: put the originals back and ask again for everything on screen.
    restoreAll();
    fullScan();
  }
};

/** Starts the hook once for the page's lifetime (the observed DOM outlives the components that host it). */
export const startHook = () => {
  if (started) return;
  started = true;
  try {
    configSub = config$.subscribe(onConfig);
    translationsSub = translations$.subscribe(applyDelta);
    onConfig(configSub.value);
    if (translationsSub.value) applyDelta(translationsSub.value);
  } catch (e) {
    // C# bindings unavailable: stay inert, the page must not break.
    started = false;
    if (configSub) configSub.dispose();
    if (translationsSub) translationsSub.dispose();
    configSub = null;
    translationsSub = null;
  }
};

// ---------------------------------------------------------------------------------------------------------------------
// Click mode (floating button)

/**
 * The texts an element shows, as their originals: a text we translated gives its text before translation (so C# can
 * recognise the mod and save texts that stay valid). Text and title / placeholder attributes; at most `limit`.
 */
/**
 * The first visible text of a window in document order (its title in practice), original text; "" when none.
 * Attributes (tooltips such as "Drag to move") are skipped: they make poor names.
 */
export const panelTitle = (root: Element): string => {
  const stack: Node[] = [root];
  while (stack.length > 0) {
    const node = stack.pop() as Node;
    if (node.nodeType === TEXT_NODE) {
      const text = node as Text;
      const state = textState.get(text);
      const value = ((state && text.nodeValue === state.shown ? state.orig : text.nodeValue) || "").trim();
      if (value.length >= 2 && value.length <= 60 && /[A-Za-zÀ-￿]/.test(value)) return value;
      continue;
    }
    if (node.nodeType !== ELEMENT_NODE) continue;
    const el = node as Element;
    const tag = tagOf(el);
    if (SKIP_ALL[tag] || SKIP_TEXT[tag]) continue;
    const children = el.childNodes;
    for (let i = children.length - 1; i >= 0; i--) stack.push(children[i]);
  }
  return "";
};

export const collectTexts = (root: Element, limit: number): string[] => {
  const out: string[] = [];
  const seen = new Set<string>();
  const add = (value: string | null) => {
    if (!value) return;
    const key = value.trim();
    if (key.length < 2 || key.length > MAX_TEXT_LENGTH || seen.has(key)) return;
    seen.add(key);
    out.push(key);
  };
  const stack: Node[] = [root];
  while (stack.length > 0 && out.length < limit) {
    const node = stack.pop() as Node;
    if (node.nodeType === TEXT_NODE) {
      const text = node as Text;
      const state = textState.get(text);
      add(state && text.nodeValue === state.shown ? state.orig : text.nodeValue);
      continue;
    }
    if (node.nodeType !== ELEMENT_NODE) continue;
    const el = node as Element;
    const tag = tagOf(el);
    if (SKIP_ALL[tag]) continue;
    const states = attrState.get(el);
    for (let i = 0; i < ATTRIBUTES.length; i++) {
      const name = ATTRIBUTES[i];
      const value = el.getAttribute(name);
      const state = states ? states[name] : undefined;
      add(state && value === state.shown ? state.orig : value);
    }
    if (SKIP_TEXT[tag]) continue;
    const children = el.childNodes;
    for (let i = children.length - 1; i >= 0; i--) stack.push(children[i]);
  }
  return out;
};
