// Trial form. The license service hands out a proof-of-work token; the browser finds a nonce whose
// SHA-256 starts with the asked number of zero bits (a few seconds, started while the visitor types),
// then posts the address and repositories with it. Messages come from the form's data-msg-* attributes,
// so every language has its own; results are written with textContent. Without this script the form
// cannot be sent (the site's CSP has form-action 'none').
(() => {
  const encoder = new TextEncoder();

  const proves = (hash, bits) => {
    const full = bits >> 3;
    for (let i = 0; i < full; i++) {
      if (hash[i] !== 0) {
        return false;
      }
    }
    const rest = bits & 7;
    return rest === 0 || hash[full] >> (8 - rest) === 0;
  };

  // Hashes in batches so the page stays responsive.
  const solve = async (challenge, bits) => {
    for (let base = 0; ; base += 512) {
      const batch = [];
      for (let n = base; n < base + 512; n++) {
        batch.push(crypto.subtle.digest("SHA-256", encoder.encode(`${challenge}.${n}`)).then((hash) => [n, new Uint8Array(hash)]));
      }
      for (const [n, hash] of await Promise.all(batch)) {
        if (proves(hash, bits)) {
          return String(n);
        }
      }
    }
  };

  document.addEventListener("DOMContentLoaded", () => {
    for (const form of document.querySelectorAll("form[data-trial]")) {
      if (form.dataset.open !== "true" || !window.crypto?.subtle) {
        form.dataset.open = "false";
        continue;
      }

      const endpoint = form.dataset.endpoint.replace(/\/$/, "");
      const status = form.querySelector(".trial-status");
      const button = form.querySelector("button[type=submit]");
      const say = (key, kind) => {
        status.textContent = form.dataset[`msg${key[0].toUpperCase()}${key.slice(1)}`] || form.dataset.msgError;
        status.dataset.kind = kind;
      };

      const fresh = () =>
        fetch(`${endpoint}/challenge`, { headers: { Accept: "application/json" } })
          .then((response) => (response.ok ? response.json() : Promise.reject(new Error(String(response.status)))))
          .then(async ({ challenge, bits }) => ({ challenge, nonce: await solve(challenge, bits) }));
      let proof = fresh();
      proof.catch(() => {});

      form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (!form.elements.email.value.trim() || !form.elements.email.checkValidity()) {
          say("email", "error");
          form.elements.email.focus();
          return;
        }
        if (!form.elements.repositories.value.trim()) {
          say("repositories", "error");
          form.elements.repositories.focus();
          return;
        }
        if (!form.elements.terms.checked) {
          say("terms", "error");
          form.elements.terms.focus();
          return;
        }

        button.disabled = true;
        say("working", "busy");
        try {
          const { challenge, nonce } = await proof.catch(() => (proof = fresh()));
          say("sending", "busy");
          const response = await fetch(endpoint, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ email: form.elements.email.value.trim(), repositories: form.elements.repositories.value, challenge, nonce }),
          });
          const answer = await response.json().catch(() => ({}));
          // A token is good once: solve the next one in the background, whatever happened.
          proof = fresh();
          proof.catch(() => {});
          if (answer.status === "accepted") {
            say("accepted", "ok");
            form.reset();
          } else if (answer.status === "invalid") {
            say(answer.field, "error");
            form.elements[answer.field]?.focus?.();
          } else {
            say(answer.status ?? "error", "error");
          }
        } catch {
          say("error", "error");
        } finally {
          button.disabled = false;
        }
      });
    }
  });
})();
