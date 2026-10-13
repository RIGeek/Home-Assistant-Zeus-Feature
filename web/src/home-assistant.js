// SPDX-License-Identifier: GPL-2.0-or-later
// Home Assistant panel. Plain createElement (no JSX, no bundler dependencies), built the same
// way and with the same look as the RZ1ZR amplifier panel. `react` is provided by the Zeus host;
// styles are inlined by build.mjs.
import * as ReactNS from 'react';
// Works whether the host exposes React as named exports, a default export, or both.
const React = ReactNS.default ?? ReactNS;

const h = React.createElement;
const STYLES = /*__STYLES__*/"";
const ROOT = 'com-n1mjf-homeassistant';
const POLL_MS = 500;
const CONFIRM_MS = 3000;

function ensureStyles() {
  if (document.getElementById(`${ROOT}-styles`)) return;
  const el = document.createElement('style');
  el.id = `${ROOT}-styles`;
  el.textContent = STYLES;
  document.head.appendChild(el);
}

// callBackend resolves to a fetch Response (not parsed JSON); parse it and surface HTTP errors.
async function call(api, method, path, body) {
  const res = await api.callBackend(method, path, body);
  let data = null;
  try { data = await res.json(); } catch { /* empty or non-JSON body */ }
  return { ok: !!res.ok, status: res.status, data };
}

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
const pctOf = (v, min, max) => (v == null ? 0 : Math.max(0, Math.min(100, ((v - min) / (max - min)) * 100)));
const ZONE_WORD = { warn: 'Caution', bad: 'Danger' };

// Same table as the backend (Tiles.cs). The backend is the authority; this only fills the editor.
const TOGGLE_DOMAINS = ['switch', 'light', 'fan', 'input_boolean', 'automation', 'siren'];
const PRESS_DOMAINS = ['button', 'input_button', 'script', 'scene'];
const domainOf = (id) => id.slice(0, id.indexOf('.'));
const kindsFor = (domain) => (TOGGLE_DOMAINS.includes(domain) ? ['toggle', 'sensor']
  : PRESS_DOMAINS.includes(domain) ? ['button', 'sensor'] : ['sensor']);
const KIND_NAME = { toggle: 'Switch', button: 'Button', sensor: 'Readout' };

// Binary sensors read better in the words Home Assistant itself uses for their device class.
const BINARY_WORDS = {
  door: ['Open', 'Closed'], window: ['Open', 'Closed'], opening: ['Open', 'Closed'], garage_door: ['Open', 'Closed'],
  motion: ['Detected', 'Clear'], occupancy: ['Detected', 'Clear'], presence: ['Home', 'Away'],
  moisture: ['Wet', 'Dry'], smoke: ['Detected', 'Clear'], gas: ['Detected', 'Clear'], problem: ['Problem', 'OK'],
  connectivity: ['Connected', 'Disconnected'], power: ['On', 'Off'], plug: ['Plugged in', 'Unplugged'],
  battery: ['Low', 'Normal'], lock: ['Unlocked', 'Locked'], running: ['Running', 'Not running'],
};

const tileName = (t) => t.label || t.name || t.entityId;

function zoneOf(value, m) {
  if (value == null || !m || !isNum(m.warn) || !isNum(m.bad)) return null;
  if (m.lowIsBad) return value <= m.bad ? 'bad' : value <= m.warn ? 'warn' : 'ok';
  return value >= m.bad ? 'bad' : value >= m.warn ? 'warn' : 'ok';
}

// Decimals: the tile's choice, else as many as Home Assistant sent (at most 2).
function fmtValue(t) {
  if (!isNum(t.value)) return null;
  let d = t.digits;
  if (d == null) {
    const dot = String(t.state ?? '').indexOf('.');
    d = dot < 0 ? 0 : Math.min(2, String(t.state).length - dot - 1);
  }
  return t.value.toFixed(d);
}

