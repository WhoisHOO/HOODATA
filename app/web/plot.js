// HOODATA plots, drawn on a canvas with no outside library: a time-series line (with a min-max band when values are
// summarised), anomaly spans and markers, a crosshair tooltip that also follows the arrow keys, and drag to zoom.
'use strict';

(function () {
  const HD = window.HD || (window.HD = {});

  HD.token = function (name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  };

  // Numbers: about six significant digits (1,013.25 hPa, 101.284 kPa, 23.5), at most four decimals.
  HD.fmt = function (v, lang, decimals) {
    if (v === null || v === undefined || !isFinite(v)) return '–';
    const a = Math.abs(v);
    const whole = a >= 1 ? Math.floor(Math.log10(a)) + 1 : 1;
    const d = decimals !== undefined ? decimals : Math.max(0, Math.min(4, 6 - whole));
    return new Intl.NumberFormat(lang === 'ko' ? 'ko-KR' : 'en-US',
      decimals !== undefined ? { minimumFractionDigits: d, maximumFractionDigits: d } : { maximumFractionDigits: d }).format(v);
  };

  // Axis ticks: as many decimals as the step between ticks needs, the same for every tick.
  HD.tickDecimals = function (step) {
    if (!(step > 0)) return 0;
    let d = Math.max(0, -Math.floor(Math.log10(step) + 1e-9));
    if (Math.abs(step * Math.pow(10, d) - Math.round(step * Math.pow(10, d))) > 1e-6) d++;
    return Math.min(d, 6);
  };

  const pad2 = n => (n < 10 ? '0' : '') + n;

  // Times are shown in this PC's time zone, written the same way in both languages: 2026-10-25 13:05:09.
  HD.fmtTime = function (ms, seconds) {
    const d = new Date(ms);
    let s = d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
    if (seconds !== false) s += ':' + pad2(d.getSeconds());
    return s;
  };

  HD.fmtClock = function (ms, seconds) {
    const d = new Date(ms);
    return pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + (seconds ? ':' + pad2(d.getSeconds()) : '');
  };

  HD.isAlertKind = k => k === 'gap' || k === 'stuck' || k === 'range' || k === 'step';

  function niceStep(span, count) {
    const raw = span / Math.max(1, count);
    const p = Math.pow(10, Math.floor(Math.log10(raw)));
    const f = raw / p;
    return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
  }

  const SEC = 1000, MIN = 60 * SEC, HOUR = 60 * MIN, DAY = 24 * HOUR;
  const TIME_STEPS = [SEC, 2 * SEC, 5 * SEC, 10 * SEC, 15 * SEC, 30 * SEC, MIN, 2 * MIN, 5 * MIN, 10 * MIN, 15 * MIN,
    30 * MIN, HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 7 * DAY, 14 * DAY, 30 * DAY];

  function timeTicks(from, to, maxTicks) {
    const span = to - from;
    let step = TIME_STEPS[TIME_STEPS.length - 1];
    for (const s of TIME_STEPS) if (span / s <= maxTicks) { step = s; break; }
    // Align to local midnight so ticks fall on round local times.
    const start = new Date(from);
    start.setHours(0, 0, 0, 0);
    let t = start.getTime();
    const ticks = [];
    if (step >= DAY) {
      const days = Math.round(step / DAY);
      const d = new Date(t);
      while (d.getTime() < from) d.setDate(d.getDate() + 1);
      while (d.getTime() <= to && ticks.length < 200) {
        ticks.push(d.getTime());
        d.setDate(d.getDate() + days);
      }
    } else {
      t += Math.ceil((from - t) / step) * step;
      for (; t <= to && ticks.length < 400; t += step) ticks.push(t);
    }
    return { ticks, step };
  }

  function tickLabel(t, step, prev) {
    const d = new Date(t);
    const date = pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
    if (step >= DAY) return date;
    const clock = HD.fmtClock(t, step < MIN);
    const newDay = prev === null || new Date(prev).getDate() !== d.getDate();
    return newDay ? date + ' ' + clock : clock;
  }

  function lowerBound(arr, x) {
    let lo = 0, hi = arr.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (arr[m] < x) lo = m + 1; else hi = m; }
    return lo;
  }

  class Plot {
    constructor(el, opts) {
      this.el = el;
      this.o = Object.assign({ height: 260, spark: false, zoom: true, zeroBased: false, label: '', lang: 'en' }, opts || {});
      el.classList.add('plot');
      el.style.height = this.o.height + 'px';
      this.canvas = document.createElement('canvas');
      if (!this.o.spark) this.canvas.tabIndex = 0;
      this.canvas.setAttribute('role', 'img');
      if (this.o.label) this.canvas.setAttribute('aria-label', this.o.label);
      el.appendChild(this.canvas);
      this.tip = document.createElement('div');
      this.tip.className = 'tooltip hidden';
      el.appendChild(this.tip);
      this.data = null;
      this.hover = -1;
      this.drag = null;
      this.ro = new ResizeObserver(() => this.draw());
      this.ro.observe(el);
      this.bind();
    }

    destroy() { this.ro.disconnect(); }

    setData(d) {
      this.data = d;
      this.hover = -1;
      this.hideTip();
      this.draw();
    }

    // ---- scales ----

    layout() {
      const w = this.el.clientWidth, h = this.el.clientHeight;
      const d = this.data;
      const spark = this.o.spark;
      let min = Infinity, max = -Infinity;
      if (d) {
        const lo = d.lo || d.v, hi = d.hi || d.v;
        for (let i = 0; i < d.v.length; i++) {
          if (isFinite(lo[i]) && lo[i] < min) min = lo[i];
          if (isFinite(hi[i]) && hi[i] > max) max = hi[i];
        }
        for (const l of d.limits || []) { if (l.value < min) min = l.value; if (l.value > max) max = l.value; }
      }
      if (!isFinite(min)) { min = 0; max = 1; }
      if (this.o.zeroBased && min > 0) min = 0;
      if (min === max) { const e = Math.abs(min) * 0.05 || 1; min -= e; max += e; }
      const padV = (max - min) * 0.08;
      if (!(this.o.zeroBased && min === 0)) min -= padV;
      max += padV;

      const ctx = this.ctx;
      ctx.font = '12px "Segoe UI", "Malgun Gothic", sans-serif';
      let yTicks = [], step = 0;
      if (!spark) {
        step = niceStep(max - min, Math.max(2, Math.floor(h / 56)));
        for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) yTicks.push(Math.abs(v) < step * 1e-9 ? 0 : v);
      }
      let left = 2;
      const tickDec = HD.tickDecimals(step);
      if (!spark) for (const v of yTicks) left = Math.max(left, ctx.measureText(HD.fmt(v, this.o.lang, tickDec)).width + 12);
      const m = spark ? { l: 2, r: 6, t: 6, b: 4 } : { l: Math.ceil(left), r: 14, t: 12, b: 24 };
      const from = d ? d.from : 0, to = d ? d.to : 1;
      return {
        w, h, m, min, max, from, to, yTicks, tickDec,
        x: t => m.l + (t - from) / (to - from) * (w - m.l - m.r),
        y: v => m.t + (1 - (v - min) / (max - min)) * (h - m.t - m.b),
        t: x => from + (x - m.l) / (w - m.l - m.r) * (to - from)
      };
    }

    // ---- drawing ----

    draw() {
      const w = this.el.clientWidth, h = this.el.clientHeight;
      if (w <= 0 || h <= 0) return;
      const dpr = window.devicePixelRatio || 1;
      if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
        this.canvas.width = Math.round(w * dpr);
        this.canvas.height = Math.round(h * dpr);
      }
      const ctx = this.ctx = this.canvas.getContext('2d');
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      const L = this.L = this.layout();
      const d = this.data;
      const c = {
        ink2: HD.token('--ink-2'), line: HD.token('--line'), axis: HD.token('--axis'), series: HD.token('--series'),
        wash: HD.token('--series-wash'), surface: HD.token('--surface'), warn: HD.token('--warn'), alert: HD.token('--alert'),
        focus: HD.token('--focus')
      };
      const plotW = w - L.m.l - L.m.r, plotH = h - L.m.t - L.m.b;

      if (!this.o.spark) {
        // Gridlines: solid hairlines, one step off the surface.
        ctx.lineWidth = 1;
        ctx.strokeStyle = c.line;
        ctx.fillStyle = c.ink2;
        ctx.textAlign = 'right';
        ctx.textBaseline = 'middle';
        for (const v of L.yTicks) {
          const y = Math.round(L.y(v)) + 0.5;
          ctx.beginPath(); ctx.moveTo(L.m.l, y); ctx.lineTo(w - L.m.r, y); ctx.stroke();
          ctx.fillText(HD.fmt(v, this.o.lang, L.tickDec), L.m.l - 8, y);
        }
        ctx.strokeStyle = c.axis;
        ctx.beginPath(); ctx.moveTo(L.m.l, h - L.m.b + 0.5); ctx.lineTo(w - L.m.r, h - L.m.b + 0.5); ctx.stroke();
        if (d) {
          const tt = timeTicks(d.from, d.to, Math.max(2, Math.floor(plotW / 110)));
          ctx.textAlign = 'center';
          ctx.textBaseline = 'top';
          let prev = null, lastRight = -Infinity;
          for (const t of tt.ticks) {
            const x = L.x(t);
            const label = tickLabel(t, tt.step, prev);
            const half = ctx.measureText(label).width / 2;
            if (x - half < lastRight + 8 || x + half > w) continue;
            ctx.fillText(label, x, h - L.m.b + 6);
            ctx.beginPath(); ctx.moveTo(Math.round(x) + 0.5, h - L.m.b); ctx.lineTo(Math.round(x) + 0.5, h - L.m.b + 4); ctx.stroke();
            lastRight = x + half;
            prev = t;
          }
        }
      }
      if (!d) return;

      ctx.save();
      ctx.beginPath();
      ctx.rect(L.m.l, L.m.t - 4, plotW, plotH + 8);
      ctx.clip();

      // Anomalies: spans as a wash with a solid strip on top, short ones as a marker.
      const marks = [];
      for (const e of d.events || []) {
        const col = HD.isAlertKind(e.kind) ? c.alert : c.warn;
        const x0 = Math.max(L.m.l, L.x(e.t0)), x1 = Math.min(w - L.m.r, L.x(e.t1 === null ? d.to : e.t1));
        if (x1 - x0 >= 3) {
          ctx.globalAlpha = 0.14;
          ctx.fillStyle = col;
          ctx.fillRect(x0, L.m.t, x1 - x0, plotH);
          ctx.globalAlpha = 1;
          ctx.fillRect(x0, L.m.t - 4, x1 - x0, 3);
        } else marks.push({ x: (x0 + x1) / 2, col });
      }

      // Limits the user set for the channel.
      for (const l of d.limits || []) {
        const y = Math.round(L.y(l.value)) + 0.5;
        ctx.strokeStyle = c.alert;
        ctx.globalAlpha = 0.6;
        ctx.beginPath(); ctx.moveTo(L.m.l, y); ctx.lineTo(w - L.m.r, y); ctx.stroke();
        ctx.globalAlpha = 1;
      }

      const gap = d.gapMs || Infinity;
      const segments = [];
      let seg = null;
      for (let i = 0; i < d.t.length; i++) {
        if (!isFinite(d.v[i])) { seg = null; continue; }
        if (!seg || d.t[i] - d.t[i - 1] > gap) { seg = []; segments.push(seg); }
        seg.push(i);
      }
      if (d.lo && d.hi) {
        ctx.fillStyle = c.wash;
        for (const s of segments) {
          if (s.length < 2) continue;
          ctx.beginPath();
          s.forEach((i, k) => k ? ctx.lineTo(L.x(d.t[i]), L.y(d.hi[i])) : ctx.moveTo(L.x(d.t[i]), L.y(d.hi[i])));
          for (let k = s.length - 1; k >= 0; k--) ctx.lineTo(L.x(d.t[s[k]]), L.y(d.lo[s[k]]));
          ctx.closePath();
          ctx.fill();
        }
      }
      ctx.strokeStyle = c.series;
      ctx.lineWidth = 2;
      ctx.lineJoin = 'round';
      ctx.lineCap = 'round';
      for (const s of segments) {
        ctx.beginPath();
        s.forEach((i, k) => k ? ctx.lineTo(L.x(d.t[i]), L.y(d.v[i])) : ctx.moveTo(L.x(d.t[i]), L.y(d.v[i])));
        if (s.length === 1) { const x = L.x(d.t[s[0]]), y = L.y(d.v[s[0]]); ctx.moveTo(x - 1, y); ctx.lineTo(x + 1, y); }
        ctx.stroke();
      }
      ctx.restore();

      for (const mk of marks) {
        // A downward triangle on the top edge, with a surface ring so it stays clear over the line.
        const y = L.m.t;
        ctx.beginPath();
        ctx.moveTo(mk.x - 6, y - 5); ctx.lineTo(mk.x + 6, y - 5); ctx.lineTo(mk.x, y + 4); ctx.closePath();
        ctx.lineWidth = 2;
        ctx.strokeStyle = c.surface;
        ctx.stroke();
        ctx.fillStyle = mk.col;
        ctx.fill();
      }

      if (this.o.spark && d.t.length) {
        const i = d.t.length - 1;
        this.dot(L.x(d.t[i]), L.y(d.v[i]), c);
      }

      if (this.drag && this.drag.x1 !== undefined) {
        ctx.fillStyle = c.focus;
        ctx.globalAlpha = 0.12;
        const a = Math.min(this.drag.x0, this.drag.x1), b = Math.max(this.drag.x0, this.drag.x1);
        ctx.fillRect(a, L.m.t, b - a, plotH);
        ctx.globalAlpha = 1;
      }

      if (this.hover >= 0 && this.hover < d.t.length) {
        const x = Math.round(L.x(d.t[this.hover])) + 0.5;
        ctx.strokeStyle = c.ink2;
        ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(x, L.m.t); ctx.lineTo(x, h - L.m.b); ctx.stroke();
        if (isFinite(d.v[this.hover])) this.dot(x, L.y(d.v[this.hover]), c);
      }
    }

    dot(x, y, c) {
      const ctx = this.ctx;
      ctx.beginPath();
      ctx.arc(x, y, 4, 0, Math.PI * 2);
      ctx.lineWidth = 2;
      ctx.strokeStyle = c.surface;
      ctx.fillStyle = c.series;
      ctx.fill();
      ctx.stroke();
    }

    // ---- hover, keyboard and zoom ----

    nearest(x) {
      const d = this.data;
      if (!d || !d.t.length) return -1;
      const t = this.L.t(x);
      let i = lowerBound(d.t, t);
      if (i >= d.t.length) i = d.t.length - 1;
      if (i > 0 && t - d.t[i - 1] < d.t[i] - t) i--;
      return i;
    }

    bind() {
      const cv = this.canvas;
      cv.addEventListener('pointermove', ev => {
        if (!this.data || !this.L) return;
        const r = cv.getBoundingClientRect();
        const x = ev.clientX - r.left;
        if (this.drag) { this.drag.x1 = Math.max(this.L.m.l, Math.min(r.width - this.L.m.r, x)); }
        this.hover = this.nearest(x);
        this.draw();
        this.showTip();
      });
      cv.addEventListener('pointerleave', () => { if (!this.drag) { this.hover = -1; this.draw(); this.hideTip(); } });
      if (this.o.zoom && !this.o.spark) {
        cv.addEventListener('pointerdown', ev => {
          if (ev.button !== 0 || !this.L) return;
          const r = cv.getBoundingClientRect();
          this.drag = { x0: ev.clientX - r.left };
          cv.setPointerCapture(ev.pointerId);
        });
        cv.addEventListener('pointerup', ev => {
          const dg = this.drag;
          this.drag = null;
          if (dg && dg.x1 !== undefined && Math.abs(dg.x1 - dg.x0) > 6 && this.o.onZoom) {
            const a = this.L.t(Math.min(dg.x0, dg.x1)), b = this.L.t(Math.max(dg.x0, dg.x1));
            this.o.onZoom(Math.round(a), Math.round(b));
          }
          this.draw();
        });
      }
      cv.addEventListener('keydown', ev => {
        const d = this.data;
        if (!d || !d.t.length) return;
        let i = this.hover < 0 ? d.t.length - 1 : this.hover;
        const big = Math.max(1, Math.round(d.t.length / 20));
        if (ev.key === 'ArrowLeft') i = Math.max(0, i - (ev.shiftKey ? big : 1));
        else if (ev.key === 'ArrowRight') i = Math.min(d.t.length - 1, i + (ev.shiftKey ? big : 1));
        else if (ev.key === 'Home') i = 0;
        else if (ev.key === 'End') i = d.t.length - 1;
        else if (ev.key === 'Escape') { this.hover = -1; this.draw(); this.hideTip(); return; }
        else return;
        ev.preventDefault();
        this.hover = i;
        this.draw();
        this.showTip();
      });
      cv.addEventListener('blur', () => { this.hover = -1; this.draw(); this.hideTip(); });
    }

    hideTip() { this.tip.classList.add('hidden'); }

    showTip() {
      const d = this.data, i = this.hover, L = this.L;
      if (!d || i < 0 || !this.o.tooltip) { this.hideTip(); return; }
      const tip = this.tip;
      tip.textContent = '';
      for (const part of this.o.tooltip(d, i)) {
        const div = document.createElement('div');
        div.className = part.cls || '';
        if (part.key) {
          const k = document.createElement('span');
          k.className = 'key';
          if (part.color) k.style.background = part.color;
          div.appendChild(k);
        }
        div.appendChild(document.createTextNode(part.text));
        tip.appendChild(div);
      }
      tip.classList.remove('hidden');
      const x = L.x(d.t[i]);
      const tw = tip.offsetWidth, th = tip.offsetHeight;
      let left = x + 14;
      if (left + tw > this.el.clientWidth) left = x - tw - 14;
      tip.style.left = Math.max(0, left) + 'px';
      tip.style.top = (this.o.spark ? -th - 6 : L.m.t + 4) + 'px';
    }

    // Events within a few pixels of the point under the crosshair.
    eventsAt(i) {
      const d = this.data;
      if (!d || !d.events || !this.L) return [];
      const t = d.t[i], tol = (this.L.t(6) - this.L.t(0));
      return d.events.filter(e => e.t0 - tol <= t && (e.t1 === null ? d.to : e.t1) + tol >= t);
    }
  }

  HD.Plot = Plot;
})();
