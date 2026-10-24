// Savings calculator on the time-saved docs page. Every number is recomputed from the form on input and
// written with textContent; the markup carries the results for the default values, so the section
// reads the same without this script. Numbers use the page language's format.
(() => {
  const workingDays = 21;
  document.addEventListener("DOMContentLoaded", () => {
    for (const form of document.querySelectorAll("form[data-calc]")) {
      const lang = document.documentElement.lang || "en";
      const number = (digits) => new Intl.NumberFormat(lang, { maximumFractionDigits: digits });
      const money = new Intl.NumberFormat(lang, { style: "currency", currency: "USD", maximumFractionDigits: 0 });
      // Hours and minutes with the language's own short unit ("91 h", "91 sa", "91 時間").
      const hours = new Intl.NumberFormat(lang, { style: "unit", unit: "hour", unitDisplay: "short", maximumFractionDigits: 0 });
      const minutes = new Intl.NumberFormat(lang, { style: "unit", unit: "minute", unitDisplay: "short", maximumFractionDigits: 1 });
      // "28%" or "%28": the share of test classes that still run.
      const runShare = Number(String(form.dataset.share).replace(/[^\d.]/g, "")) / 100;
      const value = (name) => Math.max(0, Number(form.elements[name].value) || 0);
      const out = (name, text) => {
        form.elements[name].textContent = text;
      };
      const update = () => {
        const savedPerRun = value("minutes") * (1 - runShare);
        const runsPerMonth = value("runs") * workingDays;
        const waitMinutes = savedPerRun * runsPerMonth;
        const runnerMinutes = waitMinutes * Math.max(1, value("jobs"));
        const ciCost = runnerMinutes * value("price");
        const devCost = (waitMinutes / 60) * value("hourly");
        out("hours", hours.format(waitMinutes / 60));
        out("perRun", minutes.format(savedPerRun));
        out("ciMinutes", number(0).format(runnerMinutes));
        out("ciCost", money.format(ciCost));
        out("devCost", money.format(devCost));
      };
      form.addEventListener("input", update);
      form.addEventListener("submit", (event) => event.preventDefault());
      update();
    }
  });
})();