// Text for a readout: number, binary-sensor word, timestamp, or the state itself.
function readoutText(t) {
  if (!t.available) return t.missing ? 'Missing' : '—';
  if (t.state === 'unknown') return '—';
  const n = fmtValue(t);
  if (n != null) return n;
  if (t.domain === 'binary_sensor') {
    const words = BINARY_WORDS[t.deviceClass] || ['On', 'Off'];
    return t.state === 'on' ? words[0] : words[1];
  }
  if (t.deviceClass === 'timestamp') {
    const ms = Date.parse(t.state);
    if (Number.isFinite(ms)) return new Date(ms).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' });
  }
  const s = String(t.state ?? '').replace(/_/g, ' ');
  return s.length > 28 ? `${s.slice(0, 27)}…` : s.charAt(0).toUpperCase() + s.slice(1);
}

const changedTitle = (t) => {
  const ms = Date.parse(t.lastChanged);
  return Number.isFinite(ms) ? `${t.entityId} · changed ${new Date(ms).toLocaleString()}` : t.entityId;
};

// One meter row: label + value on top, an LED-style bar below. Identical in build to the amplifier's meters.
function Meter({ t }) {
  const m = t.meter;
  const value = t.available && isNum(t.value) ? t.value : null;
  const live = value != null;
  const zones = isNum(m.warn) && isNum(m.bad) ? m : null;
  const z = live ? zoneOf(value, zones) : null;
  const word = ZONE_WORD[z];
  const banded = !!zones && live;
  const style = banded ? { '--wp': `${pctOf(m.warn, m.min, m.max)}%`, '--bp': `${pctOf(m.bad, m.min, m.max)}%` } : undefined;
  const pct = pctOf(value, m.min, m.max);
  const badPct = banded ? pctOf(m.bad, m.min, m.max) : null;
  const label = tileName(t);
  const unit = t.unit || '';
  const text = live ? fmtValue(t) : readoutText(t);
  return h('div', { className: 'zha-meter', title: changedTitle(t) },
    h('div', { className: 'zha-meter-head' },
      h('span', { className: 'zha-label' }, label),
      word ? h('span', { className: `zha-zone ${z}` }, word) : null,
      h('span', { className: 'val' }, text, live && unit ? h('span', { className: 'unit' }, unit) : null)),
    h('div', {
      className: `zha-bar${live ? '' : ' stale'}${banded ? (m.lowIsBad ? ' banded-low' : ' banded') : ''}`, style, role: 'meter', 'aria-label': label,
      'aria-valuemin': m.min, 'aria-valuemax': m.max, 'aria-valuenow': live ? value : m.min,
      'aria-valuetext': live ? `${text} ${unit}${word ? `, ${word.toLowerCase()}` : ''}` : 'no reading',
    },
      h('i', { style: { clipPath: `inset(0 ${100 - pct}% 0 0)` } }),
      badPct != null && badPct > 0 && badPct < 100 ? h('em', { className: 'zha-notch', style: { left: `${badPct}%` } }) : null,
      pct > 0 ? h('s', { className: 'zha-tip', style: { left: `${pct}%` } }) : null));
}

// A boxed readout, the same box the amplifier uses for Band / Output / Antenna.
function Readout({ t }) {
  const text = readoutText(t);
  const unit = t.available && isNum(t.value) && t.unit ? t.unit : null;
  const z = t.meter && t.available ? zoneOf(t.value, t.meter) : null;
  const binary = t.available && (t.domain === 'binary_sensor' || ['on', 'off'].includes(t.state));
  const cls = !t.available ? ' na' : z ? ` z-${z}` : binary ? (t.state === 'on' ? ' on' : ' off') : '';
  return h('div', { className: `zha-read${cls}`, title: changedTitle(t) },
    h('span', { className: 'zha-label k' }, tileName(t)),
    h('span', { className: 'v' }, text, unit ? h('span', { className: 'unit' }, unit) : null));
}

function Lock() {
  return h('svg', {
    width: 11, height: 11, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2.6,
    strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': 'true', focusable: 'false', className: 'zha-lock',
  }, h('rect', { x: 5, y: 11, width: 14, height: 10, rx: 2 }), h('path', { d: 'M8 11V8a4 4 0 0 1 8 0v3' }));
}

