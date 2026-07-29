/*
 * Novee2Mqtt control panel.
 *
 * Plain ES2020, no build step and no dependencies: the bridge is frequently
 * installed on networks with no route to the internet, so the page has to be
 * able to render from the container alone.
 *
 * Live updates arrive over /api/events (server-sent events); polling is only a
 * fallback for when that stream cannot be established.
 */

const POLL_MS = 5000;
// After a command, ignore server state for this device briefly: the transports
// take a moment to report back and we do not want the slider to snap backwards.
const SUPPRESS_MS = 4000;

const COLOR_PRESETS = [
  "#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#00c7be",
  "#30b0ff", "#5b8cff", "#af52de", "#ff2d92", "#ffffff",
];

const KELVIN_PRESETS = [2200, 2700, 3500, 4500, 5500, 6500];

const ui = {
  devices: [],
  cards: new Map(),
  layout: "",
  filter: "",
  suppressed: new Map(),
  tabs: new Map(),
  scenes: new Map(),
  lastMessage: 0,
};

/* ------------------------------------------------------------------ helpers */

const $ = (sel, root = document) => root.querySelector(sel);

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (value !== null && value !== undefined) node.setAttribute(key, value);
  }
  for (const child of [].concat(children)) {
    if (child) node.append(child);
  }
  return node;
}

const hex = (c) =>
  "#" + [c.r, c.g, c.b].map((v) => (v || 0).toString(16).padStart(2, "0")).join("");

/* Approximate black-body colour, so white modes still show a believable tint. */
function kelvinToHex(kelvin) {
  const t = Math.max(1000, Math.min(40000, kelvin)) / 100;
  let r, g, b;

  if (t <= 66) {
    r = 255;
    g = 99.47 * Math.log(t) - 161.12;
  } else {
    r = 329.7 * Math.pow(t - 60, -0.1332);
    g = 288.12 * Math.pow(t - 60, -0.0755);
  }

  if (t >= 66) b = 255;
  else if (t <= 19) b = 0;
  else b = 138.52 * Math.log(t - 10) - 305.04;

  const clamp = (v) => Math.max(0, Math.min(255, Math.round(v)));
  return hex({ r: clamp(r), g: clamp(g), b: clamp(b) });
}

