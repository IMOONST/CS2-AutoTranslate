import { bindValue, trigger } from "cs2/api";
import mod from "mod.json";

// C# side: AutoTranslate.Interface.InterfaceTextSystem (binding group = mod id "AutoTranslate").
const group = mod.id;

/** What the hook needs to know from C#. */
export interface HookConfig {
  /** Interface translation is on and a target language is set. */
  enabled: boolean;
  /** Target language code ("ar", "zh-Hans", ...), "" when off. */
  target: string;
  /** The target language is written right to left. */
  rtl: boolean;
  /** Changes when every original must be restored and every text requested again. */
  generation: number;
}

/** A batch of answers: only texts the hook asked for and has not received yet. "" = leave the text as it is. */
export interface TranslationDelta {
  nonce: number;
  generation: number;
  map: Record<string, string>;
}

export const OFF_CONFIG: HookConfig = { enabled: false, target: "", rtl: false, generation: -1 };

/** JSON {@link HookConfig}. */
export const config$ = bindValue<string>(group, "config", JSON.stringify(OFF_CONFIG));
/** JSON {@link TranslationDelta}, "" until the first answer. */
export const translations$ = bindValue<string>(group, "translations", "");

/** The hook started with an empty state: C# forgets what it sent before. */
export const sayHello = () => trigger(group, "hello");
/** Asks for the translation of these texts (trimmed, as seen in the UI). */
export const requestTexts = (texts: string[]) => trigger(group, "request", JSON.stringify(texts));

export const parseConfig = (json: string): HookConfig => {
  if (!json) return OFF_CONFIG;
  try {
    const value = JSON.parse(json);
    if (!value || typeof value !== "object") return OFF_CONFIG;
    return {
      enabled: value.enabled === true,
      target: typeof value.target === "string" ? value.target : "",
      rtl: value.rtl === true,
      generation: typeof value.generation === "number" ? value.generation : 0,
    };
  } catch (e) {
    return OFF_CONFIG;
  }
};

export const parseDelta = (json: string): TranslationDelta | null => {
  if (!json) return null;
  try {
    const value = JSON.parse(json);
    if (!value || typeof value !== "object" || !value.map || typeof value.map !== "object") return null;
    return {
      nonce: typeof value.nonce === "number" ? value.nonce : 0,
      generation: typeof value.generation === "number" ? value.generation : -1,
      map: value.map,
    };
  } catch (e) {
    return null;
  }
};