function Gear() {
  const spokes = Array.from({ length: 8 }, (_, i) => {
    const a = (i * Math.PI) / 4;
    return h('line', {
      key: i, x1: 12 + 7.5 * Math.cos(a), y1: 12 + 7.5 * Math.sin(a),
      x2: 12 + 10 * Math.cos(a), y2: 12 + 10 * Math.sin(a),
    });
  });
  return h('svg', {
    width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2.4, strokeLinecap: 'round', 'aria-hidden': 'true', focusable: 'false',
  }, h('circle', { cx: 12, cy: 12, r: 6 }), h('circle', { cx: 12, cy: 12, r: 2 }), spokes);
}

function Icon({ d }) {
  return h('svg', {
    width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2.4,
    strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': 'true', focusable: 'false',
  }, h('path', { d }));
}
const ICONS = { up: 'M6 15l6-6 6 6', down: 'M6 9l6 6 6-6', edit: 'M4 20h4L19 9l-4-4L4 16v4z', remove: 'M6 6l12 12M18 6 6 18' };

// ------------------------------------------------------------------ tile editor (settings)

// Draft tiles hold strings for number boxes so half-typed values survive re-renders.
const str = (v) => (v == null ? '' : String(v));
function toDraftTile(t) {
  return {
    id: t.id, entityId: t.entityId, kind: t.kind, label: t.label ?? '', group: t.group ?? '',
    lockTx: t.lockTx ?? true, confirm: !!t.confirm, digits: t.digits == null ? '' : String(t.digits),
    name: t.name, unit: t.unit,
    bar: !!t.meter,
    meter: t.meter
      ? { min: str(t.meter.min), max: str(t.meter.max), warn: str(t.meter.warn), bad: str(t.meter.bad), lowIsBad: !!t.meter.lowIsBad }
      : { min: '0', max: '100', warn: '', bad: '', lowIsBad: false },
  };
}
const num = (s) => (s === '' || s == null ? null : Number(s));

// Returns [payload, error].
function fromDraftTile(d) {
  const out = {
    id: d.id, entityId: d.entityId, kind: d.kind, label: d.label.trim() || null, group: d.group.trim() || null,
    lockTx: d.lockTx, confirm: d.confirm, digits: d.kind === 'sensor' && d.digits !== '' ? Number(d.digits) : null, meter: null,
  };
  if (d.kind === 'sensor' && d.bar) {
    const m = { min: num(d.meter.min), max: num(d.meter.max), warn: num(d.meter.warn), bad: num(d.meter.bad), lowIsBad: d.meter.lowIsBad };
    if (!isNum(m.min) || !isNum(m.max)) return [null, 'Enter the bar minimum and maximum'];
    if (!(m.min < m.max)) return [null, 'Minimum must be below maximum'];
    if ((m.warn == null) !== (m.bad == null)) return [null, 'Fill both colour thresholds, or neither'];
    if (m.warn != null && (!isNum(m.warn) || !isNum(m.bad))) return [null, 'Thresholds must be numbers'];
    if (m.warn != null && (m.lowIsBad ? m.warn < m.bad : m.warn > m.bad)) {
      return [null, m.lowIsBad ? 'With “low is bad”, yellow must not be below red' : 'Yellow must not be above red'];
    }
    out.meter = m;
  }
  return [out, null];
}

