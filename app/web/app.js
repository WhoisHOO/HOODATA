// HOODATA dashboard. Plain JavaScript, no outside code. Every text a user or a data file supplied (channel names,
// CSV headers, file names) goes into the page with textContent, never as HTML.
'use strict';

(function () {
  const HD = window.HD;
  const S = {
    info: null, strings: null, lang: 'en', channels: new Map(), sources: [], jobs: [], seq: 0,
    connected: true, locked: false, page: null, pollTimer: null
  };
  const SPARK_MS = 60 * 60 * 1000;

  // ---- text ----

  function T(k) {
    const t = S.strings && S.strings[S.lang];
    return t && t[k] !== undefined ? t[k] : k;
  }

  function F(k, ...a) { return T(k).replace(/\{(\d+)\}/g, (m, i) => (a[i] !== undefined ? a[i] : m)); }

  const fmt = v => HD.fmt(v, S.lang);

  function fmtDur(ms) {
    if (ms === null || ms === undefined || !isFinite(ms)) return '–';
    const s = Math.round(ms / 1000);
    if (s < 60) return F('DurS', s);
    const m = Math.floor(s / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
    if (m < 60) return s % 60 ? F('DurM', m) + ' ' + F('DurS', s % 60) : F('DurM', m);
    if (h < 24) return m % 60 ? F('DurH', h) + ' ' + F('DurM', m % 60) : F('DurH', h);
    return h % 24 ? F('DurD', d) + ' ' + F('DurH', h % 24) : F('DurD', d);
  }

  function ago(ms) {
    const s = Math.max(0, Math.round((Date.now() - ms) / 1000));
    if (s < 5) return T('AgoNow');
    if (s < 60) return F('AgoS', s);
    if (s < 3600) return F('AgoM', Math.floor(s / 60));
    if (s < 86400) return F('AgoH', Math.floor(s / 3600));
    return F('AgoD', Math.floor(s / 86400));
  }

  const withUnit = (v, unit) => fmt(v) + (unit ? ' ' + unit : '');

  // ---- DOM ----

  function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else el.setAttribute(k, v === true ? '' : v);
    }
    for (const kid of kids.flat()) {
      if (kid === null || kid === undefined || kid === false) continue;
      el.appendChild(typeof kid === 'string' || typeof kid === 'number' ? document.createTextNode(String(kid)) : kid);
    }
    return el;
  }

  const SVG_NS = 'http://www.w3.org/2000/svg';
  const ICONS = {
    ok: [['circle', { cx: 8, cy: 8, r: 6.5, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6 }],
      ['path', { d: 'M5 8.2l2 2 4-4.4', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.8, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }]],
    warn: [['path', { d: 'M8 1.8l6.6 11.7H1.4z', fill: 'currentColor' }],
      ['path', { d: 'M8 6v3.6M8 11.4v.2', stroke: 'var(--surface)', 'stroke-width': 1.8, 'stroke-linecap': 'round' }]],
    alert: [['circle', { cx: 8, cy: 8, r: 7, fill: 'currentColor' }],
      ['path', { d: 'M5.5 5.5l5 5M10.5 5.5l-5 5', stroke: 'var(--surface)', 'stroke-width': 1.8, 'stroke-linecap': 'round' }]],
    learning: [['circle', { cx: 8, cy: 8, r: 6.5, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6, 'stroke-dasharray': '2.5 2' }]],
    idle: [['rect', { x: 2.5, y: 2.5, width: 11, height: 11, rx: 2, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6 }],
      ['path', { d: 'M5 6h6M5 8.5h6M5 11h4', stroke: 'currentColor', 'stroke-width': 1.4 }]]
  };

  function icon(name) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', '0 0 16 16');
    svg.setAttribute('aria-hidden', 'true');
    for (const [tag, attrs] of ICONS[name] || []) {
      const el = document.createElementNS(SVG_NS, tag);
      for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
      svg.appendChild(el);
    }
    return svg;
  }

  function statusChip(status) {
    return h('span', { class: 'status ' + status }, icon(status), T('Status_' + status));
  }

  function kindChip(kind) {
    const s = HD.isAlertKind(kind) ? 'alert' : 'warn';
    return h('span', { class: 'status ' + s }, icon(s), T('Kind_' + kind));
  }

  function seg(options, current, onPick, label) {
    const box = h('div', { class: 'seg', role: 'group', 'aria-label': label || null });
    for (const [value, text] of options) {
      box.appendChild(h('button', {
        type: 'button', 'aria-pressed': String(value === current),
        onclick: () => {
          for (const b of box.children) b.setAttribute('aria-pressed', 'false');
          box.querySelector('[data-v="' + value + '"]').setAttribute('aria-pressed', 'true');
          onPick(value);
        }, 'data-v': value
      }, text));
    }
    return box;
  }

  // ---- API ----

  async function api(path, opts) {
    opts = opts || {};
    const init = { method: opts.method || 'GET', headers: {}, credentials: 'same-origin' };
    if (init.method !== 'GET') init.headers['X-HOODATA'] = '1';
    if (opts.body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(opts.body);
    }
    const res = await fetch(path, init);
    if (res.status === 401) { lock(); throw new Error('locked'); }
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    return data;
  }

  function lock() {
    if (S.locked) return;
    S.locked = true;
    const app = document.getElementById('app');
    app.textContent = '';
    app.appendChild(h('div', { class: 'locked card' },
      h('h1', { text: T('LockedTitle') }), h('p', { class: 'muted', text: T('LockedText') })));
  }

  // ---- live state ----

  function mergeLive(data) {
    S.seq = data.seq;
    const seen = new Set();
    for (const c of data.channels) {
      seen.add(c.id);
      let ch = S.channels.get(c.id);
      if (!ch) { ch = { points: [] }; S.channels.set(c.id, ch); }
      const pts = ch.points;
      Object.assign(ch, c);
      ch.points = pts;
      for (const p of c.points) pts.push(p);
      const cut = Date.now() - SPARK_MS;
      let drop = 0;
      while (drop < pts.length - 2 && pts[drop][0] < cut && ch.live) drop++;
      if (drop) pts.splice(0, drop);
      if (pts.length > 1500) pts.splice(0, pts.length - 1500);
    }
    for (const id of [...S.channels.keys()]) if (!seen.has(id)) S.channels.delete(id);
    S.sources = data.sources;
    S.jobs = data.jobs;
  }

  async function poll() {
    try {
      const data = await api('/api/live?since=' + S.seq);
      mergeLive(data);
      setConnected(true);
      if (S.page && S.page.onLive) S.page.onLive(data);
    } catch (e) {
      if (e.message !== 'locked') setConnected(false);
    }
  }

  function setConnected(on) {
    S.connected = on;
    document.getElementById('conn').classList.toggle('off', !on);
    document.getElementById('conn-text').textContent = T(on ? 'Connected' : 'Disconnected');
  }

  const channelStatus = ch => (ch.live ? ch.status : (ch.open && ch.open.length ? ch.status : 'idle'));
  const sourceOf = ch => S.sources.find(s => s.id === ch.source);

  function sortedChannels() {
    return [...S.channels.values()].sort((a, b) => a.source - b.source || a.id - b.id);
  }

  // ---- anomalies explained ----

  function why(e, unit) {
    const d = e.detail || {};
    const u = v => withUnit(v, unit);
    switch (e.kind) {
      case 'gap': return F('Why_gap', fmtDur((e.t1 === null ? Date.now() : e.t1) - e.t0), fmtDur(d.interval));
      case 'spike': return F(d.count > 1 ? 'Why_spikeN' : 'Why_spike', u(e.value), u(e.expected), fmt(e.score), d.count);
      case 'step': return F('Why_step', u(d.before), u(d.after));
      case 'stuck': return F('Why_stuck', u(e.value), fmtDur((e.t1 === null ? Date.now() : e.t1) - e.t0), fmtDur(d.usual));
      case 'range': return F('Why_range', d.lo === null ? '–' : u(d.lo), d.hi === null ? '–' : u(d.hi), u(e.value));
      case 'noisy': return F('Why_noisy', fmt(e.score));
      case 'unusual': return F('Why_unusual', d.hour, u(d.lo), u(d.hi), u(e.value));
    }
    return '';
  }

  // ---- time ranges ----

  const RANGES = [['1h', 3600e3], ['6h', 6 * 3600e3], ['24h', 86400e3], ['7d', 7 * 86400e3], ['30d', 30 * 86400e3], ['all', 0]];

  function rangeButtons(current, onPick) {
    return seg(RANGES.map(([k]) => [k, T('Range_' + k)]), current, onPick, T('Period'));
  }

  // ---- pages ----

  const pages = {};

  // Overview: one tile per channel, grouped by where the data comes from.
  pages.overview = {
    render(root) {
      this.root = root;
      this.tiles = new Map();
      root.appendChild(h('h1', { text: T('OverviewTitle') }));
      root.appendChild(h('p', { class: 'lead', text: T('OverviewLead') }));
      this.body = h('div', { class: 'groups' });
      root.appendChild(this.body);
      this.build();
    },
    build() {
      this.body.textContent = '';
      for (const t of this.tiles.values()) t.plot.destroy();
      this.tiles.clear();
      const chans = sortedChannels().filter(c => !c.hidden);
      this.count = S.channels.size;
      if (!chans.length) {
        this.body.appendChild(h('div', { class: 'empty' },
          h('p', { text: T('EmptyText') }),
          h('a', { class: 'btn primary', href: '#/sources', text: T('EmptyAction') })));
        return;
      }
      for (const src of S.sources) {
        const list = chans.filter(c => c.source === src.id);
        if (!list.length) continue;
        const kind = T('Kind_src_' + src.kind);
        const title = h('h2', { class: 'group-title' }, src.name, kind !== src.name ? h('span', { class: 'muted small', text: kind }) : null);
        const grid = h('div', { class: 'cards' });
        for (const ch of list) grid.appendChild(this.tile(ch));
        this.body.appendChild(h('section', {}, title, grid));
      }
      const hidden = S.channels.size - chans.length;
      if (hidden > 0) this.body.appendChild(h('p', { class: 'muted small', text: F('HiddenChannels', hidden) }));
    },
    tile(ch) {
      const value = h('div', { class: 'value' });
      const status = h('span');
      const meta = h('div', { class: 'meta' });
      const spark = h('div', { class: 'spark' });
      const a = h('a', { class: 'tile', href: '#/ch/' + ch.id },
        h('div', { class: 'top' }, h('span', { class: 'label', text: ch.name, title: ch.name }), status), value, meta, spark);
      const plot = new HD.Plot(spark, {
        spark: true, height: 44, lang: S.lang,
        tooltip: (d, i) => [{ text: withUnit(d.v[i], ch.unit), cls: 'v' }, { text: HD.fmtTime(d.t[i]) }]
      });
      this.tiles.set(ch.id, { a, value, status, meta, plot });
      this.updateTile(ch);
      return a;
    },
    updateTile(ch) {
      const t = this.tiles.get(ch.id);
      if (!t) return;
      t.value.textContent = '';
      t.value.appendChild(document.createTextNode(ch.v === undefined ? '–' : fmt(ch.v)));
      if (ch.unit) t.value.appendChild(h('span', { class: 'unit', text: ch.unit }));
      t.status.textContent = '';
      t.status.appendChild(statusChip(channelStatus(ch)));
      t.meta.textContent = ch.t === undefined ? T('NoValuesYet') : (ch.live ? ago(ch.t) : HD.fmtTime(ch.t, false));
      const pts = ch.points;
      if (pts.length) {
        const from = pts[0][0];
        t.plot.setData({
          t: pts.map(p => p[0]), v: pts.map(p => p[1]), from, to: ch.live ? Math.max(Date.now(), pts[pts.length - 1][0]) : pts[pts.length - 1][0],
          gapMs: ch.interval > 0 ? ch.interval * 6 : Infinity
        });
      }
    },
    onLive() {
      const visible = sortedChannels().filter(c => !c.hidden);
      if (visible.length !== this.tiles.size || S.channels.size !== this.count || visible.some(c => !this.tiles.has(c.id))) { this.build(); return; }
      for (const ch of visible) this.updateTile(ch);
    },
    destroy() { for (const t of this.tiles.values()) t.plot.destroy(); }
  };

  // One channel: the graph for a period, its noise, statistics and anomalies, and its settings.
  pages.channel = {
    render(root, id) {
      this.root = root;
      this.id = Number(id);
      const ch = S.channels.get(this.id);
      if (!ch) { root.appendChild(h('p', { class: 'notice', text: T('NoChannel') })); return; }
      this.range = ch.live ? '1h' : 'all';
      this.zoom = null;
      this.head = h('div');
      root.appendChild(h('p', {}, h('a', { href: '#/', text: '← ' + T('BackToOverview') })));
      root.appendChild(this.head);
      this.updateHead();

      this.zoomReset = h('button', { class: 'btn hidden', type: 'button', text: T('ZoomReset'), onclick: () => { this.zoom = null; this.load(); } });
      this.tableBtn = h('button', { class: 'btn', type: 'button', text: T('ShowTable'), 'aria-expanded': 'false', onclick: () => this.toggleTable() });
      root.appendChild(h('div', { class: 'toolbar' }, rangeButtons(this.range, r => { this.range = r; this.zoom = null; this.load(); }),
        this.zoomReset, h('span', { class: 'grow' }), this.tableBtn));

      this.plotTitle = h('div', { class: 'plot-title' });
      const plotBox = h('div');
      this.legend = h('div', { class: 'legend' });
      root.appendChild(h('div', { class: 'card' }, this.plotTitle, plotBox, this.legend));
      this.plot = new HD.Plot(plotBox, {
        height: 300, lang: S.lang, label: ch.name,
        onZoom: (a, b) => { this.zoom = [a, b]; this.load(); },
        tooltip: (d, i) => this.tip(d, i)
      });

      const noiseBox = h('div');
      root.appendChild(h('div', { class: 'card', style: null },
        h('div', { class: 'plot-title' }, h('strong', { text: T('NoiseTitle') }), h('span', { class: 'muted small', text: T('NoiseLead') })), noiseBox));
      this.noisePlot = new HD.Plot(noiseBox, {
        height: 130, lang: S.lang, zeroBased: true, label: T('NoiseTitle'),
        onZoom: (a, b) => { this.zoom = [a, b]; this.load(); },
        tooltip: (d, i) => [{ text: '± ' + withUnit(d.v[i], ch.unit), cls: 'v' }, { text: HD.fmtTime(d.t[i], false) }]
      });

      this.table = h('div', { class: 'table-wrap hidden' });
      root.appendChild(this.table);

      this.statsBox = h('div', { class: 'stats-grid' });
      this.eventsBox = h('div');
      root.appendChild(h('div', { class: 'two' },
        h('section', {}, h('h2', { text: T('EventsInPeriod') }), this.eventsBox),
        h('section', {}, h('h2', { text: T('StatsInPeriod') }), h('div', { class: 'card' }, this.statsBox))));

      root.appendChild(this.settingsForm(ch));
      this.load();
      this.timer = setInterval(() => { if (!this.zoom && this.range !== 'all' && S.channels.get(this.id) && S.channels.get(this.id).live) this.load(true); }, 10000);
    },
    destroy() {
      clearInterval(this.timer);
      if (this.plot) this.plot.destroy();
      if (this.noisePlot) this.noisePlot.destroy();
    },
    updateHead() {
      const ch = S.channels.get(this.id);
      if (!ch) return;
      const src = sourceOf(ch);
      this.head.textContent = '';
      const big = h('span', { class: 'big' }, ch.v === undefined ? '–' : fmt(ch.v));
      if (ch.unit) big.appendChild(h('span', { class: 'unit muted', text: ' ' + ch.unit }));
      const meta = [src ? src.name : null];
      if (ch.t !== undefined) meta.push(ch.live ? F('LastValue', ago(ch.t)) : F('LastValueAt', HD.fmtTime(ch.t)));
      if (ch.interval > 0) meta.push(F('UsualInterval', fmtDur(ch.interval)));
      if (ch.noise > 0) meta.push(F('NoiseLevel', '± ' + withUnit(ch.noise, ch.unit)));
      this.head.appendChild(h('div', { class: 'detail-head' }, h('h1', { text: ch.name }), statusChip(channelStatus(ch)), big));
      this.head.appendChild(h('p', { class: 'lead', text: meta.filter(Boolean).join(' · ') }));
    },
    async period() {
      if (this.zoom) return this.zoom;
      const now = Date.now();
      if (this.range !== 'all') return [now - RANGES.find(r => r[0] === this.range)[1], now];
      const ext = await api('/api/extent?ch=' + this.id);
      if (ext.first === null) return [now - 3600e3, now];
      return [ext.first, Math.max(ext.last, ext.first + 60000)];
    },
    async load(quiet) {
      const ch = S.channels.get(this.id);
      if (!ch) return;
      const [from, to] = await this.period();
      this.from = from;
      this.to = to;
      this.zoomReset.classList.toggle('hidden', !this.zoom);
      const n = Math.max(200, Math.min(3000, Math.round(this.plot.el.clientWidth)));
      if (!quiet) this.plot.el.style.opacity = '0.6';
      const [series, noise] = await Promise.all([
        api('/api/series?ch=' + this.id + '&from=' + from + '&to=' + to + '&n=' + n),
        api('/api/series?ch=' + this.id + '&from=' + from + '&to=' + to + '&n=' + Math.round(n / 2) + '&field=noise')]);
      this.plot.el.style.opacity = '';
      this.series = series;
      const limits = [];
      if (ch.lo !== null) limits.push({ value: ch.lo });
      if (ch.hi !== null) limits.push({ value: ch.hi });
      const gapMs = this.gapFor(series, ch);
      this.plot.setData({ t: series.t, v: series.v, lo: series.lo, hi: series.hi, from, to, events: series.events, limits, gapMs });
      this.noisePlot.setData({ t: noise.t, v: noise.v, from, to, gapMs: this.gapFor(noise, ch) });
      this.plotTitle.textContent = '';
      this.plotTitle.appendChild(h('strong', { text: series.bucket ? F('AveragedEvery', fmtDur(series.bucket)) : T('RawValues') }));
      this.plotTitle.appendChild(h('span', { class: 'muted small', text: HD.fmtTime(from, false) + ' – ' + HD.fmtTime(to, false) }));
      this.legend.textContent = '';
      this.legend.appendChild(h('span', {}, h('i'), series.bucket ? T('LegendMean') : T('LegendValue')));
      if (series.bucket) this.legend.appendChild(h('span', {}, h('i', { class: 'band' }), T('LegendBand')));
      this.legend.appendChild(h('span', {}, h('i', { class: 'ev-warn' }), T('LegendWarn')));
      this.legend.appendChild(h('span', {}, h('i', { class: 'ev-alert' }), T('LegendAlert')));
      if (limits.length) this.legend.appendChild(h('span', {}, h('i', { class: 'limit' }), T('LegendLimit')));
      if (!this.table.classList.contains('hidden')) this.fillTable();
      this.fillEvents(series.events, ch);
      this.fillStats(from, to, ch);
    },
    gapFor(s, ch) {
      if (s.t.length < 3) return Infinity;
      const d = [];
      for (let i = 1; i < s.t.length; i++) d.push(s.t[i] - s.t[i - 1]);
      d.sort((a, b) => a - b);
      return Math.max(d[d.length >> 1] * 4, (s.bucket || 0) * 2.5, ch.interval > 0 && !s.bucket ? ch.interval * 5 : 0);
    },
    tip(d, i) {
      const ch = S.channels.get(this.id);
      const unit = ch ? ch.unit : '';
      const parts = [{ text: withUnit(d.v[i], unit), cls: 'v' },
        { text: this.series.bucket ? HD.fmtTime(d.t[i], false) + ' + ' + fmtDur(this.series.bucket) : HD.fmtTime(d.t[i]) }];
      if (d.lo) parts.push({ text: F('MinMax', withUnit(d.lo[i], unit), withUnit(d.hi[i], unit)) });
      for (const e of this.plot.eventsAt(i).slice(0, 4))
        parts.push({ text: T('Kind_' + e.kind) + ' · ' + why(e, unit), cls: 'row', key: true, color: HD.token(HD.isAlertKind(e.kind) ? '--alert' : '--warn') });
      return parts;
    },
    toggleTable() {
      const show = this.table.classList.contains('hidden');
      this.table.classList.toggle('hidden', !show);
      this.tableBtn.textContent = T(show ? 'HideTable' : 'ShowTable');
      this.tableBtn.setAttribute('aria-expanded', String(show));
      if (show) this.fillTable();
    },
    fillTable() {
      const s = this.series;
      const ch = S.channels.get(this.id);
      if (!s || !ch) return;
      this.table.textContent = '';
      const head = h('tr', {}, h('th', { text: T('Time') }), h('th', { class: 'n', text: s.bucket ? T('LegendMean') : T('LegendValue') }));
      if (s.bucket) { head.appendChild(h('th', { class: 'n', text: T('StatMin') })); head.appendChild(h('th', { class: 'n', text: T('StatMax') })); }
      const body = h('tbody');
      for (let i = s.t.length - 1; i >= 0 && body.children.length < 2000; i--) {
        const tr = h('tr', {}, h('td', { class: 'num', text: HD.fmtTime(s.t[i]) }), h('td', { class: 'n', text: fmt(s.v[i]) }));
        if (s.bucket) { tr.appendChild(h('td', { class: 'n', text: fmt(s.lo[i]) })); tr.appendChild(h('td', { class: 'n', text: fmt(s.hi[i]) })); }
        body.appendChild(tr);
      }
      this.table.appendChild(h('table', {}, h('caption', { class: 'muted small', text: ch.name + (ch.unit ? ' (' + ch.unit + ')' : '') }), h('thead', {}, head), body));
    },
    fillEvents(events, ch) {
      this.eventsBox.textContent = '';
      if (!events.length) { this.eventsBox.appendChild(h('p', { class: 'muted', text: T('NoEventsInPeriod') })); return; }
      const body = h('tbody');
      for (const e of [...events].reverse().slice(0, 200)) {
        body.appendChild(h('tr', { class: e.ack ? 'acked' : null },
          h('td', { class: 'num' }, HD.fmtTime(e.t0)), h('td', {}, kindChip(e.kind), h('div', { class: 'why', text: why(e, ch.unit) })),
          h('td', { class: 'n', text: e.t1 === null ? T('Ongoing') : fmtDur(e.t1 - e.t0) })));
      }
      this.eventsBox.appendChild(h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, h('th', { text: T('Start') }), h('th', { text: T('What') }), h('th', { class: 'n', text: T('Duration') }))), body)));
    },
    async fillStats(from, to, ch) {
      const data = await api('/api/stats?from=' + from + '&to=' + to);
      const r = data.channels.find(c => c.ch === this.id);
      this.statsBox.textContent = '';
      if (!r) { this.statsBox.appendChild(h('p', { class: 'muted', text: T('NoStats') })); return; }
      const items = [[T('StatCount'), fmt(r.n)], [T('StatMean'), withUnit(r.mean, ch.unit)], [T('StatSd'), withUnit(r.sd, ch.unit)],
        [T('StatMin'), withUnit(r.min, ch.unit)], [T('StatMax'), withUnit(r.max, ch.unit)],
        [T('StatNoise'), r.noise === null ? '–' : '± ' + withUnit(r.noise, ch.unit)], [T('StatGap'), fmtDur(r.gap_ms)]];
      let total = 0;
      for (const k in r.events) total += r.events[k];
      items.push([T('StatEvents'), fmt(total)]);
      for (const [k, v] of items) this.statsBox.appendChild(h('div', { class: 'stat' }, h('div', { class: 'k', text: k }), h('div', { class: 'v num', text: v })));
    },
    settingsForm(ch) {
      const name = h('input', { type: 'text', value: ch.name, maxlength: 120 });
      const unit = h('input', { type: 'text', value: ch.unit, maxlength: 20, size: 8 });
      const lo = h('input', { type: 'number', step: 'any', value: ch.lo === null ? '' : ch.lo });
      const hi = h('input', { type: 'number', step: 'any', value: ch.hi === null ? '' : ch.hi });
      const hidden = h('input', { type: 'checkbox' });
      hidden.checked = ch.hidden;
      const msg = h('span', { class: 'muted small', role: 'status' });
      const save = async () => {
        try {
          await api('/api/channels/' + this.id, { method: 'POST', body: {
            name: name.value, unit: unit.value, lo: lo.value === '' ? null : Number(lo.value), hi: hi.value === '' ? null : Number(hi.value), hidden: hidden.checked } });
          msg.textContent = T('Saved');
          await poll();
          this.updateHead();
          this.load(true);
        } catch (e) { msg.textContent = T('Err_' + e.message) !== 'Err_' + e.message ? T('Err_' + e.message) : e.message; }
      };
      return h('section', {}, h('h2', { text: T('ChannelSettings') }),
        h('div', { class: 'card' },
          h('div', { class: 'form-row' },
            h('label', { class: 'field' }, T('Name'), name), h('label', { class: 'field' }, T('Unit'), unit),
            h('label', { class: 'field' }, T('LimitLo'), lo), h('label', { class: 'field' }, T('LimitHi'), hi),
            h('label', { class: 'check' }, hidden, T('HideChannel')),
            h('button', { class: 'btn primary', type: 'button', text: T('Save'), onclick: save }), msg),
          h('p', { class: 'muted small', text: T('LimitHelp') })));
    },
    onLive() { this.updateHead(); }
  };

  // Every anomaly, filtered, with what each one means.
  pages.events = {
    render(root) {
      this.root = root;
      this.range = '7d';
      this.ch = '';
      this.kind = '';
      this.hideAck = false;
      this.offset = 0;
      root.appendChild(h('h1', { text: T('EventsTitle') }));
      root.appendChild(h('p', { class: 'lead', text: T('EventsLead') }));
      const chSel = h('select', { 'aria-label': T('Channel'), onchange: ev => { this.ch = ev.target.value; this.offset = 0; this.load(); } },
        h('option', { value: '', text: T('AllChannels') }), sortedChannels().map(c => h('option', { value: c.id, text: c.name })));
      const kindSel = h('select', { 'aria-label': T('Kind'), onchange: ev => { this.kind = ev.target.value; this.offset = 0; this.load(); } },
        h('option', { value: '', text: T('AllKinds') }), ['gap', 'spike', 'step', 'stuck', 'range', 'noisy', 'unusual'].map(k => h('option', { value: k, text: T('Kind_' + k) })));
      const ack = h('input', { type: 'checkbox', onchange: ev => { this.hideAck = ev.target.checked; this.offset = 0; this.load(); } });
      this.csv = h('a', { class: 'btn', text: T('DownloadCsv') });
      root.appendChild(h('div', { class: 'toolbar' }, rangeButtons(this.range, r => { this.range = r; this.offset = 0; this.load(); }),
        chSel, kindSel, h('label', { class: 'check' }, ack, T('HideAcked')), h('span', { class: 'grow' }), this.csv));
      this.summary = h('div', { class: 'stats-grid' });
      root.appendChild(h('div', { class: 'card' }, this.summary));
      this.list = h('div');
      root.appendChild(h('h2', { text: T('EventList') }));
      root.appendChild(this.list);
      root.appendChild(h('details', { class: 'card' }, h('summary', { text: T('KindsHelpTitle') }),
        h('dl', {}, ['gap', 'spike', 'step', 'stuck', 'range', 'noisy', 'unusual'].map(k => [h('dt', {}, kindChip(k)), h('dd', { class: 'muted', text: T('KindHelp_' + k) })]))));
      this.load();
    },
    query() {
      const now = Date.now();
      const span = RANGES.find(r => r[0] === this.range)[1];
      let q = 'from=' + (span ? now - span : 0) + '&to=' + now;
      if (this.ch) q += '&ch=' + this.ch;
      if (this.kind) q += '&kind=' + this.kind;
      if (this.hideAck) q += '&hide_ack=1';
      return q;
    },
    async load() {
      const q = this.query();
      this.csv.setAttribute('href', '/api/events.csv?' + q + '&lang=' + S.lang);
      const data = await api('/api/events?' + q + '&limit=100&offset=' + this.offset);
      this.summary.textContent = '';
      const byKind = data.by_kind;
      let all = 0;
      for (const k in byKind) all += byKind[k];
      this.summary.appendChild(h('div', { class: 'stat' }, h('div', { class: 'k', text: T('Total') }), h('div', { class: 'v num', text: fmt(all) })));
      for (const k of ['gap', 'spike', 'step', 'stuck', 'range', 'noisy', 'unusual'])
        this.summary.appendChild(h('div', { class: 'stat' }, h('div', { class: 'k' }, kindChip(k)), h('div', { class: 'v num', text: fmt(byKind[k] || 0) })));
      this.list.textContent = '';
      if (!data.events.length) { this.list.appendChild(h('p', { class: 'muted', text: T('NoEventsInPeriod') })); return; }
      const body = h('tbody');
      for (const e of data.events) {
        const ch = S.channels.get(e.ch);
        const btn = h('button', { class: 'btn small', type: 'button', 'aria-pressed': String(e.ack), text: e.ack ? T('Acked') : T('Ack'),
          onclick: async () => { await api('/api/events/' + e.id + '/ack', { method: 'POST', body: { ack: !e.ack } }); this.load(); } });
        body.appendChild(h('tr', { class: e.ack ? 'acked' : null },
          h('td', { class: 'num' }, HD.fmtTime(e.t0)),
          h('td', {}, ch ? h('a', { href: '#/ch/' + e.ch, text: ch.name }) : String(e.ch)),
          h('td', {}, kindChip(e.kind), h('div', { class: 'why', text: why(e, ch ? ch.unit : '') })),
          h('td', { class: 'n', text: e.t1 === null ? T('Ongoing') : fmtDur(e.t1 - e.t0) }),
          h('td', {}, btn)));
      }
      this.list.appendChild(h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, h('th', { text: T('Start') }), h('th', { text: T('Channel') }), h('th', { text: T('What') }),
          h('th', { class: 'n', text: T('Duration') }), h('th', { text: T('AckColumn') }))), body)));
      const pager = h('div', { class: 'pager' },
        h('button', { class: 'btn', type: 'button', text: T('Newer'), disabled: this.offset === 0, onclick: () => { this.offset = Math.max(0, this.offset - 100); this.load(); } }),
        h('span', { class: 'muted small', text: F('ShowingOf', this.offset + 1, this.offset + data.events.length, data.total) }),
        h('button', { class: 'btn', type: 'button', text: T('Older'), disabled: this.offset + 100 >= data.total, onclick: () => { this.offset += 100; this.load(); } }));
      this.list.appendChild(pager);
    },
    onLive(data) { if (data.events.length && this.offset === 0) { clearTimeout(this.t); this.t = setTimeout(() => this.load(), 500); } }
  };

  // In which situations anomalies happen more often, what comes before them, and when they happen.
  const chName = id => { const c = S.channels.get(id); return c ? c.name : '#' + id; };
  const chUnit = id => { const c = S.channels.get(id); return c ? c.unit : ''; };

  function dayName(d) {
    // 4 January 2026 was a Sunday.
    return new Intl.DateTimeFormat(S.lang === 'ko' ? 'ko-KR' : 'en-US', { weekday: 'long' }).format(new Date(2026, 0, 4 + d));
  }

  function condText(c) {
    switch (c.type) {
      case 'hour': return F('CondHour', c.from, c.to);
      case 'weekday': return dayName(c.day);
      case 'daytype': return T(c.weekend ? 'CondWeekend' : 'CondWeekday');
      case 'high': return F('CondHigh', chName(c.ch), withUnit(c.value, chUnit(c.ch)));
      case 'low': return F('CondLow', chName(c.ch), withUnit(c.value, chUnit(c.ch)));
      case 'value': return F('CondValue', chName(c.ch), fmt(c.value));
    }
    return '';
  }

  function eventText(ch, kind) {
    if (kind.startsWith('to:')) return F('SwitchTo', chName(ch), fmt(Number(kind.slice(3))));
    return chName(ch) + ' · ' + T('Kind_' + kind);
  }

  const pct = x => fmt(Math.round(1000 * x) / 10) + '%';

  pages.patterns = {
    render(root) {
      this.range = '30d';
      root.appendChild(h('h1', { text: T('PatternsTitle') }));
      root.appendChild(h('p', { class: 'lead', text: T('PatternsLead') }));
      this.runBtn = h('button', { class: 'btn primary', type: 'button', text: T('Analyze'), onclick: () => this.run() });
      this.status = h('span', { class: 'muted small', role: 'status' });
      root.appendChild(h('div', { class: 'toolbar' },
        seg([['7d', T('Range_7d')], ['30d', T('Range_30d')], ['all', T('Range_all')]], this.range, r => { this.range = r; this.run(); }, T('Period')),
        this.runBtn, this.status));
      this.body = h('div');
      root.appendChild(this.body);
      this.load(true);
    },
    destroy() { clearTimeout(this.timer); },
    async run() {
      const now = Date.now();
      const span = RANGES.find(r => r[0] === this.range)[1];
      await api('/api/patterns/run', { method: 'POST', body: { from: span ? now - span : 0, to: now } });
      this.load(false);
    },
    async load(first) {
      clearTimeout(this.timer);
      const data = await api('/api/patterns');
      this.runBtn.disabled = data.running;
      if (data.running) {
        this.status.textContent = T('Analyzing');
        this.body.style.opacity = '0.6';
        this.timer = setTimeout(() => this.load(false), 1000);
        return;
      }
      this.body.style.opacity = '';
      const r = data.result;
      // The first visit with nothing analysed yet, or an old analysis: run it.
      if (first && (!r || Date.now() - r.created > 3600e3)) { this.run(); return; }
      if (data.error) this.status.textContent = F('AnalyzeFailed', data.error);
      else if (r) this.status.textContent = F('AnalyzedAt', HD.fmtTime(r.created, false), fmt(r.slots), fmtDur(r.slot_ms), fmt(r.tests),
        HD.fmtTime(r.from, false).slice(0, 10), HD.fmtTime(r.to, false).slice(0, 10));
      this.show(r);
    },
    show(r) {
      this.body.textContent = '';
      if (!r) return;
      if (!r.rules.length && !r.sequences.length)
        this.body.appendChild(h('p', { class: 'notice', text: r.targets ? T('NoPatterns') : T('FewEvents') }));

      if (r.rules.length) {
        this.body.appendChild(h('h2', { text: T('Situations') }));
        const groups = new Map();
        for (const rule of r.rules) {
          const key = rule.ch + '/' + rule.kind;
          if (!groups.has(key)) groups.set(key, []);
          groups.get(key).push(rule);
        }
        const list = h('div', { class: 'source-list' });
        for (const rules of groups.values()) {
          const first = rules[0];
          rules.sort((a, b) => a.when.length - b.when.length || b.lift - a.lift);
          const ul = h('ul', { class: 'rules' });
          for (const rule of rules.slice(0, 6)) {
            ul.appendChild(h('li', {},
              h('span', { class: 'rule' }, F('RuleWhen', rule.when.map(condText).join(' · ')), h('strong', { text: ' ' + F('TimesUsual', fmt(Math.round(rule.lift * 10) / 10)) })),
              h('div', { class: 'why', text: F('RuleEvidence', pct(rule.slots / rule.observed), fmt(rule.hits), fmt(rule.events), pct(rule.hits / rule.events)) })));
          }
          list.appendChild(h('div', { class: 'card' },
            h('div', { class: 'detail-head' }, kindChip(first.kind), h('a', { href: '#/ch/' + first.ch, text: chName(first.ch) }),
              h('span', { class: 'muted small', text: F('EventCount', fmt(first.events)) })), ul));
        }
        this.body.appendChild(list);
      }

      if (r.sequences.length) {
        this.body.appendChild(h('h2', { text: T('Sequences') }));
        const ul = h('ul', { class: 'rules' });
        for (const s of r.sequences.slice(0, 30)) {
          ul.appendChild(h('li', {},
            h('span', { class: 'rule' }, F('SequenceText', eventText(s.ch_a, s.kind_a), fmtDur(s.window), eventText(s.ch_b, s.kind_b)),
              h('strong', { text: ' ' + F('TimesUsual', fmt(Math.round(s.lift * 10) / 10)) })),
            h('div', { class: 'why', text: F('SequenceEvidence', fmt(s.hits), fmt(s.count_b), fmt(Math.round(s.expected * 10) / 10)) })));
        }
        this.body.appendChild(h('div', { class: 'card' }, ul));
      }

      this.body.appendChild(h('h2', { text: T('WhenTitle') }));
      this.body.appendChild(this.heatmap(r.heat));
      this.body.appendChild(h('p', { class: 'muted small', text: T('PatternsMethod') }));
    },
    heatmap(heat) {
      let max = 0;
      for (const row of heat) for (const v of row) max = Math.max(max, v);
      // Monday first.
      const order = [1, 2, 3, 4, 5, 6, 0];
      const grid = h('div', { class: 'heat', role: 'img', 'aria-label': T('WhenTitle') });
      const tip = h('div', { class: 'tooltip hidden' });
      grid.appendChild(h('span'));
      for (let hr = 0; hr < 24; hr++) grid.appendChild(h('span', { class: 'heat-x', text: hr % 3 === 0 ? String(hr) : '' }));
      for (const d of order) {
        grid.appendChild(h('span', { class: 'heat-y', text: dayName(d) }));
        for (let hr = 0; hr < 24; hr++) {
          const v = heat[d][hr];
          const step = v === 0 || max === 0 ? 0 : Math.min(6, 1 + Math.floor(5 * v / max));
          const cell = h('span', { class: 'heat-cell s' + step });
          cell.addEventListener('pointerenter', () => {
            tip.textContent = '';
            tip.appendChild(h('div', { class: 'v', text: F('EventCount', fmt(v)) }));
            tip.appendChild(h('div', { text: dayName(d) + ' ' + F('CondHour', hr, hr + 1) }));
            tip.classList.remove('hidden');
            tip.style.left = (cell.offsetLeft + 18) + 'px';
            tip.style.top = (cell.offsetTop - 8) + 'px';
          });
          cell.addEventListener('pointerleave', () => tip.classList.add('hidden'));
          grid.appendChild(cell);
        }
      }
      const legend = h('div', { class: 'legend' }, h('span', { text: T('Fewer') }),
        [1, 2, 3, 4, 5, 6].map(s => h('span', { class: 'heat-cell s' + s })), h('span', { text: T('More') }));
      // The same numbers as a table, for reading without colour.
      const table = h('div', { class: 'table-wrap hidden' });
      const btn = h('button', { class: 'btn', type: 'button', text: T('ShowTable'), 'aria-expanded': 'false', onclick: () => {
        const show = table.classList.contains('hidden');
        table.classList.toggle('hidden', !show);
        btn.textContent = T(show ? 'HideTable' : 'ShowTable');
        btn.setAttribute('aria-expanded', String(show));
        if (show && !table.firstChild) {
          const head = h('tr', {}, h('th'), Array.from({ length: 24 }, (_, i) => h('th', { class: 'n', text: String(i) })));
          const body = h('tbody', {}, order.map(d => h('tr', {}, h('th', { text: dayName(d) }), heat[d].map(v => h('td', { class: 'n', text: String(v) })))));
          table.appendChild(h('table', {}, h('thead', {}, head), body));
        }
      } });
      return h('div', { class: 'card heat-card' }, h('div', { class: 'heat-wrap' }, grid, tip), h('div', { class: 'toolbar' }, legend, h('span', { class: 'grow' }), btn), table);
    }
  };

  // Statistics of every channel for a period.
  pages.stats = {
    render(root) {
      this.range = '24h';
      root.appendChild(h('h1', { text: T('StatsTitle') }));
      root.appendChild(h('p', { class: 'lead', text: T('StatsLead') }));
      this.csv = h('a', { class: 'btn', text: T('DownloadCsv') });
      root.appendChild(h('div', { class: 'toolbar' }, rangeButtons(this.range, r => { this.range = r; this.load(); }), h('span', { class: 'grow' }), this.csv));
      this.box = h('div');
      root.appendChild(this.box);
      this.load();
    },
    async load() {
      const now = Date.now();
      const span = RANGES.find(r => r[0] === this.range)[1];
      const q = 'from=' + (span ? now - span : 0) + '&to=' + now;
      this.csv.setAttribute('href', '/api/stats.csv?' + q + '&lang=' + S.lang);
      const data = await api('/api/stats?' + q);
      this.box.textContent = '';
      if (!data.channels.length) { this.box.appendChild(h('p', { class: 'muted', text: T('NoStats') })); return; }
      const cols = ['StatCount', 'StatMean', 'StatSd', 'StatMin', 'StatMax', 'StatNoise', 'StatGap', 'StatEvents'];
      const body = h('tbody');
      for (const r of data.channels) {
        const ch = S.channels.get(r.ch);
        if (!ch) continue;
        let total = 0;
        const parts = [];
        for (const k of ['gap', 'spike', 'step', 'stuck', 'range', 'noisy', 'unusual']) if (r.events[k]) { total += r.events[k]; parts.push(T('Kind_' + k) + ' ' + r.events[k]); }
        const u = v => withUnit(v, ch.unit);
        body.appendChild(h('tr', {},
          h('td', {}, h('a', { href: '#/ch/' + r.ch, text: ch.name })),
          h('td', { class: 'n', text: fmt(r.n) }), h('td', { class: 'n', text: u(r.mean) }), h('td', { class: 'n', text: u(r.sd) }),
          h('td', { class: 'n', text: u(r.min) }), h('td', { class: 'n', text: u(r.max) }),
          h('td', { class: 'n', text: r.noise === null ? '–' : '± ' + u(r.noise) }), h('td', { class: 'n', text: fmtDur(r.gap_ms) }),
          h('td', { class: 'n' }, fmt(total), parts.length ? h('div', { class: 'why', text: parts.join(' · ') }) : null)));
      }
      this.box.appendChild(h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, h('th', { text: T('Channel') }), cols.map(c => h('th', { class: 'n', text: T(c) })))), body)));
      this.box.appendChild(h('p', { class: 'muted small', text: T('StatsHelp') }));
    }
  };

  // Where the data comes from: the running connections, imports and what can be added.
  pages.sources = {
    render(root) {
      this.root = root;
      root.appendChild(h('h1', { text: T('SourcesTitle') }));
      root.appendChild(h('p', { class: 'lead', text: T('SourcesLead') }));
      this.list = h('div', { class: 'source-list' });
      root.appendChild(this.list);
      this.jobsBox = h('div');
      root.appendChild(this.jobsBox);
      root.appendChild(h('h2', { text: T('AddSource') }));
      this.formBox = h('div');
      root.appendChild(this.formBox);
      this.importBox = h('div');
      root.appendChild(this.importBox);
      this.addGrid = h('div', { class: 'add-grid' });
      root.appendChild(this.addGrid);
      this.build();
      this.showDrop();
      if (S.pendingFile) { const f = S.pendingFile; S.pendingFile = null; this.upload(f); }
    },
    build() {
      this.list.textContent = '';
      if (!S.sources.length) this.list.appendChild(h('p', { class: 'muted', text: T('NoSources') }));
      for (const s of S.sources) this.list.appendChild(this.sourceCard(s));
      this.addGrid.textContent = '';
      if (!S.sources.some(s => s.kind === 'pc'))
        this.addGrid.appendChild(h('div', { class: 'card add-card' }, h('span', { class: 'title', text: T('Kind_src_pc') }),
          h('span', { class: 'muted small', text: T('AddPcHelp') }),
          h('button', { class: 'btn', type: 'button', text: T('Add'), onclick: async () => {
            await api('/api/sources', { method: 'POST', body: { kind: 'pc', name: T('PcSource'), config: { interval: 5 } } });
            await poll(); this.build();
          } })));
      for (const k of ['http', 'mqtt', 'modbus', 'serial', 'folder'])
        this.addGrid.appendChild(h('div', { class: 'card add-card' }, h('span', { class: 'title', text: T('Kind_src_' + k) }),
          h('span', { class: 'muted small', text: T('AddHelp_' + k) }),
          h('button', { class: 'btn', type: 'button', text: T('Add'), onclick: () => this.openForm(k, null) })));
      this.lastSig = this.sig();
      this.buildJobs();
    },
    // One form for adding or changing a source; the fields depend on the kind.
    async openForm(kind, src) {
      const cfg = src ? src.config : {};
      const box = this.formBox;
      box.textContent = '';
      const fields = {};
      const val = (k, d) => (cfg[k] !== undefined && cfg[k] !== null ? cfg[k] : d);
      const text = (k, d, type) => (fields[k] = h('input', { type: type || 'text', value: val(k, d) }));
      const num = (k, d, step) => (fields[k] = h('input', { type: 'number', step: step || 'any', value: val(k, d), size: 8 }));
      const check = k => { const c = h('input', { type: 'checkbox' }); c.checked = !!val(k, false); fields[k] = c; return c; };
      const select = (k, d, options) => (fields[k] = h('select', {}, options.map(([v, label]) => h('option', { value: v, text: label, selected: String(val(k, d)) === String(v) }))));
      const field = (k, control, wide) => h('label', { class: 'field' + (wide ? ' wide' : '') }, T('F_' + k), control);
      const name = h('input', { type: 'text', value: src ? src.name : T('Kind_src_' + kind), maxlength: 120 });
      const rows = [h('label', { class: 'field' }, T('SourceName'), name)];
      let registers = null, rtuOnly = [];

      if (kind === 'http') {
        rows.push(field('port', num('port', 47110, 1)), h('label', { class: 'check' }, check('lan'), T('F_lan')));
      } else if (kind === 'mqtt') {
        rows.push(field('host', text('host', '')), field('port', num('port', 1883, 1)), h('label', { class: 'check' }, check('tls'), T('F_tls')),
          field('user', text('user', '')),
          field('password', fields.password = h('input', { type: 'password', autocomplete: 'new-password', placeholder: cfg.has_password ? T('PasswordKept') : '' })),
          field('topics', text('topics', '#'), true), field('qos', select('qos', 0, [[0, '0'], [1, '1']])),
          h('label', { class: 'check' }, check('retained'), T('F_retained')));
      } else if (kind === 'modbus') {
        rows.push(field('host', text('host', '')), field('port', num('port', 502, 1)), field('unit', num('unit', 1, 1)), field('interval', num('interval', 5)));
        registers = this.registerEditor(cfg.registers || []);
      } else if (kind === 'serial') {
        let ports = [];
        try { ports = await api('/api/serial-ports'); } catch (e) { }
        const current = val('port', '');
        if (current && !ports.includes(current)) ports.unshift(current);
        rows.push(field('port', ports.length ? select('port', current || ports[0], ports.map(p => [p, p])) : h('span', { class: 'notice', text: T('NoSerialPorts') })),
          field('baud', select('baud', 9600, [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200].map(b => [b, String(b)]))),
          field('mode', select('mode', 'text', [['text', T('Opt_text')], ['rtu', T('Opt_rtu')]])));
        const rtu = [field('parity', select('parity', 'none', [['none', T('Opt_none')], ['even', T('Opt_even')], ['odd', T('Opt_odd')]])),
          field('unit', num('unit', 1, 1)), field('interval', num('interval', 5))];
        rows.push(...rtu);
        registers = this.registerEditor(cfg.registers || []);
        rtuOnly = rtu.concat([registers.el]);
        const sync = () => rtuOnly.forEach(el => el.classList.toggle('hidden', fields.mode.value !== 'rtu'));
        fields.mode.addEventListener('change', sync);
        setTimeout(sync, 0);
      } else if (kind === 'folder') {
        rows.push(field('path', text('path', ''), true), field('pattern', text('pattern', '*.csv')));
      }

      const msg = h('p', { class: 'notice error hidden', role: 'alert' });
      const save = async () => {
        const config = {};
        for (const [k, el] of Object.entries(fields)) {
          if (el.type === 'checkbox') config[k] = el.checked;
          else if (el.type === 'number' || ['qos', 'baud'].includes(k)) config[k] = Number(el.value);
          else config[k] = el.value.trim();
        }
        if (kind === 'mqtt' && !config.password) delete config.password;
        if (registers) config.registers = registers.value();
        try {
          if (src) await api('/api/sources/' + src.id, { method: 'POST', body: { name: name.value, config } });
          else await api('/api/sources', { method: 'POST', body: { kind, name: name.value, config } });
          box.textContent = '';
          await poll();
          this.build();
        } catch (e) {
          msg.textContent = T('Err_' + e.message) !== 'Err_' + e.message ? T('Err_' + e.message) : e.message;
          msg.classList.remove('hidden');
        }
      };
      box.appendChild(h('div', { class: 'card' },
        h('h2', { text: (src ? T('EditSource') : T('AddSource')) + ' · ' + T('Kind_src_' + kind) }),
        h('p', { class: 'muted small', text: T('Help_' + kind) }),
        h('div', { class: 'form-row' }, rows), registers ? registers.el : null, msg,
        h('div', { class: 'toolbar' }, h('button', { class: 'btn primary', type: 'button', text: T('Save'), onclick: save }),
          h('button', { class: 'btn', type: 'button', text: T('Cancel'), onclick: () => { box.textContent = ''; } }))));
      box.scrollIntoView({ behavior: 'smooth', block: 'start' });
    },
    // A table of Modbus registers: what each value is called, where it is and how it is stored.
    registerEditor(list) {
      const body = h('tbody');
      const AREAS = ['holding', 'input', 'coil', 'discrete'], TYPES = ['int16', 'uint16', 'int32', 'uint32', 'float32'];
      const addRow = r => {
        r = r || {};
        const sel = (opts, v, labels) => h('select', {}, opts.map(o => h('option', { value: o, text: labels ? T(labels + o) : o, selected: o === v })));
        const cells = {
          name: h('input', { type: 'text', value: r.name || '', size: 10 }),
          fn: sel(AREAS, r.fn || 'holding', 'Opt_'),
          address: h('input', { type: 'number', min: 0, max: 65535, step: 1, value: r.address !== undefined ? r.address : 0, size: 6 }),
          type: sel(TYPES, r.type || 'uint16'),
          order: sel(['big', 'little'], r.order || 'big', 'Opt_'),
          scale: h('input', { type: 'number', step: 'any', value: r.scale !== undefined ? r.scale : 1, size: 6 }),
          unit: h('input', { type: 'text', value: r.unit || '', size: 5 })
        };
        const tr = h('tr', {}, Object.values(cells).map(c => h('td', {}, c)),
          h('td', {}, h('button', { class: 'btn danger', type: 'button', text: T('Remove'), onclick: () => tr.remove() })));
        tr.cells_ = cells;
        body.appendChild(tr);
      };
      for (const r of list) addRow(r);
      if (!list.length) addRow({ name: 'value1' });
      const head = h('tr', {}, ['F_reg_name', 'F_reg_fn', 'F_reg_address', 'F_reg_type', 'F_reg_order', 'F_reg_scale', 'F_reg_unit'].map(k => h('th', { text: T(k) })), h('th'));
      const el = h('div', {}, h('h2', { text: T('F_registers') }), h('p', { class: 'muted small', text: T('RegistersHelp') }),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, head), body)),
        h('div', { class: 'toolbar' }, h('button', { class: 'btn', type: 'button', text: T('AddRegister'), onclick: () => addRow({}) })));
      return {
        el,
        value: () => [...body.children].map(tr => {
          const c = tr.cells_;
          return { name: c.name.value.trim(), fn: c.fn.value, address: Number(c.address.value), type: c.type.value, order: c.order.value, scale: Number(c.scale.value), unit: c.unit.value.trim() };
        }).filter(r => r.name)
      };
    },
    // How a device sends to an HTTP source: addresses, the key, and examples to copy.
    httpHelp(s) {
      const port = s.config.port || 47110, key = s.config.key || '';
      const box = h('div', { class: 'http-help' });
      const fill = addresses => {
        box.textContent = '';
        const hosts = s.config.lan ? addresses : ['127.0.0.1'];
        const url = 'http://' + (hosts[0] || '127.0.0.1') + ':' + port + '/ingest/room1';
        box.appendChild(h('p', { class: 'small', text: T('HttpSendTo') }));
        box.appendChild(h('ul', { class: 'small' }, hosts.map(a => h('li', {}, h('code', { text: 'http://' + a + ':' + port + '/ingest' })))));
        const keyLine = h('p', { class: 'small' }, T('HttpKey') + ' ', h('code', { text: key }), ' ',
          h('button', { class: 'btn', type: 'button', text: T('Copy'), onclick: () => { try { navigator.clipboard.writeText(key); } catch (e) { } } }),
          ' ', h('button', { class: 'btn', type: 'button', text: T('NewKey'), onclick: async () => {
            if (!confirm(T('ConfirmNewKey'))) return;
            await api('/api/sources/' + s.id, { method: 'POST', body: { config: { new_key: true } } });
            await poll(); this.build();
          } }));
        box.appendChild(keyLine);
        if (!s.config.lan) box.appendChild(h('p', { class: 'muted small', text: T('HttpLocalOnly') }));
        box.appendChild(h('p', { class: 'small', text: T('HttpExample') }));
        box.appendChild(h('pre', { text: 'curl -X POST "' + url + '" -H "X-HOODATA-Key: ' + key + '" -d "temp=23.5,hum=61"' }));
        box.appendChild(h('p', { class: 'small', text: T('HttpEsp32') }));
        box.appendChild(h('pre', { text: [
          '#include <HTTPClient.h>',
          'HTTPClient http;',
          'http.begin("' + url + '");',
          'http.addHeader("X-HOODATA-Key", "' + key + '");',
          'http.POST("temp=" + String(temp) + ",hum=" + String(hum));',
          'http.end();'].join('\n') }));
      };
      fill([]);
      api('/api/network').then(n => fill(n.addresses)).catch(() => { });
      return h('details', {}, h('summary', { text: T('HttpHowTo') }), box);
    },
    summary(s) {
      const c = s.config || {};
      switch (s.kind) {
        case 'http': return F('SumHttp', c.port, T(c.lan ? 'SumLan' : 'SumLocal'));
        case 'mqtt': return F('SumMqtt', (c.tls ? 'mqtts://' : 'mqtt://') + c.host + ':' + c.port, c.topics);
        case 'modbus': return F('SumModbus', c.host + ':' + c.port, (c.registers || []).length);
        case 'serial': return F('SumSerial', c.port, c.baud, T('Opt_' + (c.mode || 'text')));
        case 'folder': return F('SumFolder', c.path, c.pattern);
      }
      return '';
    },
    sig() { return JSON.stringify(S.sources.map(s => [s.id, s.name, s.enabled, s.state, s.error, s.config])); },
    sourceCard(s) {
      const chans = sortedChannels().filter(c => c.source === s.id);
      const state = s.kind === 'file' ? 'idle' : s.state === 'running' ? 'ok' : s.state === 'error' ? 'alert' : 'learning';
      const stateText = s.kind === 'file' ? T('SourceImported') : T('SourceState_' + s.state);
      const actions = h('div', { class: 'toolbar' });
      if (s.kind !== 'file')
        actions.appendChild(h('button', { class: 'btn', type: 'button', text: s.enabled ? T('Pause') : T('Resume'),
          onclick: async () => { await api('/api/sources/' + s.id, { method: 'POST', body: { enabled: !s.enabled } }); await poll(); this.build(); } }));
      if (s.kind === 'pc') {
        const sel = h('select', { 'aria-label': T('Interval'), onchange: async ev => {
          await api('/api/sources/' + s.id, { method: 'POST', body: { config: { interval: Number(ev.target.value) } } }); await poll(); this.build();
        } }, [1, 5, 10, 30, 60].map(v => h('option', { value: v, text: F('EverySeconds', v), selected: (s.config && s.config.interval) === v })));
        actions.appendChild(h('label', { class: 'check' }, T('Interval'), sel));
      }
      if (['http', 'mqtt', 'modbus', 'serial', 'folder'].includes(s.kind))
        actions.appendChild(h('button', { class: 'btn', type: 'button', text: T('Settings'), onclick: () => this.openForm(s.kind, s) }));
      actions.appendChild(h('button', { class: 'btn danger', type: 'button', text: T('Delete'), onclick: async () => {
        if (!confirm(F('ConfirmDelete', s.name, chans.length))) return;
        await api('/api/sources/' + s.id, { method: 'DELETE' });
        await poll(); this.build();
      } }));
      const sum = this.summary(s);
      return h('div', { class: 'card source' },
        h('div', { class: 'grow' }, h('div', { class: 'name', text: s.name }),
          h('div', { class: 'muted small', text: T('Kind_src_' + s.kind) + ' · ' + F('ChannelCount', chans.length) + ' · ' + F('AddedAt', HD.fmtTime(s.created, false)) }),
          sum ? h('div', { class: 'muted small', text: sum }) : null,
          s.error ? h('div', { class: 'notice error', text: s.error }) : null,
          s.kind === 'http' ? this.httpHelp(s) : null),
        h('span', { class: 'status ' + state }, icon(state), stateText), actions);
    },
    showDrop() {
      this.importBox.textContent = '';
      const input = h('input', { type: 'file', accept: '.csv,.txt,.tsv,.xlsx,text/csv', class: 'hidden', onchange: ev => { if (ev.target.files[0]) this.upload(ev.target.files[0]); } });
      const drop = h('div', { class: 'drop' },
        h('p', { text: T('DropCsv') }),
        h('button', { class: 'btn primary', type: 'button', text: T('ChooseFile'), onclick: () => input.click() }), input,
        h('p', { class: 'muted small', text: T('CsvHelp') }));
      drop.addEventListener('dragover', ev => { ev.preventDefault(); drop.classList.add('over'); });
      drop.addEventListener('dragleave', () => drop.classList.remove('over'));
      drop.addEventListener('drop', ev => { ev.preventDefault(); ev.stopPropagation(); drop.classList.remove('over'); if (ev.dataTransfer.files[0]) this.upload(ev.dataTransfer.files[0]); });
      this.importBox.appendChild(h('div', { class: 'card add-card' }, h('span', { class: 'title', text: T('Kind_src_file') }), drop));
    },
    upload(file) {
      this.importBox.textContent = '';
      const bar = h('div');
      const status = h('p', { class: 'muted', text: F('Uploading', file.name) });
      this.importBox.appendChild(h('div', { class: 'card' }, status, h('div', { class: 'progress' }, bar)));
      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/api/import?name=' + encodeURIComponent(file.name));
      xhr.setRequestHeader('X-HOODATA', '1');
      xhr.upload.onprogress = ev => { if (ev.lengthComputable) bar.style.width = (100 * ev.loaded / ev.total).toFixed(1) + '%'; };
      xhr.onload = () => {
        let data = {};
        try { data = JSON.parse(xhr.responseText); } catch (e) { }
        if (xhr.status === 200) this.preview(data);
        else { this.showDrop(); this.importBox.prepend(h('p', { class: 'notice error', text: F('UploadFailed', data.error || xhr.status) })); }
      };
      xhr.onerror = () => { this.showDrop(); this.importBox.prepend(h('p', { class: 'notice error', text: F('UploadFailed', 'network') })); };
      xhr.send(file);
    },
    preview(job) {
      const L = job.layout;
      this.importBox.textContent = '';
      const name = h('input', { type: 'text', value: job.name, maxlength: 120 });
      const timeSel = h('select', {}, h('option', { value: -1, text: T('NoTimeColumn') }),
        L.columns.filter(c => c.kind === 'time' || c.kind === 'epoch' || c.kind === 'date').map(c => h('option', { value: c.index, text: c.name, selected: c.index === L.time_column })));
      const zone = h('select', {}, h('option', { value: 'local', text: T('ZoneLocal') }), h('option', { value: 'utc', text: 'UTC' }));
      const interval = h('input', { type: 'number', value: 1, min: 0.001, step: 'any', size: 6 });
      const intervalRow = h('label', { class: 'field' }, T('RowInterval'), interval);
      const sync = () => intervalRow.classList.toggle('hidden', Number(timeSel.value) >= 0);
      timeSel.addEventListener('change', sync);
      sync();
      const boxes = [];
      const colList = h('div', { class: 'form-row' });
      for (const c of L.columns) {
        if (c.kind !== 'number') continue;
        const cb = h('input', { type: 'checkbox', value: c.index });
        cb.checked = job.columns.includes(c.index);
        boxes.push(cb);
        colList.appendChild(h('label', { class: 'check' }, cb, c.name + (c.unit ? ' (' + c.unit + ')' : '')));
      }
      const head = h('tr', {}, L.columns.map(c => h('th', {}, c.name, h('div', { class: 'why', text: T('ColKind_' + c.kind) }))));
      const rows = L.preview.map(r => h('tr', {}, r.map(f => h('td', { text: f }))));
      const msg = h('p', { class: 'notice error hidden', role: 'alert' });
      const start = async () => {
        const columns = boxes.filter(b => b.checked).map(b => Number(b.value));
        if (!columns.length) { msg.textContent = T('Err_no_columns'); msg.classList.remove('hidden'); return; }
        try {
          await api('/api/import/' + job.id + '/start', { method: 'POST', body: {
            name: name.value, columns, time_column: Number(timeSel.value), zone: zone.value, interval: Number(interval.value), start: Date.now() } });
          this.showDrop();
          await poll();
          this.buildJobs();
        } catch (e) { msg.textContent = T('Err_' + e.message); msg.classList.remove('hidden'); }
      };
      const info = F('FileInfo', L.encoding.toUpperCase(), L.delimiter === '\t' ? 'Tab' : L.delimiter, fmt(L.size / 1024) + ' KB');
      this.importBox.appendChild(h('div', { class: 'card' },
        h('h2', { text: F('ImportOf', job.name) }), h('p', { class: 'muted small', text: info + (L.descending ? ' · ' + T('Descending') : '') }),
        h('div', { class: 'form-row' }, h('label', { class: 'field' }, T('SourceName'), name), h('label', { class: 'field' }, T('TimeColumn'), timeSel),
          intervalRow, h('label', { class: 'field' }, T('TimeZone'), zone)),
        h('h2', { text: T('ValueColumns') }), colList,
        h('h2', { text: T('FirstRows') }), h('div', { class: 'table-wrap preview' }, h('table', {}, h('thead', {}, head), h('tbody', {}, rows))),
        msg,
        h('div', { class: 'toolbar' }, h('button', { class: 'btn primary', type: 'button', text: T('StartImport'), onclick: start }),
          h('button', { class: 'btn', type: 'button', text: T('Cancel'), onclick: async () => { await api('/api/import/' + job.id, { method: 'DELETE' }); this.showDrop(); } }))));
    },
    buildJobs() {
      this.jobsBox.textContent = '';
      const jobs = S.jobs.filter(j => j.state !== 'ready' && j.state !== 'cancelled');
      if (!jobs.length) return;
      this.jobsBox.appendChild(h('h2', { text: T('Imports') }));
      for (const j of jobs) {
        const bar = h('div');
        bar.style.width = (100 * j.progress).toFixed(1) + '%';
        let text;
        if (j.state === 'running') text = F('ImportRunning', fmt(j.rows));
        else if (j.state === 'done') text = F('ImportDone', fmt(j.rows), fmt(j.values)) + (j.skipped ? ' · ' + F('ImportSkipped', fmt(j.skipped)) : '') + (j.out_of_order ? ' · ' + F('ImportUnsorted', fmt(j.out_of_order)) : '');
        else text = F('ImportFailed', T('Err_' + j.error) !== 'Err_' + j.error ? T('Err_' + j.error) : j.error);
        this.jobsBox.appendChild(h('div', { class: 'card source' }, h('div', { class: 'grow' }, h('div', { class: 'name', text: j.name }),
          h('div', { class: 'muted small', text: text })), j.state === 'running' ? h('div', { class: 'progress' }, bar) : null,
          j.state === 'done' ? h('a', { class: 'btn', href: '#/', text: T('SeeDashboard') }) : null));
      }
    },
    onLive() {
      if (this.sig() !== this.lastSig) this.build();
      else this.buildJobs();
    }
  };

  pages.settings = {
    render(root) {
      root.appendChild(h('h1', { text: T('SettingsTitle') }));
      const msg = h('span', { class: 'muted small', role: 'status' });
      const save = async body => {
        try { S.info = await api('/api/settings', { method: 'POST', body }); msg.textContent = T('Saved'); } catch (e) { msg.textContent = e.message; }
      };
      const retention = h('input', { type: 'number', min: 1, max: 3650, value: S.info.retention_days, size: 6 });
      let theme = 'system';
      try { theme = localStorage.getItem('hoodata-theme') || 'system'; } catch (e) { }
      root.appendChild(h('section', {}, h('h2', { text: T('Sensitivity') }), h('div', { class: 'card' },
        seg([['low', T('Sens_low')], ['normal', T('Sens_normal')], ['high', T('Sens_high')]], S.info.sensitivity, v => save({ sensitivity: v }), T('Sensitivity')),
        h('p', { class: 'muted small', text: T('SensitivityHelp') }))));
      root.appendChild(h('section', {}, h('h2', { text: T('Retention') }), h('div', { class: 'card' },
        h('div', { class: 'form-row' }, h('label', { class: 'field' }, T('RetentionDays'), retention),
          h('button', { class: 'btn', type: 'button', text: T('Save'), onclick: () => save({ retention_days: Number(retention.value) }) })),
        h('p', { class: 'muted small', text: T('RetentionHelp') }))));
      root.appendChild(h('section', {}, h('h2', { text: T('Theme') }), h('div', { class: 'card' },
        seg([['system', T('Theme_system')], ['light', T('Theme_light')], ['dark', T('Theme_dark')]], theme, v => { setTheme(v); }, T('Theme')))));
      root.appendChild(msg);
      root.appendChild(h('section', {}, h('h2', { text: T('About') }), h('div', { class: 'card' }, h('dl', {},
        h('dt', { class: 'muted small', text: T('Version') }), h('dd', { text: S.info.version }),
        h('dt', { class: 'muted small', text: T('DataFolder') }), h('dd', { text: S.info.data_dir }),
        h('dt', { class: 'muted small', text: 'SQLite' }), h('dd', { text: S.info.sqlite + ' (Windows)' })),
        h('p', { class: 'muted small', text: T('PrivacyNote') }))));
    }
  };

  function setTheme(v) {
    try { localStorage.setItem('hoodata-theme', v); } catch (e) { }
    if (v === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', v);
    if (S.page && S.page.plot) S.page.plot.draw();
  }

  // ---- routing ----

  const NAV = [['overview', '#/', 'NavOverview'], ['events', '#/events', 'NavEvents'], ['patterns', '#/patterns', 'NavPatterns'], ['stats', '#/stats', 'NavStats'],
    ['sources', '#/sources', 'NavSources'], ['settings', '#/settings', 'NavSettings']];

  function route() {
    if (S.locked) return;
    const hash = location.hash || '#/';
    const m = hash.match(/^#\/(\w+)?(?:\/(\d+))?/) || [];
    const key = m[1] === 'ch' ? 'channel' : (m[1] || 'overview');
    const page = pages[key] || pages.overview;
    if (S.page && S.page.destroy) S.page.destroy();
    S.page = page;
    const app = document.getElementById('app');
    app.textContent = '';
    page.render(app, m[2]);
    // #/sources/add/<kind> opens that form, so a guide can link straight to it.
    const add = hash.match(/^#\/sources\/add\/(\w+)$/);
    if (add && key === 'sources' && ['http', 'mqtt', 'modbus', 'serial', 'folder'].includes(add[1])) page.openForm(add[1], null);
    const navKey = key === 'channel' ? 'overview' : key;
    for (const a of document.querySelectorAll('#nav a')) {
      if (a.dataset.key === navKey) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    }
  }

  function buildChrome() {
    document.documentElement.lang = S.lang;
    const nav = document.getElementById('nav');
    nav.textContent = '';
    for (const [key, href, label] of NAV) nav.appendChild(h('a', { href, 'data-key': key, text: T(label) }));
    for (const b of document.querySelectorAll('#lang button')) {
      b.setAttribute('aria-pressed', String(b.dataset.lang === S.lang));
    }
    setConnected(S.connected);
  }

  async function start() {
    try { const th = localStorage.getItem('hoodata-theme'); if (th && th !== 'system') document.documentElement.setAttribute('data-theme', th); } catch (e) { }
    S.strings = await (await fetch('/strings.json', { cache: 'no-cache' })).json();
    S.lang = (navigator.language || 'en').toLowerCase().startsWith('ko') ? 'ko' : 'en';
    try {
      S.info = await api('/api/info');
    } catch (e) {
      buildChrome();
      if (!S.locked) lock();
      return;
    }
    S.lang = S.info.lang === 'ko' || S.info.lang === 'en' ? S.info.lang : S.lang;
    buildChrome();
    for (const b of document.querySelectorAll('#lang button')) {
      b.addEventListener('click', async () => {
        S.lang = b.dataset.lang;
        S.info = await api('/api/settings', { method: 'POST', body: { lang: S.lang } });
        buildChrome();
        route();
      });
    }
    document.title = 'HOODATA';
    await poll();
    window.addEventListener('hashchange', route);
    route();
    S.pollTimer = setInterval(poll, 2000);

    // A file dropped anywhere is imported.
    window.addEventListener('dragover', ev => ev.preventDefault());
    window.addEventListener('drop', ev => {
      ev.preventDefault();
      const f = ev.dataTransfer && ev.dataTransfer.files[0];
      if (!f) return;
      S.pendingFile = f;
      if (location.hash === '#/sources') route(); else location.hash = '#/sources';
    });
  }

  document.addEventListener('DOMContentLoaded', start);
})();
