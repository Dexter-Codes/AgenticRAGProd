import { call, activity, routes } from "./api.js";
const $ = (s) => document.querySelector(s);
const el = (tag, text, className) => {
  const n = document.createElement(tag);
  if (text !== undefined) n.textContent = text;
  if (className) n.className = className;
  return n;
};
const add = (parent, ...children) => {
  parent.append(...children.filter(Boolean));
  return parent;
};
const money = (value) =>
  new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: "INR",
    maximumFractionDigits: 2,
  }).format(value?.amount ?? 0);
const pretty = (data) => el("pre", JSON.stringify(data, null, 2));
const jsonDetails = (title, data) =>
  add(el("details"), el("summary", title), pretty(data));
const storage = {
  get(key, fallback) {
    try {
      return JSON.parse(localStorage.getItem(key)) ?? fallback;
    } catch {
      return fallback;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch {}
  },
};
let current = storage.get("tc.current", null),
  busy = false,
  chatEnabled = false;
function persist() {
  storage.set("tc.current", current);
  const ids = storage.get("tc.recent", []);
  if (current) {
    storage.set(
      "tc.recent",
      [current.runId, ...ids.filter((x) => x !== current.runId)].slice(0, 12),
    );
  }
  recent();
}
function errorAt(target, error) {
  const box = el("div", error.message || String(error), "error");
  box.setAttribute("role", "alert");
  if (error.data?.errors)
    add(
      box,
      el(
        "p",
        Object.entries(error.data.errors)
          .map(([k, v]) => `${k}: ${v.join(" ")}`)
          .join("\n"),
      ),
    );
  target.replaceChildren(box);
}
async function action(form, target, fn) {
  if (form.dataset.busy) return;
  form.dataset.busy = "true";
  const buttons = [...form.querySelectorAll("button")];
  buttons.forEach((b) => (b.disabled = true));
  target.replaceChildren(el("p", "Working…", "muted"));
  try {
    await fn();
  } catch (e) {
    errorAt(target, e);
  } finally {
    delete form.dataset.busy;
    buttons.forEach((b) => (b.disabled = false));
    if (form.id === "chat-form") $("#chat-send").disabled = !chatEnabled;
  }
}
function navigate() {
  const name = location.hash.slice(1) || "plan";
  const view = ["plan", "runs", "knowledge", "retrieval", "explorer"].includes(
    name,
  )
    ? name
    : "plan";
  document
    .querySelectorAll("[data-view]")
    .forEach((n) => (n.hidden = n.dataset.view !== view));
  document.querySelectorAll("nav a").forEach((a) => {
    const active = a.hash === `#${view}`;
    a.classList.toggle("active", active);
    if (active) a.setAttribute("aria-current", "page");
    else a.removeAttribute("aria-current");
  });
  $("#breadcrumb").textContent = $(`nav a[href="#${view}"] span`).textContent;
}
window.addEventListener("hashchange", navigate);
navigate();
const localDate = (offset) => {
  const d = new Date();
  d.setDate(d.getDate() + offset);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};
const tripForm = $("#trip-form");
tripForm.elements.departureDate.value = localDate(7);
tripForm.elements.returnDate.value = localDate(12);
function tripRequest() {
  const f = new FormData(tripForm);
  return {
    origin: f.get("origin"),
    destination: f.get("destination"),
    departureDate: f.get("departureDate"),
    returnDate: f.get("returnDate"),
    travelerCount: Number(f.get("travelerCount")),
    style: f.get("style"),
    budget: { amount: Number(f.get("budget")), currency: "INR" },
  };
}
function fillTrip(r) {
  for (const name of [
    "origin",
    "destination",
    "departureDate",
    "returnDate",
    "travelerCount",
    "style",
  ])
    tripForm.elements[name].value = r[name];
  tripForm.elements.budget.value = r.budget.amount;
}
$("#validate").onclick = () => {
  if (tripForm.reportValidity())
    action(tripForm, $("#plan-message"), async () => {
      await call("/api/trips/validate", "POST", tripRequest());
      $("#plan-message").replaceChildren(
        el(
          "p",
          "Inputs validated by the server. Ready when you are.",
          "success",
        ),
      );
    });
};
tripForm.onsubmit = (e) => {
  e.preventDefault();
  if (busy) return;
  action(tripForm, $("#plan-message"), async () => {
    const request = tripRequest();
    await call("/api/trips/validate", "POST", request);
    current = {
      runId: crypto.randomUUID(),
      request,
      selectedDestinationId: null,
    };
    persist();
    await plan();
  });
};
function sameOriginal() {
  const r = current?.request;
  const a = tripRequest();
  return (
    r &&
    [
      "origin",
      "destination",
      "departureDate",
      "returnDate",
      "travelerCount",
      "style",
    ].every((k) => a[k] === r[k]) &&
    a.budget.amount === r.budget.amount &&
    a.budget.currency === r.budget.currency
  );
}
async function readRun(id, restore = false) {
  const saved = await call(`/api/trip-runs/${encodeURIComponent(id)}`);
  if (restore) {
    current = {
      runId: saved.runId,
      request: saved.request,
      selectedDestinationId: saved.selectedDestinationId,
      draft: saved.draft,
      checkpoint: saved.checkpoint,
    };
    fillTrip(saved.request);
    persist();
  } else if (current?.runId === id) {
    current.draft = saved.draft;
    current.checkpoint = saved.checkpoint;
    current.selectedDestinationId = saved.selectedDestinationId;
    persist();
  }
  renderRun();
  return saved;
}
async function plan(selection) {
  if (busy) return;
  if (!sameOriginal()) {
    throw new Error(
      "The trip has been edited. Use Start planning to create a new run for these inputs.",
    );
  }
  busy = true;
  if (selection !== undefined) current.selectedDestinationId = selection;
  persist();
  renderRun();
  const id = current.runId;
  let pollActive = true;
  const poll = async () => {
    if (!pollActive) return;
    try {
      await readRun(id);
    } catch {}
    if (pollActive) setTimeout(poll, 1500);
  };
  setTimeout(poll, 800);
  try {
    const body = { ...current.request };
    if (current.selectedDestinationId != null)
      body.selectedDestinationId = current.selectedDestinationId;
    const draft = await call(`/api/trip-runs/${id}`, "PUT", body);
    if (current.runId === id) {
      current.draft = draft;
      persist();
    }
    $("#plan-message").replaceChildren(
      el("p", "Run saved. You can reopen it by ID at any time.", "success"),
    );
  } catch (e) {
    errorAt($("#plan-message"), e);
    if (e.status === 409)
      add(
        $("#plan-message"),
        el(
          "p",
          "The run is active or has advanced. Read its latest state, then explicitly retry the same run.",
        ),
      );
  } finally {
    pollActive = false;
    try {
      await readRun(id);
    } catch {}
    busy = false;
    renderRun();
  }
}
function sourceButtons(sources) {
  const list = el("div", undefined, "source-list");
  sources.forEach((source, index) => {
    const b = el(
      "button",
      `[${source.number ?? index + 1}] ${source.documentName || "Source"} · p. ${source.pageNumber ?? "?"}`,
      "secondary",
    );
    b.type = "button";
    b.onclick = () => {
      const content = $("#source-content");
      content.replaceChildren(
        el(
          "p",
          `${source.documentName || "Document"} · page ${source.pageNumber ?? "?"} `,
        ),
        el("p", source.passage || "No passage returned.", "passage"),
        jsonDetails("Provenance & extraction context", source),
      );
      $("#source-dialog").showModal();
    };
    list.append(b);
  });
  return list;
}
$("#close-source").onclick = () => $("#source-dialog").close();
function budgetView(b, revision) {
  const card = el("article", undefined, "card");
  add(
    card,
    el("h2", "A closer look at your budget"),
    el(
      "p",
      "Fictional transport, lodging and food estimates. Activities are unpriced and excluded.",
      "notice",
    ),
  );
  const stats = el("div", undefined, "stats");
  for (const [label, value] of [
    ["Your budget", b.budget],
    ["Estimated total", b.total],
    ["Remaining", b.remaining],
  ])
    add(
      stats,
      add(
        el("div", undefined, "stat"),
        el("span", label),
        el("strong", money(value)),
      ),
    );
  card.append(stats);
  const table = el("table");
  const head = el("tr");
  ["Demo option", "Quantity", "Unit price", "Total"].forEach((v) =>
    head.append(el("th", v)),
  );
  table.append(add(el("thead"), head));
  const body = el("tbody");
  b.lines.forEach((l) => {
    const row = el("tr");
    add(
      row,
      add(el("td", l.description), el("small", l.priceSource)),
      el("td", String(l.quantity)),
      el("td", money(l.unitPrice)),
      el("td", money(l.total)),
    );
    body.append(row);
  });
  table.append(body);
  add(
    card,
    add(el("div", undefined, "table-wrap"), table),
    el(
      "p",
      b.isWithinBudget
        ? "Within demo budget"
        : "Over budget — review the original trip and start a new run.",
      "hint",
    ),
  );
  if (revision) {
    add(
      card,
      el("h3", "Supervisor review"),
      el(
        "p",
        `${revision.status} · Original total ${money(revision.initialTotal)}`,
      ),
    );
    for (const change of revision.changes) {
      card.append(
        el(
          "p",
          `${change.previousOptionId} replaced with ${change.replacementOptionId}`,
          "hint",
        ),
      );
    }
    add(
      card,
      jsonDetails(
        `Supervisor revision · ${revision.status} · initial ${money(revision.initialTotal)}`,
        revision,
      ),
    );
  }
  return card;
}
function renderRun() {
  const panel = $("#run-panel");
  panel.replaceChildren();
  if (!current) return;
  const d = current.draft,
    c = current.checkpoint || {};
  const card = el("article", undefined, "card");
  add(
    card,
    el("h2", busy ? "Planning your trip…" : "Your trip workspace"),
    el("p", `Run ID: ${current.runId}`),
    el(
      "p",
      `Status: ${d?.status ?? "Saved request · awaiting explicit planning"}`,
      "muted",
    ),
  );
  const progress = el("div", undefined, "progress");
  for (const [key, title] of [
    ["destination", "Destination"],
    ["weather", "Weather"],
    ["budgetDraft", "Budget"],
    ["knowledge", "Knowledge"],
    ["itinerary", "Itinerary"],
  ]) {
    const stage = el(
      "span",
      `${c[key] ? "✓" : "○"} ${title}`,
      `stage ${c[key] ? "done" : ""}`,
    );
    progress.append(stage);
  }
  add(
    card,
    progress,
    el(
      "p",
      `Last completed checkpoint: ${c.lastCompletedStage || "created"}`,
      "hint",
    ),
  );
  const buttons = el("div", undefined, "actions");
  const refresh = el("button", "Refresh saved state", "secondary");
  refresh.disabled = busy;
  refresh.onclick = () =>
    action(buttons, $("#plan-message"), () => readRun(current.runId));
  buttons.append(refresh);
  if (!d || d.status !== "awaiting_destination_selection") {
    const retry = el(
      "button",
      d ? "Replay / resume same run" : "Continue this run",
    );
    retry.disabled = busy;
    retry.onclick = () => action(buttons, $("#plan-message"), () => plan());
    buttons.append(retry);
  }
  card.append(buttons);
  if (d?.status === "awaiting_destination_selection") {
    card.append(el("h3", "Which destination did you have in mind?"));
    d.destinationLookup.candidates.forEach((candidate) => {
      const row = el("div", undefined, "candidate");
      const info = add(
        el("div"),
        el("strong", candidate.name),
        el(
          "p",
          [candidate.country, candidate.admin1, candidate.timezone]
            .filter(Boolean)
            .join(" · "),
        ),
        el("small", `Destination ID ${candidate.id}`),
      );
      const choose = el("button", "Select & continue");
      choose.disabled = busy;
      choose.onclick = () =>
        action(tripForm, $("#plan-message"), () => plan(candidate.id));
      add(row, info, choose);
      card.append(row);
    });
    card.append(el("p", d.destinationLookup.attribution, "hint"));
  }
  panel.append(card);
  const draft = d?.budget ? d : c.budgetDraft;
  if (draft?.budget)
    panel.append(budgetView(draft.budget, draft.budgetRevision));
  const weather = d?.weather || c.weather;
  const knowledge = d?.knowledge || c.knowledge;
  const itinerary = d?.itinerary || c.itinerary;
  if (weather) {
    const wc = el("article", undefined, "card");
    add(
      wc,
      el("h2", "Weather outlook"),
      el("p", `Forecast status: ${weather.status}`),
      el(
        "p",
        `Provider window: ${weather.availableFrom || "unknown"} to ${weather.availableThrough || "unknown"}. Dates outside this range have no forecast. Missing values are unknown.`,
        "hint",
      ),
      el("p", `${weather.attribution} · checked ${weather.checkedAt}`, "hint"),
    );
    panel.append(wc);
  }
  if (itinerary) {
    const it = el("article", undefined, "card");
    add(
      it,
      el("h2", "Your daily outline"),
      el("p", itinerary.notice, "notice"),
      el("p", `Itinerary status: ${itinerary.status}`, "hint"),
      el(
        "p",
        "Named attractions and per-activity citations are not returned by the current planner. Guide passages below are retrieval evidence, not linked claims for these generic slots.",
        "hint",
      ),
    );
    itinerary.days.forEach((day) => {
      const row = el("div", undefined, "day");
      const w = weather?.days?.find((x) => x.date === day.date);
      add(
        row,
        el("h3", day.date),
        el(
          "p",
          w
            ? `${w.minimumTemperatureC ?? "?"}–${w.maximumTemperatureC ?? "?"} °C`
            : "Forecast unavailable for this date",
          "hint",
        ),
      );
      const slots = el("div", undefined, "slots");
      for (const slot of ["morning", "afternoon", "evening"])
        add(
          slots,
          add(
            el("p"),
            el("b", slot.toUpperCase()),
            el(
              "span",
              itinerary.activityCatalog[day[`${slot}ActivityId`]] ||
                day[`${slot}ActivityId`],
            ),
          ),
        );
      row.append(slots);
      it.append(row);
    });
    panel.append(it);
  }
  if (knowledge) {
    const kc = el("article", undefined, "card");
    add(
      kc,
      el("h2", "Guide passages"),
      el(
        "p",
        `Knowledge status: ${knowledge.status}`,
        knowledge.status === "retrieved" ? "success" : "notice",
      ),
    );
    if (knowledge.status !== "retrieved")
      kc.append(
        el(
          "p",
          "No usable guide evidence was returned. Check destination-to-document configuration and RAG availability.",
          "hint",
        ),
      );
    add(
      kc,
      sourceButtons(knowledge.passages || []),
      jsonDetails("Returned knowledge JSON", knowledge),
    );
    panel.append(kc);
  }
  if (d) panel.append(jsonDetails("Complete saved draft", d));
}
function recent() {
  const box = $("#recent-runs");
  box.replaceChildren(el("h3", "Recently opened in this browser"));
  const ids = storage.get("tc.recent", []);
  if (!ids.length)
    box.append(el("p", "No runs yet. Start with a destination.", "muted"));
  ids.forEach((id) => {
    const b = el("button", id, "secondary");
    b.style.margin = "6px";
    b.onclick = () => openRun(id);
    box.append(b);
  });
}
async function openRun(id) {
  if (busy) return;
  await action($("#open-run"), $("#run-message"), async () => {
    await readRun(id, true);
    $("#run-message").replaceChildren();
    location.hash = "plan";
  });
}
$("#open-run").onsubmit = (e) => {
  e.preventDefault();
  const id = new FormData(e.target).get("runId").trim();
  if (!/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(id)) {
    errorAt($("#run-message"), new Error("Enter a valid GUID run ID."));
    return;
  }
  openRun(id);
};
$("#upload-form").onsubmit = (e) => {
  e.preventDefault();
  action(e.target, $("#upload-result"), async () => {
    const data = new FormData(e.target),
      file = data.get("file");
    if (
      !/\.pdf$/i.test(file.name) ||
      !file.size ||
      file.size > 10 * 1024 * 1024
    )
      throw new Error("Upload a non-empty PDF no larger than 10 MB.");
    const result = await call("/api/documents", "POST", data);
    $("#index-form").elements.documentId.value = result.documentId;
    $("#index-form").elements.documentName.value = result.fileName;
    $("#upload-result").replaceChildren(pretty(result));
  });
};
$("#index-form").onsubmit = (e) => {
  e.preventDefault();
  action(e.target, $("#index-result"), async () => {
    const f = new FormData(e.target);
    const r = await call(
      `/api/documents/${encodeURIComponent(f.get("documentId"))}/index?documentName=${encodeURIComponent(f.get("documentName"))}`,
      "POST",
    );
    $("#index-result").replaceChildren(pretty(r));
  });
};
$("#conversation-id").value = storage.get("tc.conversation", "");
$("#chat-form").onsubmit = (e) => {
  e.preventDefault();
  if (!chatEnabled) return;
  const target = el("div");
  $("#chat-messages").append(target);
  action(e.target, target, async () => {
    const query = new FormData(e.target).get("query");
    const result = await call("/api/chat", "POST", {
      conversationId: $("#conversation-id").value.trim() || null,
      query,
    });
    $("#conversation-id").value = result.conversationId;
    storage.set("tc.conversation", result.conversationId);
    target.replaceChildren(
      el("strong", query),
      el("p", result.answer),
      el(
        "small",
        `Answer kind: ${result.answerKind} · Message: ${result.messageId}`,
      ),
      sourceButtons(result.sources || []),
    );
    target.className = "chat-entry";
    e.target.reset();
  });
};
$("#retrieval-form").onsubmit = (e) => {
  e.preventDefault();
  action(e.target, $("#retrieval-result"), async () => {
    const f = new FormData(e.target);
    const result = await call(
      f.get("transport") === "mcp"
        ? "/api/mcp/search"
        : "/api/travel-knowledge/search",
      "POST",
      { destinationId: Number(f.get("destinationId")), query: f.get("query") },
    );
    const payload = result.structuredContent || result;
    const target = $("#retrieval-result");
    target.replaceChildren(
      el(
        "p",
        result.isError
          ? "MCP tool returned an error"
          : `Status: ${payload.status || "MCP response received"}`,
        result.isError ? "error" : "success",
      ),
      sourceButtons(payload.passages || []),
      jsonDetails("Response & provenance", result),
    );
  });
};
$("#discover").onclick = () =>
  action($("#discover").parentElement, $("#tools-result"), async () => {
    const tools = await call("/api/mcp/tools");
    $("#tools-result").replaceChildren(
      ...tools.map((t) =>
        add(
          el("details"),
          el("summary", t.name),
          el("p", t.description),
          pretty(t.schema),
        ),
      ),
    );
    if (!tools.length) $("#tools-result").append(el("p", "No tools returned."));
  });
const op = $("#operation");
routes.forEach(([method, path, title], i) => {
  const option = el("option", `${method} ${path} · ${title}`);
  option.value = i;
  op.append(option);
});
function operationChanged() {
  const [method, path, , paid, kind] = routes[op.value];
  $("#path-label").hidden = !path.includes("{id}");
  $("#document-name-label").hidden = kind !== "index";
  $("#explorer-document-name").required = kind === "index";
  $("#explorer-document-name").value =
    $("#index-form").elements.documentName.value;
  $("#json-label").hidden =
    method === "GET" || kind === "upload" || kind === "index";
  $("#operation-note").textContent = paid
    ? "Invokes models and/or embeddings. Each click is a new explicit operation."
    : "No model or embedding call for this operation.";
  $("#request-json").value = JSON.stringify(
    kind === "trip"
      ? tripRequest()
      : kind === "chat"
        ? {
            conversationId: $("#conversation-id").value || null,
            query: "What should I know before visiting?",
          }
        : kind === "search"
          ? { destinationId: 1581130, query: "Visitor attractions" }
          : {},
    null,
    2,
  );
  if (kind === "upload")
    $("#operation-note").textContent =
      "Choose a PDF in the Knowledge upload form to send multipart content.";
  $("#path-id").value =
    kind === "index"
      ? $("#index-form").elements.documentId.value
      : current?.runId || "";
}
op.onchange = operationChanged;
operationChanged();
$("#explorer-form").onsubmit = (e) => {
  e.preventDefault();
  action(e.target, $("#explorer-result"), async () => {
    const [method, template, , , kind] = routes[op.value];
    if (kind === "upload") {
      location.hash = "knowledge";
      $("#explorer-result").replaceChildren(
        el("p", "Use the PDF upload form."),
      );
      return;
    }
    let path = template;
    if (path.includes("{id}")) {
      const id = $("#path-id").value.trim();
      if (
        !/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(id)
      )
        throw new Error("Enter a valid GUID.");
      path = path.replace("{id}", id);
    }
    if (kind === "index")
      path += `?documentName=${encodeURIComponent($("#explorer-document-name").value)}`;
    const body =
      method === "GET" || kind === "index"
        ? undefined
        : JSON.parse($("#request-json").value);
    const result = await call(path, method, body);
    const latest = activity[0];
    $("#explorer-result").replaceChildren(
      el(
        "p",
        `${latest.status} · ${latest.duration} ms · Trace: ${latest.traceId || "not supplied"}`,
        "activity-meta",
      ),
      jsonDetails("Response JSON", result),
    );
  });
};
document.addEventListener("api-activity", () => {
  $("#activity").replaceChildren(
    ...activity.map((a) =>
      add(
        el("details"),
        el("summary", `${a.method} ${a.path} · ${a.status} · ${a.duration} ms`),
        el("p", `Trace ID: ${a.traceId || "not supplied"}`, "activity-meta"),
        pretty({ request: a.request, response: a.response }),
      ),
    ),
  );
});
recent();
renderRun();
if (current) {
  fillTrip(current.request);
  readRun(current.runId).catch((e) =>
    errorAt(
      $("#plan-message"),
      new Error(
        `Could not restore saved state: ${e.message}. Your original request and run ID remain in this browser.`,
      ),
    ),
  );
}
call("/api/local/capabilities")
  .then((data) => {
    chatEnabled = data.chatEnabled === true;
    $("#chat-availability").textContent = chatEnabled
      ? "Local single-user chat is enabled."
      : "Local chat is disabled. Enable Chat:LocalSingleUserMode in the Development RAG backend and restart it.";
    $("#chat-send").disabled = !chatEnabled;
  })
  .catch((e) => {
    $("#chat-availability").textContent =
      `Chat availability unknown: ${e.message}. Check the configured RAG API, then reload.`;
  });
