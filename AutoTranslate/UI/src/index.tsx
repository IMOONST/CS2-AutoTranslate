import { useEffect } from "react";
import { ModRegistrar } from "cs2/modding";
import { startHook } from "hook";
import { FloatingButton } from "floating";

/**
 * Renders nothing; starts the interface text hook (once per page). Mounted in every screen so the hook runs from the
 * main menu (where mod option pages live) through the game and the editor. The hook keeps running when a host
 * unmounts: it observes the whole page, not this component.
 */
const AutoTranslateHost = () => {
  useEffect(() => {
    startHook();
  }, []);
  return null;
};

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("Menu", AutoTranslateHost);
  moduleRegistry.append("Game", AutoTranslateHost);
  moduleRegistry.append("Game", FloatingButton);
  moduleRegistry.append("Editor", AutoTranslateHost);
};

export default register;