function TileEditor({ d, onChange }) {
  const kinds = kindsFor(domainOf(d.entityId));
  const set = (patch) => onChange({ ...d, ...patch });
  const setM = (patch) => onChange({ ...d, meter: { ...d.meter, ...patch } });
  const control = d.kind !== 'sensor';
  const text = (label, value, onInput, opts = {}) => h('label', { className: 'zha-num' },
    h('span', { className: 'zha-label' }, label),
    h('input', { type: opts.type || 'text', inputMode: opts.inputMode, step: 'any', value, placeholder: opts.placeholder, maxLength: opts.max, onChange: (e) => onInput(e.target.value) }));
  const [, err] = fromDraftTile(d);
  return h('div', { className: 'zha-editor' },
    h('div', { className: 'zha-fields' },
      text('Label', d.label, (v) => set({ label: v }), { placeholder: d.name || d.entityId, max: 40 }),
      control ? text('Group', d.group, (v) => set({ group: v }), { placeholder: d.kind === 'toggle' ? 'Switches' : 'Buttons', max: 24 }) : null,
      kinds.length > 1
        ? h('label', { className: 'zha-num' },
          h('span', { className: 'zha-label' }, 'Show as'),
          h('select', { value: d.kind, onChange: (e) => set({ kind: e.target.value }) },
            kinds.map((k) => h('option', { key: k, value: k }, KIND_NAME[k]))))
        : null,
      !control
        ? h('label', { className: 'zha-num' },
          h('span', { className: 'zha-label' }, 'Decimals'),
          h('select', { value: d.digits, onChange: (e) => set({ digits: e.target.value }) },
            h('option', { value: '' }, 'As sent'), ['0', '1', '2', '3'].map((n) => h('option', { key: n, value: n }, n))))
        : null),
    control ? h('label', { className: 'zha-check' },
      h('input', { type: 'checkbox', checked: d.lockTx, onChange: (e) => set({ lockTx: e.target.checked }) }),
      'Lock while transmitting') : null,
    control ? h('label', { className: 'zha-check' },
      h('input', { type: 'checkbox', checked: d.confirm, onChange: (e) => set({ confirm: e.target.checked }) }),
      'Ask before switching (press twice)') : null,
    !control ? h('label', { className: 'zha-check' },
      h('input', { type: 'checkbox', checked: d.bar, onChange: (e) => set({ bar: e.target.checked }) }),
      'Show as a bar meter') : null,
    !control && d.bar ? h('div', { className: 'zha-range-fields' },
      text('Min', d.meter.min, (v) => setM({ min: v }), { type: 'number', inputMode: 'decimal' }),
      text('Max', d.meter.max, (v) => setM({ max: v }), { type: 'number', inputMode: 'decimal' }),
      text('Yellow from', d.meter.warn, (v) => setM({ warn: v }), { type: 'number', inputMode: 'decimal' }),
      text('Red from', d.meter.bad, (v) => setM({ bad: v }), { type: 'number', inputMode: 'decimal' })) : null,
    !control && d.bar ? h('label', { className: 'zha-check' },
      h('input', { type: 'checkbox', checked: d.meter.lowIsBad, onChange: (e) => setM({ lowIsBad: e.target.checked }) }),
      'Low values are bad (battery, signal)') : null,
    !control && d.bar ? h(Strip, { m: d.meter }) : null,
    err ? h('p', { className: 'zha-msg err', role: 'alert' }, err) : null);
}

// Preview of the colour zones while editing, as in the amplifier's range settings.
function Strip({ m }) {
  const lo = num(m.min), hi = num(m.max), w = num(m.warn), b = num(m.bad);
  if (![lo, hi, w, b].every(isNum) || !(hi > lo)) return null;
  const p = (v) => pctOf(v, lo, hi);
  const bg = m.lowIsBad
    ? `linear-gradient(90deg, var(--tx) 0 ${p(b)}%, var(--amber) ${p(b)}% ${p(w)}%, var(--ok) ${p(w)}% 100%)`
    : `linear-gradient(90deg, var(--ok) 0 ${p(w)}%, var(--amber) ${p(w)}% ${p(b)}%, var(--tx) ${p(b)}% 100%)`;
  return h('div', { className: 'zha-strip', 'aria-hidden': 'true', style: { background: bg } });
}

