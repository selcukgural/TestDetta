// Motion: sections fade in as they scroll into view, numbers count up and bars fill. Everything is
// visible without this script; the "motion" class that hides elements until they appear is only set
// here, and never for visitors who ask for reduced motion.
(() => {
  const root = document.documentElement;
  if (!("IntersectionObserver" in window) || matchMedia("(prefers-reduced-motion: reduce)").matches) {
    return;
  }
  root.classList.add("motion");

  // Counts the first number in the element's text up from zero, keeping its prefix, suffix and
  // thousands separator, so "2,941", "2.941" and "%28" all stay in their language's format.
  const countUp = (element) => {
    const text = element.textContent;
    const match = text.match(/\d[\d.,   ]*\d|\d/);
    if (!match) {
      return;
    }
    const target = Number(match[0].replace(/\D/g, ""));
    const separator = match[0].match(/\D/)?.[0] ?? "";
    const format = (value) => String(value).replace(/\B(?=(\d{3})+(?!\d))/g, separator);
    const before = text.slice(0, match.index);
    const after = text.slice(match.index + match[0].length);
    const started = performance.now();
    const duration = 1200;
    const step = (now) => {
      const progress = Math.min((now - started) / duration, 1);
      const eased = 1 - Math.pow(1 - progress, 3);
      element.textContent = before + format(Math.round(target * eased)) + after;
      if (progress < 1) {
        requestAnimationFrame(step);
      }
    };
    requestAnimationFrame(step);
  };

  document.addEventListener("DOMContentLoaded", () => {
    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) {
          continue;
        }
        entry.target.classList.add("is-in");
        for (const number of entry.target.querySelectorAll("[data-count]")) {
          countUp(number);
        }
        observer.unobserve(entry.target);
      }
    }, { threshold: 0.2 });
    for (const element of document.querySelectorAll("[data-reveal]")) {
      observer.observe(element);
    }

    // The selected test classes light up one after another, after the steps have played.
    document.querySelectorAll(".trace .dots i.hit, .dots-figure .dots i.hit").forEach((dot, index) => {
      dot.style.transitionDelay = `${(dot.closest(".trace") ? 1.9 : 0.3) + (index % 14) * 0.08}s`;
    });

    // Chart bars rise one after another, left to right.
    document.querySelectorAll(".dist-chart .bar").forEach((bar, index) => {
      bar.style.transitionDelay = `${0.2 + index * 0.025}s`;
    });

    // The logo strip scrolls endlessly: a second, hidden copy of the list follows the first.
    for (const track of document.querySelectorAll(".marquee-track")) {
      const copy = track.firstElementChild.cloneNode(true);
      copy.setAttribute("aria-hidden", "true");
      track.append(copy);
      track.classList.add("is-moving");
    }
  });
})();
