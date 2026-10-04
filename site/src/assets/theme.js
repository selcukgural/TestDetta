// Theme switch: light (the default, whatever the system prefers) ↔ dark. The choice is kept in
// localStorage, which may be unavailable (private windows, blocked storage); the page then stays light.
(() => {
  const root = document.documentElement;
  const modes = ["light", "dark"];
  let current = null;
  const read = () => {
    if (current) {
      return current;
    }
    try {
      const saved = localStorage.getItem("theme");
      return modes.includes(saved) ? saved : "light";
    } catch {
      return "light";
    }
  };
  const apply = (mode) => {
    root.setAttribute("data-theme", mode);
    for (const button of document.querySelectorAll(".theme-toggle")) {
      button.dataset.mode = mode;
      button.setAttribute("aria-label", `${button.dataset.label}: ${button.dataset[mode]}`);
      button.title = button.getAttribute("aria-label");
    }
  };

  // Runs in <head>, before the body paints, so a chosen theme never flashes the other one.
  apply(read());
  document.addEventListener("DOMContentLoaded", () => {
    apply(read());
    for (const button of document.querySelectorAll(".theme-toggle")) {
      button.addEventListener("click", () => {
        const next = modes[(modes.indexOf(read()) + 1) % modes.length];
        try {
          localStorage.setItem("theme", next);
        } catch {
          // Not stored: the choice lasts for this page only.
        }
        current = next;
        apply(next);
      });
    }
  });
})();
