/* SoundMeeter — логика панели док-панели OBS.
 *
 * Два транспорта:
 *   1) WebSocket ws://<host>/ws — состояние приходит пакетами ~30 Гц, команды
 *      уходят тем же сокетом. Основной путь.
 *   2) GET /api/state раз в 100 мс — запасной на случай, если WebSocket в CEF
 *      заблокирован. Отличается только источник кадров, отрисовка общая.
 */

'use strict';

const POLL_MS = 100;
const SEGMENTS = 26;
const PEAK_FLOOR = -60;   // дБ, нижняя граница шкалы метра
const PEAK_CEIL = 6;      // дБ, верхняя граница шкалы метра

const dot = document.getElementById('dot');
const engineBadge = document.getElementById('engine');
const list = document.getElementById('channels');
const empty = document.getElementById('empty');

/** Рампа цвета сегмента метра: зелёный → жёлтый → оранжевый → красный.
 *  Дублирует SegmentedMeter.cs, чтобы метр выглядел так же, как в приложении. */
const RAMP = [
    [0.00, 0x22, 0xB1, 0x4C],
    [0.50, 0x9E, 0xCB, 0x2D],
    [0.74, 0xF8, 0xE7, 0x1C],
    [0.88, 0xF5, 0x8C, 0x00],
    [1.00, 0xED, 0x1C, 0x24]
];

function rampColor(t) {
    t = Math.min(1, Math.max(0, t));
    for (let i = 1; i < RAMP.length; i++) {
        if (t <= RAMP[i][0]) {
            const span = RAMP[i][0] - RAMP[i - 1][0];
            const p = span <= 0 ? 0 : (t - RAMP[i - 1][0]) / span;
            const a = RAMP[i - 1], b = RAMP[i];
            return [
                Math.round(a[1] + (b[1] - a[1]) * p),
                Math.round(a[2] + (b[2] - a[2]) * p),
                Math.round(a[3] + (b[3] - a[3]) * p)
            ];
        }
    }
    const last = RAMP[RAMP.length - 1];
    return [last[1], last[2], last[3]];
}

/** Сколько сегментов горит: та же логарифмическая шкала, что в SegmentedMeter. */
function litCount(level) {
    if (!(level > 0.0001)) return 0;
    const db = 20 * Math.log10(level);
    const norm = Math.min(1, Math.max(0, (db - PEAK_FLOOR) / (PEAK_CEIL - PEAK_FLOOR)));
    return Math.max(0, Math.min(SEGMENTS, Math.ceil(norm * SEGMENTS)));
}

class Meter {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.level = -1;
        this.resize();
    }

    resize() {
        const dpr = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.round(this.canvas.clientWidth * dpr));
        const h = Math.max(1, Math.round(this.canvas.clientHeight * dpr));
        if (this.canvas.width !== w || this.canvas.height !== h) {
            this.canvas.width = w;
            this.canvas.height = h;
        }
    }

    /** Перерисовка только при изменении уровня: иначе 30 Гц пустой работы. */
    update(level) {
        if (Math.abs(level - this.level) < 0.002) return;
        this.level = level;
        this.resize();

        const ctx = this.ctx;
        const w = this.canvas.width, h = this.canvas.height;
        ctx.clearRect(0, 0, w, h);

        const gap = 1;
        const cellW = (w - gap * (SEGMENTS - 1)) / SEGMENTS;
        const cellH = h;
        const lit = litCount(level);

        for (let i = 0; i < SEGMENTS; i++) {
            const x = i * (cellW + gap);
            if (i < lit) {
                const rgb = rampColor(i / (SEGMENTS - 1));
                ctx.fillStyle = `rgb(${rgb[0]},${rgb[1]},${rgb[2]})`;
                ctx.fillRect(x, 0, cellW, cellH);
                if (i === lit - 1) {
                    ctx.fillStyle = 'rgba(255,255,255,0.35)';
                    ctx.fillRect(x, 0, cellW, cellH);
                }
            } else {
                ctx.fillStyle = '#222';
                ctx.fillRect(x, 0, cellW, cellH);
            }
        }
    }
}

const cards = new Map();   // id -> {root, meter, db, slider, mute, solo, drag}

