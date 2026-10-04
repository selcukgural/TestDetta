// Sortable tables: a table with data-sortable gets a button in every header cell that has data-sort
// ("text" or "num"); cells are compared by their data-value. The table is already sorted in the
// markup, so it reads the same without this script.
(() => {
  document.addEventListener("DOMContentLoaded", () => {
    for (const table of document.querySelectorAll("table[data-sortable]")) {
      const headers = [...table.tHead.rows[0].cells];
      headers.forEach((header, column) => {
        const kind = header.dataset.sort;
        if (!kind) {
          return;
        }
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = header.textContent;
        header.textContent = "";
        header.append(button);
        button.addEventListener("click", () => {
          const ascending = header.getAttribute("aria-sort") !== "ascending";
          for (const other of headers) {
            other.removeAttribute("aria-sort");
          }
          header.setAttribute("aria-sort", ascending ? "ascending" : "descending");
          const value = (row) => row.cells[column].dataset.value ?? row.cells[column].textContent;
          const rows = [...table.tBodies[0].rows].sort((a, b) => {
            const order = kind === "num" ? Number(value(a)) - Number(value(b)) : value(a).localeCompare(value(b));
            return ascending ? order : -order;
          });
          table.tBodies[0].append(...rows);
        });
      });
    }
  });
})();