function ago(iso) {
  if (!iso) return "never";
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 45) return "just now";
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)}h ago`;
  return `${Math.round(seconds / 86400)}d ago`;
}

function sourceTag(source) {
  const value = (source || "").toUpperCase();
  if (value.includes("LAN")) return { label: "LAN", cls: "tag tag--lan" };
  if (value.includes("IOT")) return { label: "IoT", cls: "tag tag--iot" };
  if (value.includes("PLATFORM")) return { label: "Cloud", cls: "tag tag--cloud" };
  return { label: source || "unknown", cls: "tag" };
}

function toast(message, kind = "error") {
  const node = el("div", { class: `toast toast--${kind}`, text: message });
  $("#toasts").append(node);
  setTimeout(() => {
    node.style.opacity = "0";
    node.style.transition = "opacity .3s";
    setTimeout(() => node.remove(), 320);
  }, kind === "error" ? 6000 : 2600);
}

/* Every control funnels through here so failures are visible rather than silent. */
async function call(path, options = {}) {
  try {
    const response = await fetch(path, options);
    if (!response.ok) {
      let detail = `HTTP ${response.status}`;
      try {
        const body = await response.json();
        if (body && body.msg) detail = body.msg;
      } catch { /* non-JSON error body */ }
      toast(detail);
      return false;
    }
    return true;
  } catch (error) {
    toast(`Request failed: ${error.message}`);
    return false;
  }
}

const suppress = (id) => ui.suppressed.set(id, Date.now() + SUPPRESS_MS);
const isSuppressed = (id) => (ui.suppressed.get(id) || 0) > Date.now();

/* -------------------------------------------------------------------- cards */

function buildCard(device) {
  const refs = {};
  const id = device.id;

  refs.orb = el("div", { class: "orb" });
  refs.name = el("h3", { class: "card__name", text: device.name });
  refs.meta = el("div", { class: "card__meta" });

  refs.power = el("button", {
    class: "switch",
    role: "switch",
    "aria-checked": "false",
    "aria-label": `Power for ${device.name}`,
    onclick: async () => {
      const next = refs.power.getAttribute("aria-checked") !== "true";
      refs.power.setAttribute("aria-checked", String(next));
      refs.root.classList.toggle("is-on", next);
      suppress(id);
      await call(`/api/device/${encodeURIComponent(id)}/power/${next ? "on" : "off"}`);
    },
  });

  refs.brightValue = el("span", { class: "control__value", text: "--" });
  refs.bright = el("input", { type: "range", min: "1", max: "100", value: "50",
    "aria-label": `Brightness for ${device.name}` });

  refs.bright.addEventListener("input", () => {
    refs.brightValue.textContent = `${refs.bright.value}%`;
    paintBrightness(refs);
    suppress(id);
  });
  refs.bright.addEventListener("change", async () => {
    suppress(id);
    await call(`/api/device/${encodeURIComponent(id)}/brightness/${refs.bright.value}`);
  });

  // Colour panel
  const swatches = el("div", { class: "swatches" },
    COLOR_PRESETS.map((color) =>
      el("button", {
        class: "swatch",
        style: `background:${color};color:${color}`,
        title: color,
        "aria-label": `Set ${device.name} to ${color}`,
        onclick: async () => {
          refs.picker.value = color;
          applyAccent(refs, color);
          suppress(id);
          await call(`/api/device/${encodeURIComponent(id)}/color/${color.slice(1)}`);
        },
      })));

  refs.picker = el("input", { type: "color", value: "#ffffff",
    "aria-label": `Custom colour for ${device.name}` });
  refs.picker.addEventListener("change", async () => {
    applyAccent(refs, refs.picker.value);
    suppress(id);
    await call(`/api/device/${encodeURIComponent(id)}/color/${refs.picker.value.slice(1)}`);
  });

  const colorPanel = el("div", { class: "panel", "data-panel": "color" }, [
    swatches,
    el("div", { class: "picker" }, [el("span", { text: "Custom" }), refs.picker]),
  ]);

  // White panel
  refs.kelvinValue = el("span", { class: "control__value", text: "--" });
  refs.kelvin = el("input", { type: "range", min: "2000", max: "9000", step: "100", value: "4000",
    "aria-label": `Colour temperature for ${device.name}` });
  refs.kelvin.style.setProperty(
    "--track",
    `linear-gradient(90deg, ${kelvinToHex(2000)}, ${kelvinToHex(4000)}, ${kelvinToHex(9000)})`);

  refs.kelvin.addEventListener("input", () => {
    refs.kelvinValue.textContent = `${refs.kelvin.value}K`;
  });
  refs.kelvin.addEventListener("change", async () => {
    suppress(id);
    applyAccent(refs, kelvinToHex(Number(refs.kelvin.value)));
    await call(`/api/device/${encodeURIComponent(id)}/colortemp/${refs.kelvin.value}`);
  });

  const whitePanel = el("div", { class: "panel", "data-panel": "white" }, [
    el("div", { class: "control" }, [
      el("div", { class: "control__label" }, [
        el("span", { text: "Temperature" }), refs.kelvinValue,
      ]),
      refs.kelvin,
    ]),
    el("div", { class: "swatches" },
      KELVIN_PRESETS.map((k) =>
        el("button", {
          class: "swatch",
          style: `background:${kelvinToHex(k)};color:${kelvinToHex(k)}`,
          title: `${k}K`,
          "aria-label": `Set ${device.name} to ${k} kelvin`,
          onclick: async () => {
            refs.kelvin.value = String(k);
            refs.kelvinValue.textContent = `${k}K`;
            applyAccent(refs, kelvinToHex(k));
            suppress(id);
            await call(`/api/device/${encodeURIComponent(id)}/colortemp/${k}`);
          },
        }))),
  ]);

  // Scene panel
  refs.scene = el("select", { "aria-label": `Scene for ${device.name}` }, [
    el("option", { value: "", text: "Loading scenes..." }),
  ]);
  refs.scene.addEventListener("change", async () => {
    if (!refs.scene.value) return;
    suppress(id);
    await call(`/api/device/${encodeURIComponent(id)}/scene/${encodeURIComponent(refs.scene.value)}`);
  });

  const scenePanel = el("div", { class: "panel", "data-panel": "scene" }, [refs.scene]);

  refs.panels = { color: colorPanel, white: whitePanel, scene: scenePanel };

  refs.tabs = el("div", { class: "tabs", role: "tablist" },
    [["color", "Colour"], ["white", "White"], ["scene", "Scene"]].map(([key, label]) =>
      el("button", {
        role: "tab",
        "aria-selected": String(key === (ui.tabs.get(id) || "color")),
        text: label,
        onclick: () => selectTab(id, refs, key),
      })));

  refs.foot = el("div", { class: "card__foot" });

  refs.root = el("div", { class: "card", "data-id": id }, [
    el("div", { class: "card__head" }, [
      refs.orb,
      el("div", { class: "card__title" }, [refs.name, refs.meta]),
      refs.power,
    ]),
    el("div", { class: "control" }, [
      el("div", { class: "control__label" }, [
        el("span", { text: "Brightness" }), refs.brightValue,
      ]),
      refs.bright,
    ]),
    refs.tabs,
    colorPanel,
    whitePanel,
    scenePanel,
    refs.foot,
  ]);

  selectTab(id, refs, ui.tabs.get(id) || "color", true);
  return refs;
}

const TAB_LABELS = { color: "colour", white: "white", scene: "scene" };

function selectTab(id, refs, key, silent = false) {
  ui.tabs.set(id, key);

  for (const button of refs.tabs.children) {
    button.setAttribute("aria-selected", String(button.textContent.toLowerCase() === TAB_LABELS[key]));
  }
  for (const [name, panel] of Object.entries(refs.panels)) {
    panel.classList.toggle("is-active", name === key);
  }

  if (key === "scene" && !silent) loadScenes(id, refs);
}

async function loadScenes(id, refs) {
  if (ui.scenes.has(id)) return;
  ui.scenes.set(id, true);

  try {
    const response = await fetch(`/api/device/${encodeURIComponent(id)}/scenes`);
    const scenes = response.ok ? await response.json() : [];
    refs.scene.replaceChildren(
      el("option", { value: "", text: scenes.length ? "Select a scene..." : "No scenes available" }),
      ...scenes.map((name) => el("option", { value: name, text: name })));
  } catch {
    refs.scene.replaceChildren(el("option", { value: "", text: "Could not load scenes" }));
  }
}

function paintBrightness(refs) {
  const pct = Number(refs.bright.value);
  const color = refs.root.style.getPropertyValue("--accent") || "#5b8cff";
  refs.bright.style.setProperty(
    "--track",
    `linear-gradient(90deg, ${color} 0 ${pct}%, var(--surface-2) ${pct}% 100%)`);
}

function applyAccent(refs, color) {
  refs.root.style.setProperty("--accent", color);
  paintBrightness(refs);
}

function updateCard(refs, device) {
  const state = device.state;
  refs.name.textContent = device.name;

  const on = !!(state && state.on);
  const offline = state && state.online === false;

  refs.root.classList.toggle("is-on", on);
  refs.root.classList.toggle("is-offline", !!offline);

  // Leave the controls alone while the user is still working with them.
  if (!isSuppressed(device.id)) {
    refs.power.setAttribute("aria-checked", String(on));

    if (state) {
      const accent = state.kelvin > 0 ? kelvinToHex(state.kelvin) : hex(state.color || {});
      applyAccent(refs, accent === "#000000" ? "#5b8cff" : accent);

      if (state.brightness > 0) {
        refs.bright.value = String(Math.min(100, state.brightness));
        refs.brightValue.textContent = `${refs.bright.value}%`;
      }
      if (state.kelvin > 0) {
        refs.kelvin.value = String(Math.max(2000, Math.min(9000, state.kelvin)));
        refs.kelvinValue.textContent = `${state.kelvin}K`;
      }
      if (state.color) refs.picker.value = hex(state.color);
      paintBrightness(refs);
    }
  }

  const meta = [el("span", { text: device.sku })];
  if (state && state.scene) meta.push(el("span", { class: "tag", text: state.scene }));
  if (offline) meta.push(el("span", { class: "tag tag--offline", text: "offline" }));
  refs.meta.replaceChildren(...meta);

  const foot = [];
  if (state) {
    const tag = sourceTag(state.source);
    foot.push(el("span", { class: tag.cls, text: tag.label }));
    foot.push(el("span", { text: ago(state.updated) }));
  } else {
    foot.push(el("span", { text: "no state reported" }));
  }
  if (device.ip) foot.push(el("span", { text: device.ip }));
  foot.push(el("span", { class: "card__id", text: device.id }));
  refs.foot.replaceChildren(...foot);
}

/* ------------------------------------------------------------------- render */

function render(devices) {
  ui.devices = devices;

  const rooms = new Map();
  for (const device of devices) {
    const room = device.room || "Ungrouped";
    if (!rooms.has(room)) rooms.set(room, []);
    rooms.get(room).push(device);
  }

  // Only rebuild the DOM when the grouping itself changed; otherwise update in
  // place so focus, open dropdowns and in-progress drags survive.
  const layout = [...rooms].map(([room, list]) => room + ":" + list.map((d) => d.id).join(",")).join("|");

  if (layout !== ui.layout) {
    ui.layout = layout;
    ui.cards.clear();

    const main = $("#content");
    main.replaceChildren();

    if (!devices.length) {
      main.append(emptyState());
      updateStats();
      return;
    }

    for (const [room, list] of rooms) {
      const grid = el("div", { class: "grid" });
      for (const device of list) {
        const refs = buildCard(device);
        ui.cards.set(device.id, refs);
        grid.append(refs.root);
      }

      main.append(el("section", { class: "room" }, [
        el("div", { class: "room__head" }, [
          el("h2", { class: "room__name", text: room }),
          el("span", { class: "room__count", text: `${list.length} device${list.length === 1 ? "" : "s"}` }),
          room === "Ungrouped" ? null : el("div", { class: "room__actions" }, [
            el("button", {
              class: "chip", text: "All on",
              onclick: () => call(`/api/room/${encodeURIComponent(room)}/power/on`),
            }),
            el("button", {
              class: "chip", text: "All off",
              onclick: () => call(`/api/room/${encodeURIComponent(room)}/power/off`),
            }),
          ]),
        ]),
        grid,
      ]));
    }
  }

  for (const device of devices) {
    const refs = ui.cards.get(device.id);
    if (refs) updateCard(refs, device);
  }

  applyFilter();
  updateStats();
}

function emptyState() {
  return el("div", { class: "empty" }, [
    el("h2", { text: "No devices yet" }),
    el("p", { text: "Nothing has answered on the LAN and no cloud credentials returned devices." }),
    el("p", {}, [
      document.createTextNode("Set "),
      el("code", { text: "GOVEE_EMAIL" }),
      document.createTextNode(" and "),
      el("code", { text: "GOVEE_PASSWORD" }),
      document.createTextNode(" for cloud devices, or enable LAN control in the Govee app."),
    ]),
    el("p", { text: "LAN discovery also needs host networking, so the bridge can receive UDP 4002." }),
  ]);
}

function applyFilter() {
  const needle = ui.filter.trim().toLowerCase();

  for (const section of document.querySelectorAll(".room")) {
    let visible = 0;
    for (const card of section.querySelectorAll(".card")) {
      const device = ui.devices.find((d) => d.id === card.dataset.id);
      const haystack = [device?.name, device?.sku, device?.room, device?.ip, device?.id]
        .join(" ").toLowerCase();
      const show = !needle || haystack.includes(needle);
      card.style.display = show ? "" : "none";
      if (show) visible++;
    }
    section.style.display = visible ? "" : "none";
  }
}

function updateStats() {
  const total = ui.devices.length;
  const on = ui.devices.filter((d) => d.state && d.state.on).length;
  $("#counts").textContent = total ? `${on}/${total} on` : "no devices";
}

function setConnection(kind) {
  const dot = $("#dot");
  dot.className = "dot" + (kind === "live" ? "" : kind === "stale" ? " dot--stale" : " dot--down");
  dot.title = kind === "live" ? "Receiving live updates" : kind === "stale" ? "Waiting for updates" : "Disconnected";
}

/* --------------------------------------------------------------- data feeds */

function accept(payload) {
  ui.lastMessage = Date.now();
  setConnection("live");
  render(payload);
}

async function pollOnce() {
  try {
    const response = await fetch("/api/devices");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    accept(await response.json());
  } catch {
    setConnection("down");
  }
}

function start() {
  pollOnce();

  if (typeof EventSource === "undefined") {
    setInterval(pollOnce, POLL_MS);
    return;
  }

  let polling = null;
  const stream = new EventSource("/api/events");

  stream.onopen = () => setConnection("live");

  stream.onmessage = (event) => {
    if (polling) { clearInterval(polling); polling = null; }
    try {
      accept(JSON.parse(event.data));
    } catch { /* ignore a partial frame */ }
  };

  // EventSource reconnects on its own; polling covers the gap in between.
  stream.onerror = () => {
    setConnection("down");
    if (!polling) polling = setInterval(pollOnce, POLL_MS);
  };

  // The stream only speaks when something changed, so silence is not a fault:
  // the socket's own state is what says whether updates would reach us.
  setInterval(() => {
    if (stream.readyState === EventSource.OPEN) setConnection("live");
    else if (stream.readyState === EventSource.CONNECTING) setConnection("stale");
    else setConnection("down");
  }, 3000);

  // Timestamps in the footer go stale on their own.
  setInterval(() => {
    for (const device of ui.devices) {
      const refs = ui.cards.get(device.id);
      if (refs) updateCard(refs, device);
    }
  }, 30000);
}

/* ---------------------------------------------------------------- bootstrap */

function initTheme() {
  const stored = localStorage.getItem("novee2mqtt-theme");
  const dark = stored ? stored === "dark" : !window.matchMedia("(prefers-color-scheme: light)").matches;
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}

document.addEventListener("DOMContentLoaded", () => {
  initTheme();

  $("#search").addEventListener("input", (event) => {
    ui.filter = event.target.value;
    applyFilter();
  });

  $("#theme").addEventListener("click", () => {
    const dark = document.documentElement.dataset.theme !== "dark";
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    localStorage.setItem("novee2mqtt-theme", dark ? "dark" : "light");
  });

  $("#purge").addEventListener("click", async () => {
    if (await call("/api/purge-caches", { method: "POST" })) {
      toast("Caches purged; devices will be re-fetched.", "ok");
    }
  });

  start();
});