function EntityPicker({ api, connected, taken, onPick }) {
  const [q, setQ] = React.useState('');
  const [res, setRes] = React.useState(null);
  React.useEffect(() => {
    if (!connected) return undefined;
    let stop = false;
    const timer = setTimeout(async () => {
      try {
        const r = await call(api, 'GET', `/entities?q=${encodeURIComponent(q)}&limit=40`);
        if (!stop && r.ok) setRes(r.data);
      } catch { /* shown as no results */ }
    }, 200);
    return () => { stop = true; clearTimeout(timer); };
  }, [api, q, connected]);
  if (!connected) return h('p', { className: 'zha-note' }, 'Connect to Home Assistant to add tiles.');
  const list = res?.entities ?? [];
  return h('div', { className: 'zha-group' },
    h('label', { className: 'zha-label', htmlFor: `${ROOT}-find` }, 'Add a tile'),
    h('input', {
      id: `${ROOT}-find`, type: 'search', value: q, placeholder: 'Search by name or entity id', autoComplete: 'off', spellCheck: false,
      onChange: (e) => setQ(e.target.value),
    }),
    res ? h('div', { className: 'zha-results', role: 'list' },
      list.length === 0 ? h('p', { className: 'zha-empty', style: { padding: '6px 8px' } }, 'No matching entities') : null,
      list.map((e) => {
        const added = taken.has(e.entityId);
        return h('button', {
          key: e.entityId, type: 'button', className: 'zha-result', role: 'listitem', disabled: added,
          title: added ? 'Already a tile' : `Add ${e.entityId}`, onClick: () => onPick(e),
        },
          h('span', { className: 'zha-kind' }, KIND_NAME[e.kinds[0]]),
          h('span', { className: 'zha-item-text' },
            h('span', { className: 'zha-item-name' }, e.name || e.entityId),
            h('span', { className: 'zha-item-id' }, e.entityId)),
          h('span', { className: 'zha-result-state' }, added ? 'added' : `${e.state}${e.unit ? ` ${e.unit}` : ''}`));
      })) : null,
    res && res.total > list.length ? h('p', { className: 'zha-note' }, `Showing ${list.length} of ${res.total}. Type more to narrow it down.`) : null);
}

// ------------------------------------------------------------------ panel