function buildCard(ch, min, max) {
    const root = document.createElement('div');
    root.className = 'ch';
    if (!ch.a) root.classList.add('offline');

    const name = document.createElement('div');
    name.className = 'name';
    const kind = document.createElement('span');
    kind.className = ch.k === 'out' ? 'kind out' : 'kind';
    kind.textContent = ch.k === 'out' ? 'OUT' : 'IN';
    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = ch.n || ch.d || '—';
    name.append(kind, label);

    const canvas = document.createElement('canvas');
    const db = document.createElement('div');
    db.className = 'db';

    const slider = document.createElement('input');
    slider.type = 'range';
    slider.min = String(min);
    slider.max = String(max);
    slider.step = '0.5';
    slider.title = 'Volume (double-click = 0 dB)';

    const btns = document.createElement('div');
    btns.className = 'btns';
    const mute = document.createElement('button');
    mute.className = 'mute';
    mute.textContent = 'MUTE';
    const solo = document.createElement('button');
    solo.className = 'solo';
    solo.textContent = 'SOLO';
    btns.append(mute, solo);

    root.append(name, canvas, db, slider, btns);

    const card = { root, meter: new Meter(canvas), db, slider, mute, solo, drag: false };

    const send = (op, value) => sendCommand(op, ch.k, ch.id, value);

    slider.addEventListener('input', () => {
        const v = parseFloat(slider.value);
        db.textContent = fmtDb(v);
        send('vol', v);
    });
    slider.addEventListener('pointerdown', () => { card.drag = true; });
    slider.addEventListener('pointerup', () => { card.drag = false; });
    slider.addEventListener('dblclick', () => {
        slider.value = '0';
        db.textContent = fmtDb(0);
        send('vol', 0);
    });

    mute.addEventListener('click', () => send('mute', !mute.classList.contains('on')));
    solo.addEventListener('click', () => send('solo', !solo.classList.contains('on')));

    root.title = (ch.d || ch.n || '') + (ch.n && ch.d && ch.n !== ch.d ? ' — ' + ch.n : '');
    return card;
}

function fmtDb(v) {
    return (v > 0 ? '+' : '') + v.toFixed(1) + ' dB';
}

function render(state) {
    const min = state.min ?? -60;
    const max = state.max ?? 12;
    const list_ = state.ch || [];

    engineBadge.hidden = !!state.eng;

    // Набор каналов изменился (добавили/удалили стрип или сменили выбор в
    // настройках) — пересобираем карточки, иначе обновляем значения на месте.
    const same = list_.length === cards.size && list_.every(c => cards.has(c.id));
    if (!same) {
        list.textContent = '';
        cards.clear();
        for (const ch of list_) {
            const card = buildCard(ch, min, max);
            cards.set(ch.id, card);
            list.appendChild(card.root);
        }
    }

    for (const ch of list_) {
        const card = cards.get(ch.id);
        if (!card) continue;

        card.meter.update(ch.p || 0);
        card.db.textContent = fmtDb(ch.db || 0);
        card.db.classList.toggle('hot', (ch.db || 0) > 0);

        // Пока пользователь тянет фейдер, его значение не затираем серверным.
        if (!card.drag) card.slider.value = String(ch.db || 0);
        card.slider.disabled = !ch.a;

        card.mute.classList.toggle('on', !!ch.m);
        card.solo.classList.toggle('on', !!ch.s);
        card.root.classList.toggle('offline', !ch.a);
    }

    empty.hidden = list_.length > 0;
}

let socket = null;
let retryMs = 500;
let pollTimer = null;
let lastState = null;

function setStatus(cls) {
    dot.className = 'dot' + (cls ? ' ' + cls : '');
}

function connect() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    let ws;
    try {
        ws = new WebSocket(proto + '//' + location.host + '/ws');
    } catch (e) {
        scheduleReconnect();
        return;
    }

    socket = ws;

    ws.onopen = () => {
        retryMs = 500;
        stopPolling();
        setStatus('online');
    };

    ws.onmessage = (ev) => {
        let data;
        try {
            data = JSON.parse(ev.data);
        } catch (e) {
            return;
        }
        lastState = data;
        render(data);
    };

    ws.onerror = () => setStatus(null);

    ws.onclose = () => {
        if (socket === ws) socket = null;
        setStatus(null);
        // Пока приложение закрыто, грузим последнее состояние опросом: метры
        // замирают, но панель не показывает мусор и оживает сама.
        startPolling();
        scheduleReconnect();
    };
}

function scheduleReconnect() {
    setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 5000);
}

function startPolling() {
    if (pollTimer) return;
    pollTimer = setInterval(async () => {
        if (socket && socket.readyState === WebSocket.OPEN) return;
        try {
            const res = await fetch('/api/state', { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();
            lastState = data;
            setStatus('polling');
            render(data);
        } catch (e) {
            setStatus(null);
        }
    }, POLL_MS);
}

function stopPolling() {
    if (!pollTimer) return;
    clearInterval(pollTimer);
    pollTimer = null;
}

function sendCommand(op, kind, id, value) {
    if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ op, id, kind, v: value }));
    }
}

window.addEventListener('resize', () => {
    for (const card of cards.values()) card.meter.resize();
});

// Первое состояние: если WebSocket не поднимется, его заменит опрос.
connect();
if (!lastState) startPolling();