function Panel({ api }) {
  const [status, setStatus] = React.useState(null);
  const [loadError, setLoadError] = React.useState(null);
  const [msg, setMsg] = React.useState({ text: '', err: false });
  const [view, setView] = React.useState('main');            // 'main' | 'settings'
  const [armed, setArmed] = React.useState(null);            // { id, until } for "press twice" tiles
  const [busy, setBusy] = React.useState({});                // tile id -> true while a command is in flight
  const [flash, setFlash] = React.useState(null);            // button tile that just fired
  // settings
  const [conn, setConn] = React.useState({ url: '', token: '', insecure: false, tokenSet: false, loaded: false });
  const [draft, setDraft] = React.useState(null);            // unsaved tile list, or null when in sync
  const [editing, setEditing] = React.useState(null);        // tile id open in the editor
  const [tileMsg, setTileMsg] = React.useState({ text: '', err: false });
  const gearRef = React.useRef(null);
  const urlRef = React.useRef(null);
  const wasSettings = React.useRef(false);

  React.useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const r = await call(api, 'GET', '/status');
        if (stop) return;
        if (!r.ok || !r.data?.connection) throw new Error(r.data?.message || `HTTP ${r.status}`);
        setStatus(r.data);
        setLoadError(null);
      } catch (e) {
        if (!stop) setLoadError(e?.message || 'Cannot reach the Home Assistant feature');
      }
      if (!stop) timer = setTimeout(tick, POLL_MS);
    };
    tick();
    return () => { stop = true; clearTimeout(timer); };
  }, [api]);

  // Keyboard focus: into the address box when settings open, back to the gear when they close.
  React.useEffect(() => {
    if (view === 'settings') urlRef.current?.focus();
    else if (wasSettings.current) gearRef.current?.focus();
    wasSettings.current = view === 'settings';
  }, [view]);

  // Success messages fade after a few seconds; errors stay until the next action.
  React.useEffect(() => {
    if (!msg.text || msg.err) return undefined;
    const t = setTimeout(() => setMsg({ text: '', err: false }), 4000);
    return () => clearTimeout(t);
  }, [msg]);

  // An armed "press twice" tile disarms itself.
  React.useEffect(() => {
    if (!armed) return undefined;
    const t = setTimeout(() => setArmed(null), Math.max(0, armed.until - Date.now()));
    return () => clearTimeout(t);
  }, [armed]);

  const openSettings = async () => {
    setView('settings');
    try {
      const r = await call(api, 'GET', '/settings');
      if (r.ok) setConn({ url: r.data.url || '', token: '', insecure: !!r.data.acceptAnyCertificate, tokenSet: !!r.data.tokenSet, loaded: true });
    } catch { /* the form still works */ }
  };

  const post = async (path, body, setter = setMsg) => {
    try {
      const r = await call(api, 'POST', path, body);
      const ok = r.ok && r.data?.ok !== false;
      setter({ text: r.data?.message || (ok ? 'Done' : `Request failed (HTTP ${r.status})`), err: !ok });
      return { ok, data: r.data };
    } catch (e) {
      setter({ text: e?.message || 'Request failed', err: true });
      return { ok: false, data: null };
    }
  };

  const operate = async (t, action) => {
    if (t.confirm && !(armed && armed.id === t.id && armed.until > Date.now())) {
      setArmed({ id: t.id, until: Date.now() + CONFIRM_MS });
      setMsg({ text: `Press ${tileName(t)} again to confirm`, err: false });
      return;
    }
    setArmed(null);
    setBusy((b) => ({ ...b, [t.id]: true }));
    const r = await post('/command', { tile: t.id, action });
    setBusy((b) => ({ ...b, [t.id]: false }));
    if (r.ok && action === 'press') {
      setFlash(t.id);
      setTimeout(() => setFlash((f) => (f === t.id ? null : f)), 700);
    }
  };

  if (!status) {
    return h('div', { className: ROOT },
      h('div', { className: 'zha-card' },
        h('h2', { className: 'zha-title' }, 'Home Assistant'),
        h('p', { className: 'zha-empty', role: 'status' }, loadError ? `Unavailable: ${loadError}` : 'Loading…')));
  }

  const { connection: c, radio, tiles } = status;
  const connected = c.state === 'connected';
  const tx = radio.tx === true;
  const PILL = {
    unconfigured: ['Not configured', ''], no_token: ['Needs token', 'warn'], connecting: ['Connecting…', 'warn'],
    disconnected: ['Disconnected', 'warn'], auth_failed: ['Sign-in failed', 'tx'],
  };
  const pill = connected ? (tx ? ['Transmitting', 'tx'] : ['Connected', 'ok']) : PILL[c.state] || [c.state, 'warn'];

  // ------------------------------------------------------------------ settings view
  if (view === 'settings') {
    const current = tiles.map(toDraftTile);
    const list = draft ?? current;
    const errors = list.map((d) => fromDraftTile(d)[1]);
    const taken = new Set(list.map((d) => d.entityId));
    const change = (next) => { setDraft(next); setTileMsg({ text: '', err: false }); };
    const move = (i, by) => {
      const next = [...list];
      const [x] = next.splice(i, 1);
      next.splice(i + by, 0, x);
      change(next);
    };
    const saveTiles = async () => {
      const payload = list.map((d) => fromDraftTile(d)[0]);
      const r = await post('/tiles', { tiles: payload }, setTileMsg);
      if (r.ok) { setDraft(null); setEditing(null); }
    };
    const connStatus = connected
      ? `Connected to Home Assistant ${c.haVersion || ''} · ${c.entities} entities`
      : c.error || (c.state === 'no_token' ? 'Add an access token to connect.' : c.state === 'connecting' ? 'Connecting…' : '');

    return h('div', { className: ROOT },
      h('section', { className: 'zha-card', 'aria-label': 'Home Assistant settings' },
        h('div', { className: 'zha-head' },
          h('h2', { className: 'zha-title' }, 'Settings'),
          h('button', {
            type: 'button',
            onClick: () => { setDraft(null); setEditing(null); setTileMsg({ text: '', err: false }); setView('main'); },
          }, 'Done')),

        h('form', {
          className: 'zha-group',
          onSubmit: async (e) => {
            e.preventDefault();
            const r = await post('/settings', { url: conn.url.trim(), token: conn.token.trim() || null, acceptAnyCertificate: conn.insecure });
            if (r.ok) setConn((s) => ({ ...s, token: '', tokenSet: s.tokenSet || !!conn.token.trim() }));
          },
        },
          h('label', { className: 'zha-label', htmlFor: `${ROOT}-url` }, 'Home Assistant address'),
          h('input', {
            id: `${ROOT}-url`, ref: urlRef, type: 'text', value: conn.url, placeholder: 'http://homeassistant.local:8123',
            autoComplete: 'off', spellCheck: false, onChange: (e) => setConn((s) => ({ ...s, url: e.target.value })),
          }),
          h('label', { className: 'zha-label', htmlFor: `${ROOT}-token` }, 'Long-lived access token'),
          h('input', {
            id: `${ROOT}-token`, type: 'password', value: conn.token, autoComplete: 'off', spellCheck: false,
            placeholder: conn.tokenSet ? 'Saved (leave blank to keep it)' : 'Paste a token',
            onChange: (e) => setConn((s) => ({ ...s, token: e.target.value })),
          }),
          h('label', { className: 'zha-check' },
            h('input', { type: 'checkbox', checked: conn.insecure, onChange: (e) => setConn((s) => ({ ...s, insecure: e.target.checked })) }),
            'Accept a self-signed certificate (https only)'),
          h('div', { className: 'zha-row' },
            h('button', { type: 'submit' }, 'Connect'),
            conn.tokenSet ? h('button', {
              type: 'button',
              onClick: async () => { const r = await post('/settings', { clearToken: true }); if (r.ok) setConn((s) => ({ ...s, tokenSet: false })); },
            }, 'Forget token') : null),
          connStatus ? h('p', { className: `zha-msg${connected ? '' : ' err'}`, role: 'status' }, connStatus) : null,
          h('p', { className: 'zha-note' }, 'Create the token in Home Assistant under your profile, Security tab, Long-lived access tokens. Zeus keeps it and never sends it back to the browser. A rejected token is not retried until you press Connect again.')),

        radio.available ? null
          : h('p', { className: 'zha-note' }, 'Radio state is not available in this Zeus, so tiles cannot lock while transmitting.'),

        h('div', { className: 'zha-group' },
          h('h2', { className: 'zha-label' }, 'Tiles'),
          list.length === 0 ? h('p', { className: 'zha-note' }, 'No tiles yet. Search below to add switches, buttons and sensors.') : null,
          list.length ? h('div', { className: 'zha-list' },
            list.map((d, i) => {
              const live = tiles.find((t) => t.id === d.id);
              const open = editing === (d.id || d.entityId);
              return h('div', { key: d.id || d.entityId, className: 'zha-item' },
                h('div', { className: 'zha-item-row' },
                  h('span', { className: 'zha-kind' }, KIND_NAME[d.kind]),
                  h('span', { className: 'zha-item-text' },
                    h('span', { className: 'zha-item-name' }, d.label || d.name || live?.name || d.entityId),
                    h('span', { className: 'zha-item-id' }, d.entityId)),
                  h('button', { type: 'button', className: 'zha-icon', 'aria-label': `Move ${d.entityId} up`, title: 'Move up', disabled: i === 0, onClick: () => move(i, -1) }, h(Icon, { d: ICONS.up })),
                  h('button', { type: 'button', className: 'zha-icon', 'aria-label': `Move ${d.entityId} down`, title: 'Move down', disabled: i === list.length - 1, onClick: () => move(i, 1) }, h(Icon, { d: ICONS.down })),
                  h('button', {
                    type: 'button', className: 'zha-icon', 'aria-label': `Edit ${d.entityId}`, title: 'Edit', 'aria-expanded': open,
                    onClick: () => setEditing(open ? null : (d.id || d.entityId)),
                  }, h(Icon, { d: ICONS.edit })),
                  h('button', {
                    type: 'button', className: 'zha-icon', 'aria-label': `Remove ${d.entityId}`, title: 'Remove',
                    onClick: () => change(list.filter((_, j) => j !== i)),
                  }, h(Icon, { d: ICONS.remove }))),
                open ? h(TileEditor, { d, onChange: (nd) => change(list.map((x, j) => (j === i ? nd : x))) }) : null,
                !open && errors[i] ? h('p', { className: 'zha-msg err' }, errors[i]) : null);
            })) : null,
          h('div', { className: 'zha-row' },
            h('button', { type: 'button', disabled: !draft || errors.some(Boolean), onClick: saveTiles }, 'Save tiles'),
            draft ? h('button', { type: 'button', onClick: () => { setDraft(null); setEditing(null); setTileMsg({ text: '', err: false }); } }, 'Discard') : null),
          h('div', { className: `zha-msg${tileMsg.err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' },
            tileMsg.text || (draft ? 'Unsaved changes' : '')),
          h(EntityPicker, {
            api, connected, taken,
            onPick: (e) => {
              const kind = e.kinds[0];
              const d = toDraftTile({ entityId: e.entityId, kind, name: e.name, unit: e.unit, lockTx: true });
              change([...list, d]);
              setEditing(e.entityId);
            },
          })),
        h('div', { className: `zha-msg${msg.err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' }, msg.text)));
  }

  // ------------------------------------------------------------------ main view
  const meters = tiles.filter((t) => t.kind === 'sensor' && t.meter);
  const readouts = tiles.filter((t) => t.kind === 'sensor' && !t.meter);
  const controls = tiles.filter((t) => t.kind !== 'sensor');
  const groups = [];
  for (const t of controls) {
    const name = t.group || (t.kind === 'toggle' ? 'Switches' : 'Buttons');
    let g = groups.find((x) => x.name === name);
    if (!g) { g = { name, tiles: [] }; groups.push(g); }
    g.tiles.push(t);
  }
  const anyLocked = controls.some((t) => t.locked);

  const tileButton = (t) => {
    const isArmed = armed && armed.id === t.id;
    const why = !connected ? 'Not connected to Home Assistant'
      : t.missing ? `${t.entityId} no longer exists in Home Assistant`
        : !t.available ? `${tileName(t)} is unavailable in Home Assistant`
          : t.locked ? 'Locked while transmitting' : undefined;
    const disabled = !!why || !!busy[t.id];
    const pressed = t.kind === 'toggle' ? !!t.on : undefined;
    const cls = [isArmed ? 'zha-arm' : '', flash === t.id ? 'zha-flash' : '', connected && !t.available ? 'zha-na' : ''].filter(Boolean).join(' ');
    return h('button', {
      key: t.id, type: 'button', className: cls || undefined, 'aria-pressed': pressed, disabled,
      title: why || (t.confirm ? `${t.entityId} · press twice` : t.entityId),
      onClick: () => operate(t, t.kind === 'toggle' ? (t.on ? 'off' : 'on') : 'press'),
    }, t.lockTx && tx ? h(Lock) : null, isArmed ? `Confirm ${tileName(t)}?` : tileName(t));
  };

  const unconfigured = c.state === 'unconfigured' || c.state === 'no_token';

  return h('div', { className: ROOT },
    h('div', { className: 'zha-card' },
      h('div', { className: 'zha-head' },
        h('h2', { className: 'zha-title' }, 'Home Assistant'),
        h('div', { className: 'zha-head-end' },
          h('span', { className: `zha-pill ${pill[1]}`, role: 'status' }, pill[0]),
          h('button', {
            type: 'button', className: 'zha-icon', ref: gearRef, 'aria-label': 'Settings', title: 'Settings', onClick: openSettings,
          }, h(Gear)))),
      c.error && !connected ? h('p', { className: 'zha-msg err' }, c.error) : null,
      unconfigured
        ? h('p', { className: 'zha-empty' }, 'Not connected to Home Assistant yet. Open settings (the gear) to add its address and an access token.')
        : tiles.length === 0
          ? h('p', { className: 'zha-empty' }, 'No tiles yet. Open settings (the gear) to add switches, buttons and sensor readouts.')
          : null,
      meters.length ? h('div', { className: 'zha-meters' }, meters.map((t) => h(Meter, { key: t.id, t }))) : null,
      readouts.length ? h('div', { className: 'zha-readouts auto' }, readouts.map((t) => h(Readout, { key: t.id, t }))) : null,
      msg.text ? h('div', { className: `zha-msg${msg.err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' }, msg.text) : null),

    // Controls sit in a plain card (no collapse): group names are the only headings needed.
    groups.length ? h('section', { className: 'zha-card', 'aria-label': 'Controls' },
      groups.map((g) => h('div', { key: g.name, className: 'zha-group' },
        h('h2', { className: 'zha-label' }, g.name),
        h('div', { className: 'zha-row', role: 'group', 'aria-label': g.name }, g.tiles.map(tileButton)))),
      tx && anyLocked ? h('p', { className: 'zha-note' }, 'Tiles marked with a lock are locked while transmitting.') : null,
      radio.available ? null : h('p', { className: 'zha-note' }, 'Radio state is not available, so nothing locks during transmit.')) : null);
}

export default function register(api) {
  ensureStyles();
  api.registerPanel({
    id: 'home-assistant',
    component: () => h(Panel, { api }),
  });
}
