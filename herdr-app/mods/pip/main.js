// Pip: a little pixel critter living under the agents list, keeping an eye on them.
//
//   an agent needs you     comes to the glass and knocks, saying who
//   an agent is done       dances under confetti until you look at it
//   anything working       gets to work, and every minute or so walks off to do
//                          something else: types at a tiny laptop (sparks off the
//                          screen), reads a book, stacks a pyramid of blocks, stalks
//                          and pounces on a bug, or thinks until a lightbulb goes on.
//                          All of it goes faster with more agents at it.
//   nothing going on       wanders, blinks, watches you; and every half-minute or so
//                          kicks a ball about, chases a butterfly, smells a flower,
//                          digs up a gem or a star, or yawns and stretches
//   you are away           naps (screen locked, or no input for 5 minutes),
//                          and says hello when you are back
//
// Click it to go to the agent that wants you; otherwise clicking pets it.
// `herdr-app do pip.demo-knock` (demo-party, demo-work, demo-busy, demo-sleep, demo-off)
// shows a mood without waiting for an agent (demo-idle: none at all), `pip.work-read`
// (work-type, work-build, work-bugs, work-think) picks what it does while working, and
// `pip.idle-ball` (idle-butterfly, idle-flower, idle-dig, idle-yawn) what it does idle.
//
// Herdr's own agent status drives all of it (`agents()`; "done" turns "idle"
// once you look), so nothing has to tell Pip anything. It is drawn from
// rectangles in the theme's colours, so it follows the theme, and every motion
// is native (.loop / .transition): the timers below only make decisions.

import { View, div } from "gpui-kit";
import { h_flex, v_flex } from "gpui-base";
import { agents, run, theme, settings, exec, readTextFile, nextCommand, home, window as appWindow } from "herdr";

// ---- the sprite --------------------------------------------------------------
// '.' empty, 'b' body, 'c' cheek / inner ear. The face is carved in as 'o'
// (empty too), so the eyes show whatever is behind Pip.
const BODY = [
  "..bb......bb..",
  "..bcb....bcb..",
  "..bbbbbbbbbb..",
  ".bbbbbbbbbbbb.",
  ".bbbbbbbbbbbb.",
  ".bbbbbbbbbbbb.",
  ".bbbbbbbbbbbb.",
  ".bbbbbbbbbbbb.",
  "..bbbbbbbbbb..",
  "...bb....bb...",
];
const PAW = ["b.b.b", ".....", ".bbb.", ".bbb."];
// A low desk and an open laptop seen side on, its lit screen facing Pip: 'k' the
// keyboard deck and bezel, 'd' the back of the lid, 'g' the screen, 't' the desk.
// Its bottom row stands on the grass and its keyboard sits at Pip's belly, so
// the paws (WORK_PAWS) rest on the keys rather than reaching across a gap.
const DESK = [
  "........kd",
  ".......ggd",
  ".......ggd",
  "......ggd.",
  "kkkkkkkd..",
  "tttttttttt",
  "..t....t..",
];
const DESK_CHASSIS = DESK.map((r) => r.replaceAll("g", "."));
const DESK_SCREEN = DESK.map((r) => r.replace(/[^g]/g, "."));
const SW = BODY[0].length;
const SH = BODY.length;
const DESK_W = DESK[0].length;
const DESK_H = DESK.length;
// The desk overlaps Pip's right-hand column, which is empty while it faces the desk
// (the tail is on the other side), so the keyboard starts right against Pip.
const DESK_TUCK = 1;
// The two typing paws, in Pip's own pixels: just above the keyboard row.
const WORK_PAWS = [{ col: 13, row: 6, period: 220 }, { col: 15, row: 6, period: 280 }];

// ---- the other things it does while agents work ------------------------------------
// Every 40-75 s it walks somewhere else and does something different (`startActivity`).
const ACTIVITIES = ["type", "read", "build", "bugs", "think"];
// read: a book held up with its cover to you, Pip peeking over the top, a paw on each
// side ('b'). 'k' cover, 'K' spine, 'g' the title. As wide as Pip and drawn from its
// row 5 down, so only the top row of the eyes shows, which makes them look down at it.
const BOOK = [
  "..kkkkkkkkkk..",
  ".kkkkkKKkkkkk.",
  "bkggkkKKkkkkkb",
  ".kkkkkKKkkkkk.",
  ".kkkkkKKkkkkk.",
];
const BOOK_ROW = 5;
// build: a 3-2-1 pyramid beside it, one block at a time, in the order of the list.
// Units are blocks of BLOCK pixels; y counts up from the grass.
const BLOCK = 3;
const STACK = [[0, 0], [1, 0], [2, 0], [0.5, 1], [1.5, 1], [1, 2]];
const STACK_W = 3 * BLOCK;
// bugs: a ladybug on the grass, head to the right ('f' head and legs, 'e' spot).
const BUG = [".rrr..", "rerrrf", ".f.f.."];
const BUG_W = BUG[0].length;
const BUG_H = BUG.length;
// think: a cloud up and to the right of its head, with three dots twinkling in it,
// until a lightbulb goes on over its head instead. 'q' fill, 'Q' edge.
const CLOUD = [
  "..QQQQQQ...",
  ".QqqqqqqQQ.",
  "QqqqqqqqqqQ",
  "QqqqqqqqqqQ",
  ".QqqqqqqqQ.",
  "..QQQQQQQ..",
];
const CLOUD_COL = 14;   // the cloud's left edge, in Pip's pixels
const BULB = [".yyy.", "yyyyy", "yyyyy", "yyyyy", ".yyy.", ".sss.", "..s.."];

// ---- what it does when nothing is going on -----------------------------------------
// It wanders, and every 20-45 s does one of these, start to finish (`startPastime`).
const PASTIMES = ["ball", "butterfly", "flower", "dig", "yawn"];
// ball: kicked about the grass a few times, then off the edge. A shine pixel slides
// across it while it rolls, which is all the spin there is.
const BALL = [".rr.", "rrrr", "rrrr", ".rr."];
const BALL_W = BALL[0].length;
// butterfly: 2x2 upper wings and 1x1 lower ones either side of a 1x3 body; the upper
// wings flap by their height. Upper wings alone read as a propeller.
const FLY_W = 5;
// flower: grows out of the grass beside it, a stem of STEM_H and this on top.
const FLOWER = [".p.p.", "ppypp", ".ppp."];   // a plus shape read as a grave marker
const STEM_H = 4;
// dig: what turns up in the hole.
const TREASURES = {
  gem: [".ggg.", "ggggg", ".ggg.", "..g.."],
  star: ["..g..", ".ggg.", "ggggg", ".g.g."],
};

function sprite({ eyes = "open", mouth = "small", look = 0, facing = 1, wag = false, lying = false, arms = false }) {
  const g = BODY.map((r) => r.split(""));
  const hole = (r, c) => {
    if (g[r] && g[r][c] && g[r][c] !== ".") g[r][c] = "o";
  };
  const er = 4, mr = 6, m0 = 6 + look, m1 = m0 + 1;
  for (const c of [2 + look, 3 + look, 10 + look, 11 + look]) if (g[mr][c] === "b") g[mr][c] = "c";
  for (const c of [4 + look, 9 + look]) {
    if (eyes === "open") { hole(er, c); hole(er + 1, c); }
    else if (eyes === "blink") hole(er + 1, c);
    else if (eyes === "closed") { hole(er + 1, c - 1); hole(er + 1, c); hole(er + 1, c + 1); }
    else if (eyes === "happy") { hole(er, c); hole(er + 1, c - 1); hole(er + 1, c + 1); }
    else if (eyes === "wide") { hole(er - 1, c); hole(er, c); hole(er + 1, c); }
    else if (eyes === "up") { hole(er - 1, c); hole(er, c); }
  }
  if (mouth === "small") { hole(mr, m0); hole(mr, m1); }
  else if (mouth === "smile") { hole(mr, m0 - 1); hole(mr, m1 + 1); hole(mr + 1, m0); hole(mr + 1, m1); }
  else if (mouth === "open") { hole(mr, m0); hole(mr, m1); hole(mr + 1, m0); hole(mr + 1, m1); }
  const tail = facing > 0 ? 0 : SW - 1;
  for (const r of wag ? [4, 5] : [5, 6]) g[r][tail] = "b";
  // Paws in the air for a stretch: up from the shoulders, either side of the head.
  // Not with a wag, which puts the tail in the same column and makes one long bar.
  if (arms) for (const c of [0, SW - 1]) for (const r of [1, 2, 3]) g[r][c] = "b";
  return lying ? g.slice(0, -1) : g;
}

// A grid as rectangles: one per run of a colour in a row, not one per pixel.
function pixels(grid, colors, px, key) {
  const out = [];
  grid.forEach((row, r) => {
    let c = 0;
    while (c < row.length) {
      const ch = row[c];
      if (ch === "." || ch === "o") { c++; continue; }
      let e = c;
      while (e < row.length && row[e] === ch) e++;
      out.push(div().id(`${key}-${r}-${c}`).absolute().left(c * px).top(r * px).w((e - c) * px).h(px).bg(colors[ch]));
      c = e;
    }
  });
  return out;
}

// ---- small helpers -------------------------------------------------------------
const rand = (a, b) => a + Math.random() * (b - a);
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const hex2 = (n) => Number(n).toString(16).padStart(2, "0");
// "Claude Code" -> "Claude": the bubble is narrow.
const who = (a) => (a.program?.name ?? "agent").split(" ")[0];
const unique = (list) => [...new Set(list)];

const BLOCK_H = 112;   // keep in step with the "pip" block's px in the layout

export default class Pip extends View {
  init(_props, cx) {
    this.w = 268;
    this.h = BLOCK_H;
    this.x = null;               // left edge of the critter, in px
    this.facing = 1;
    this.walkUntil = 0;
    this.walkMs = 0;
    this.nextMove = Date.now() + 2500;
    this.blink = false;
    this.nextBlink = Date.now() + 3000;
    this.bubble = null;          // { title, sub, color, until }
    this.petUntil = 0;
    this.hearts = [];            // one-shot particles: { id, x, y, phase }
    this.confetti = [];
    this.sparks = [];
    this.burstAt = 0;
    this.sparkAt = 0;
    this.working = 0;            // how many agents are working, for the typing pace
    this.breakUntil = 0;         // a pause from typing: glancing at you, or a stretch
    this.breakKind = null;
    this.nextBreak = 0;
    this.act = null;             // what it is doing while agents work: { kind, until, ... }
    this.ids = 0;
    this.mode = null;
    this.away = false;
    this.awaySince = 0;
    this.watch = null;           // { idle, guard, key } once known

    if (!localStorage.getItem("met")) {
      localStorage.setItem("met", "1");
      this.say("hi! I'm Pip", "I'll keep an eye on your agents", null, 5000);
    }
    cx.timer.every(500, (cx) => this.tick(cx));
    cx.spawn(async (cx) => this.setupAway(cx));
    cx.spawn(async (cx) => {
      for (;;) {
        const c = await nextCommand();
        if (c.startsWith("demo-")) {
          this.demo = c === "demo-off" ? null : c.slice(5);
          cx.notify();
        } else if (c.startsWith("work-") && ACTIVITIES.includes(c.slice(5))) {
          // `herdr-app do pip.work-read` etc.: that activity now, or as soon as work starts.
          if (this.mode === "work") this.startActivity(Date.now(), c.slice(5));
          else this.wantAct = c.slice(5);
          cx.notify();
        } else if (c.startsWith("idle-") && PASTIMES.includes(c.slice(5))) {
          // `herdr-app do pip.idle-ball` etc.: the same for idle.
          if (this.mode === "idle") this.startPastime(Date.now(), c.slice(5));
          else this.wantPastime = c.slice(5);
          cx.notify();
        }
      }
    });
  }

  say(title, sub = null, color = null, ms = 3000) {
    this.bubble = { title, sub, color, until: Date.now() + ms };
  }

  // ---- away: screen locked, or no input for 5 minutes ----------------------------

  async setupAway(cx) {
    if (appWindow().platform === "windows") {
      // WarrenTool's tray app (WarrenTool repo, src\WarrenTool.cs: AwayMarker) keeps this
      // marker while the PC is locked, the screensaver is running, or nothing has been
      // touched for 5 minutes, and clears it when it starts. Folder Redirection never moves
      // AppData\Local, so the profile is enough to find it with no program to start.
      this.watch = { idle: `${home()}\\AppData\\Local\\WarrenTool\\pip.away`, guard: null };
    } else {
      try {
        const r = await exec("sh", ["-c", 'printf "%s\\n%s" "$XDG_RUNTIME_DIR" "$WAYLAND_DISPLAY"']);
        const [rt, wd] = r.stdout.split("\n");
        if (!rt) return;
        // hypridle.conf keeps this marker while you have not touched anything for 5 minutes.
        // lock.sh holds an flock on the guard for the whole of a lock (its hyprlock
        // fallback too); the kernel drops it if the script dies, so nothing goes stale.
        this.watch = { idle: `${rt}/pip.idle`, guard: `${rt}/aquarium-lock-${wd || "wayland"}.guard`, key: null, keyAt: 0 };
      } catch {
        return;
      }
    }
    cx.timer.every(2000, (cx) => cx.spawn(async (cx) => this.checkAway(cx)));
  }

  // Locked is read from /proc/locks -- NEVER by taking the flock. A probe holding
  // it at the instant lock.sh starts makes lock.sh believe a lock is already up,
  // and it exits without locking the screen.
  async locked() {
    const w = this.watch;
    const now = Date.now();
    if (!w.key || now - w.keyAt > 60000) {
      w.keyAt = now;
      try {
        const s = await exec("stat", ["-c", "%Hd %Ld %i", w.guard]);
        const [maj, min, ino] = s.stdout.trim().split(" ");
        w.key = s.code === 0 ? `${hex2(maj)}:${hex2(min)}:${ino} ` : null;
      } catch {
        w.key = null;
      }
    }
    if (!w.key) return false;
    let locks = await readTextFile("/proc/locks");
    if (!locks) locks = (await exec("cat", ["/proc/locks"])).stdout;
    return locks.split("\n").some((l) => l.includes(" FLOCK ") && l.includes(w.key));
  }

  async checkAway(cx) {
    let away;
    try {
      // On Windows the one marker covers the lock too: there is no guard.
      away = (await readTextFile(this.watch.idle)) !== null || (this.watch.guard !== null && (await this.locked()));
    } catch {
      return;
    }
    if (away === this.away) return;
    this.away = away;
    const now = Date.now();
    if (away) {
      this.awaySince = now;
      this.bubble = null;
    } else {
      // Back. Anything an agent did meanwhile is still on show (done stays done
      // until you look), so this is only the hello.
      const gone = now - this.awaySince;
      if (gone > 5000) this.say(gone > 15 * 60000 ? "missed you!" : pick(["welcome back!", "hi again!"]));
      this.hop(now);
    }
    cx.notify();
  }

  // ---- decisions ------------------------------------------------------------------

  hop(now) {
    this.petUntil = Math.max(this.petUntil, now + 700);
  }

  tick(cx) {
    const now = Date.now();
    let dirty = false;
    const sw = SW * this.px();

    if (this.bubble && now > this.bubble.until) { this.bubble = null; dirty = true; }
    if (this.petUntil && now > this.petUntil) { this.petUntil = 0; dirty = true; }
    if (this.walkUntil && now > this.walkUntil) { this.walkUntil = 0; dirty = true; }

    // Spent particles have already faded to nothing, so dropping them waits for the
    // next redraw instead of asking for one. Herdr redraws the whole window for a
    // mod, and while an agent works this was a third of Pip's redraws.
    this.hearts = this.hearts.filter((p) => now - p.born < 1600);
    this.confetti = this.confetti.filter((p) => now - p.born < 7000);
    this.sparks = this.sparks.filter((p) => now - p.born < 1400);

    if (this.mode === "party") {
      // A burst of confetti every few seconds, and a turn on the spot each second.
      if (now >= this.burstAt) {
        this.burst(cx, now, this.confetti.length ? 14 : 34);
        this.burstAt = now + 3500;
      }
      if (now >= (this.turnAt ?? 0)) {
        this.facing = -this.facing;
        this.turnAt = now + 1000;
      }
      dirty = true;
    } else if (this.mode === "work" && this.act) {
      const a = this.act;
      const arrived = now >= this.walkUntil;
      // Time for something else, once it is not mid-walk, mid-break or mid-pounce.
      if (now >= a.until && arrived && !this.breakUntil && !a.pouncing) {
        this.startActivity(now);
        dirty = true;
      } else if (arrived || a.kind === "bugs") {
        dirty = this.workTick(cx, now, a) || dirty;
      }
    } else if (this.mode === "idle" && this.act && this.act.kind !== "wander") {
      dirty = this.pastimeTick(cx, now, this.act) || dirty;
    } else if (this.mode === "idle" && this.act && now >= this.act.until && now >= this.walkUntil) {
      this.startPastime(now);
      dirty = true;
    } else if (this.mode === "idle" && now >= this.nextMove && this.x !== null) {
      // Wander: walk somewhere else, or turn and stand.
      const room = Math.max(0, this.w - sw - 8);
      if (Math.random() < 0.7 && room > 20) {
        let tx = rand(4, 4 + room);
        if (Math.abs(tx - this.x) < 24) tx = (tx + room / 2) % room + 4;
        this.walkMs = Math.max(400, (Math.abs(tx - this.x) / 32) * 1000);
        this.facing = tx > this.x ? 1 : -1;
        this.x = tx;
        this.walkUntil = now + this.walkMs;
        this.nextMove = this.walkUntil + rand(1500, 7000);
      } else {
        this.facing = -this.facing;
        this.nextMove = now + rand(2000, 5000);
      }
      dirty = true;
    }

    if (now >= this.nextBlink && this.mode !== "sleep") {
      this.blink = true;
      this.nextBlink = now + rand(2500, 6500);
      cx.timer.after(140, (cx) => { this.blink = false; cx.notify(); });
      dirty = true;
    }
    if (dirty) cx.notify();
  }

  burst(cx, now, n) {
    const t = theme();
    const colors = [t.ansi1, t.ansi2, t.ansi3, t.ansi4, t.ansi5, t.ansi6].filter(Boolean);
    const ground = this.ground();
    const fresh = [];
    for (let i = 0; i < n; i++) {
      const x = rand(0, this.w);
      fresh.push({
        id: ++this.ids, born: now, phase: 0, color: pick(colors),
        x, y: rand(-30, -4), x2: x + rand(-18, 18), y2: ground - rand(1, 4), ms: rand(1300, 2400),
      });
    }
    this.confetti.push(...fresh);
    // Drawn once where they start, then given their targets: .transition does the fall.
    cx.timer.after(40, (cx) => { fresh.forEach((p) => (p.phase = 1)); cx.notify(); });
  }

  // One-shot particles that drift from (x, y) to (x2, y2) and fade: sparks off the
  // laptop, a page turning, a squashed bug. Drawn where they start, then given their
  // targets 40 ms later for .transition to carry them there.
  puff(cx, now, list) {
    const fresh = list.map((p) => ({ id: ++this.ids, born: now, phase: 0, w: 3, h: 3, ms: 1000, ...p }));
    this.sparks.push(...fresh);
    cx.timer.after(40, (cx) => { fresh.forEach((p) => (p.phase = 1)); cx.notify(); });
  }

  spark(cx, now) {
    const t = theme();
    const px = this.px();
    const colors = [t.working ?? t.ansi3, t.accent ?? t.ansi4, t.ansi6, t.ansi5].filter(Boolean);
    // Off the top edge of the screen.
    const screenX = this.deskX() + 7 * px;
    const screenY = this.deskY();
    const n = Math.random() < (this.working > 1 ? 0.7 : 0.4) ? 2 : 1;
    this.puff(cx, now, Array.from({ length: n }, () => {
      const x = screenX + rand(-px, px * 1.5);
      const y = screenY + rand(-px, px);
      const size = Math.random() < 0.5 ? 3 : 2;
      return { color: pick(colors), x, y, x2: x + rand(-10, 10), y2: y - rand(22, 38), w: size, h: size, ms: rand(850, 1250) };
    }));
  }

  // ---- work: what it does while agents work ------------------------------------------

  // Faster with more agents at it: 1, 2, 3 or more.
  pace() { return this.working >= 3 ? 0.55 : this.working === 2 ? 0.75 : 1; }

  walkTo(x, now, speed) {
    const dist = Math.abs(x - this.x);
    if (dist < 1) return;
    this.facing = x > this.x ? 1 : -1;
    this.walkMs = Math.max(300, (dist / speed) * 1000);
    this.walkUntil = now + this.walkMs;
    this.x = x;
  }

  // How far an activity reaches to the right of Pip's left edge, in its pixels.
  span(kind) {
    if (kind === "type") return SW - DESK_TUCK + DESK_W;
    if (kind === "build") return SW + 1 + STACK_W;
    if (kind === "think") return CLOUD_COL + CLOUD[0].length;
    return SW;
  }

  // Where Pip stands for an activity: the desk is in the middle, anything else is
  // anywhere that leaves room for its prop.
  spotFor(kind) {
    if (kind === "type") return this.workX();
    return rand(0, Math.max(0, this.w - this.span(kind) * this.px()));
  }

  // Something new, never the same thing twice running, and a walk over to do it.
  startActivity(now, kind = null) {
    kind ??= pick(ACTIVITIES.filter((k) => k !== this.act?.kind));
    const a = { kind, until: now + rand(40000, 75000), nextAt: now + rand(1500, 2500) };
    if (kind === "type") { this.breakUntil = 0; this.nextBreak = now + rand(20000, 40000); this.sparkAt = 0; }
    if (kind === "read") Object.assign(a, { look: -1, lookAt: 0 });
    if (kind === "build") Object.assign(a, { blocks: [], cheerUntil: 0, clearAt: 0 });
    if (kind === "bugs") Object.assign(a, { bug: null, moves: 0, pounceAfter: 0, pouncing: false });
    if (kind === "think") Object.assign(a, { nextAt: now + rand(6000, 10000), ahaUntil: 0 });
    this.act = a;
    this.walkTo(this.spotFor(kind), now, 60);
  }

  // One tick of the current activity. Returns whether anything changed.
  workTick(cx, now, a) {
    const px = this.px();
    const t = theme();
    switch (a.kind) {
      case "type": {
        // A break every half a minute or so: a look round at you, or a stretch with its
        // paws in the air. No sparks while its paws are off the keys.
        let dirty = false;
        if (this.breakUntil && now >= this.breakUntil) {
          this.breakUntil = 0;
          this.nextBreak = now + rand(25000, 50000);
          dirty = true;
        } else if (!this.breakUntil && now >= this.nextBreak) {
          this.breakKind = Math.random() < 0.6 ? "glance" : "stretch";
          this.breakUntil = now + (this.breakKind === "stretch" ? 1800 : rand(2200, 3500));
          dirty = true;
        }
        if (!this.breakUntil && now >= this.sparkAt) {
          this.spark(cx, now);
          this.sparkAt = now + rand(900, 1500);
          dirty = true;
        }
        return dirty;
      }
      case "read": {
        // Turn the page: a sheet flicks over the top from the spine, and the eyes go
        // back to the left; halfway down the page they have moved to the right.
        if (now >= a.nextAt) {
          const x = this.x + 6 * px;
          const y = this.baseY() + 3 * px;
          this.puff(cx, now, [{ color: t.foreground ?? "#cdd6f4", x, y, x2: x - 4 * px, y2: y - px, w: px, h: 2 * px, ms: 450 }]);
          a.look = -1;
          a.lookAt = now + rand(1800, 2600) * this.pace();
          a.nextAt = now + rand(4000, 6500) * this.pace();
          return true;
        }
        if (a.lookAt && now >= a.lookAt) { a.look = 1; a.lookAt = 0; return true; }
        return false;
      }
      case "build": {
        // One block at a time, a hop for each; a cheer at the top, then they fade and
        // it starts again.
        if (a.clearAt && now >= a.clearAt) { a.blocks = []; a.clearAt = 0; a.nextAt = now + 1200; return true; }
        if (a.cheerUntil && now >= a.cheerUntil) {
          a.cheerUntil = 0;
          a.blocks.forEach((b) => (b.fading = true));
          a.clearAt = now + 700;
          return true;
        }
        if (a.cheerUntil || a.clearAt || now < a.nextAt) return false;
        if (a.blocks.length === STACK.length) { a.cheerUntil = now + 1800; return true; }
        // Never the colour of a block it touches, or the two read as one shape. Five
        // colours and at most three neighbours always leave a choice; not Pip's pink.
        const slot = a.blocks.length;
        const [bx, by] = STACK[slot];
        const near = a.blocks.filter((o) => Math.abs(STACK[o.slot][0] - bx) <= 1 && Math.abs(STACK[o.slot][1] - by) <= 1)
          .map((o) => o.color);
        const colors = unique([t.ansi1, t.ansi2, t.ansi3, t.ansi4, t.ansi6].filter(Boolean));
        const free = colors.filter((c) => !near.includes(c));
        const b = { id: ++this.ids, slot, color: pick(free.length ? free : colors), phase: 0, fading: false };
        a.blocks.push(b);
        cx.timer.after(40, (cx) => { b.phase = 1; cx.notify(); });
        this.petUntil = Math.max(this.petUntil, now + 450);
        a.nextAt = now + rand(2000, 3200) * this.pace();
        return true;
      }
      case "bugs":
        return this.huntTick(cx, now, a);
      case "think": {
        // Eyes up at the cloud for a while, then a lightbulb and a happy hop.
        if (a.ahaUntil && now >= a.ahaUntil) { a.ahaUntil = 0; a.nextAt = now + rand(7000, 12000) * this.pace(); return true; }
        if (!a.ahaUntil && now >= a.nextAt) {
          a.ahaUntil = now + 2200;
          this.petUntil = Math.max(this.petUntil, now + 600);
          return true;
        }
        return false;
      }
    }
    return false;
  }

  // bugs: a ladybug comes in from the side and wanders the grass. Pip creeps after it,
  // crouched, keeping beside it; after a few moves it pounces, and the bug is gone.
  huntTick(cx, now, a) {
    const px = this.px();
    const sw = SW * px;
    const bw = BUG_W * px;
    if (a.pouncing) return false;
    if (a.bug && a.bug.until && now >= a.bug.until) { a.bug.until = 0; return true; }   // stops scuttling
    if (now < a.nextAt || now < this.walkUntil) return false;
    if (!a.bug) {
      // Comes in from the side Pip is further from.
      const fromLeft = this.x > this.w / 2;
      a.bug = { id: ++this.ids, x: fromLeft ? -bw - 2 : this.w + 2, dir: fromLeft ? 1 : -1, ms: 0, until: 0 };
      a.moves = 0;
      a.pounceAfter = 3 + Math.floor(Math.random() * 3);
      a.nextAt = now + 100;
      return true;
    }
    if (a.moves >= a.pounceAfter) {
      // Land on it, then squash it once there.
      const target = Math.min(Math.max(0, a.bug.x + bw / 2 - sw / 2), this.w - sw);
      this.walkTo(target, now, 160);
      const leap = Math.max(200, this.walkUntil - now);   // walkTo does nothing if already there
      this.petUntil = now + leap + 350;
      a.pouncing = true;
      const bug = a.bug;
      cx.timer.after(leap, (cx) => {
        if (this.act !== a || a.bug !== bug) return;
        this.squash(cx, Date.now(), a);
        cx.notify();
      });
      return true;
    }
    // Scuttle somewhere along the grass, mostly onwards, and Pip follows.
    const lo = 4, hi = this.w - bw - 4;
    let nx = a.bug.x + (Math.random() < 0.75 ? a.bug.dir : -a.bug.dir) * rand(18, 46);
    if (a.bug.x < lo || a.bug.x > hi) nx = a.bug.x < lo ? rand(lo + 20, lo + 60) : rand(hi - 60, hi - 20);
    nx = Math.min(Math.max(lo, nx), hi);
    a.bug.dir = nx >= a.bug.x ? 1 : -1;
    a.bug.ms = Math.max(400, (Math.abs(nx - a.bug.x) / 32) * 1000 * this.pace());
    a.bug.until = now + a.bug.ms;
    a.bug.x = nx;
    // Keep beside it, on whichever side it is already on.
    const left = this.x + sw / 2 < nx + bw / 2;
    const stalk = left ? nx - sw - px : nx + bw + px;
    this.walkTo(Math.min(Math.max(0, stalk), this.w - sw), now, 32 / this.pace());
    a.moves++;
    a.nextAt = now + Math.max(a.bug.ms, this.walkMs) + rand(500, 1300);
    return true;
  }

  squash(cx, now, a) {
    const t = theme();
    const px = this.px();
    const x0 = a.bug.x + (BUG_W * px) / 2;
    const y0 = this.ground() - 2 * px;
    const colors = [t.ansi1, t.ansi3, t.foreground].filter(Boolean);
    this.puff(cx, now, Array.from({ length: 10 }, () => ({
      color: pick(colors), x: x0, y: y0, x2: x0 + rand(-22, 22), y2: y0 - rand(6, 26), w: 3, h: 3, ms: rand(600, 900),
    })));
    if (Math.random() < 0.5) this.say(pick(["got it!", "squashed!", "bug fixed!"]), null, null, 1500);
    a.bug = null;
    a.pouncing = false;
    a.nextAt = now + rand(1500, 3000);
  }

  pet(cx, now) {
    this.petUntil = now + 1400;
    this.heartsUp(cx, now, (this.x ?? 0) + (SW * this.px()) / 2, this.baseY() - 6);
    if (Math.random() < 0.5) this.say(pick(["hehe", "♥", "*wiggles*"]), null, null, 1500);
  }

  // Three hearts floating up from around (x0, top).
  heartsUp(cx, now, x0, top) {
    const fresh = [0, 1, 2].map((i) => ({ id: ++this.ids, born: now, phase: 0, x: x0 + rand(-14, 10), y: top + i * 2 }));
    this.hearts.push(...fresh);
    cx.timer.after(40, (cx) => { fresh.forEach((p) => (p.phase = 1)); cx.notify(); });
  }

  // ---- idle: what it does when nothing is going on ---------------------------------------

  startPastime(now, kind = null) {
    kind ??= pick(PASTIMES.filter((k) => k !== this.lastPastime));
    this.lastPastime = kind;
    const px = this.px();
    const sw = SW * px;
    const t = theme();
    const a = { kind, id: ++this.ids, step: 0, nextAt: now + 400 };
    if (kind === "ball") {
      // Rolls in from the side Pip is further from, to somewhere in the middle.
      const fromLeft = this.x > this.w / 2;
      a.ball = { x: fromLeft ? -BALL_W * px - 2 : this.w + 2, ms: 0, until: 0 };
      a.kicks = 0;
      a.maxKicks = 3 + Math.floor(Math.random() * 2);
    } else if (kind === "butterfly") {
      const colors = [t.ansi3, t.ansi4, t.ansi6].filter(Boolean);
      a.fly = { x: rand(0, this.w - FLY_W * px), y: -14, ms: 0, color: pick(colors) };
      a.moves = 0;
      a.maxMoves = 5 + Math.floor(Math.random() * 4);
    } else if (kind === "flower" || kind === "dig") {
      // Somewhere with room on one side for the flower, or for the dirt to fly.
      const spot = rand(4 * px, Math.max(4 * px, this.w - sw - 4 * px));
      this.walkTo(spot, now, 40);
      a.side = spot + sw / 2 < this.w / 2 ? 1 : -1;
      a.color = pick([t.ansi1, t.ansi4, t.ansi5].filter(Boolean));   // not teal, beside a green stem
      a.treasure = pick(Object.keys(TREASURES));
    }
    this.act = a;
  }

  endPastime(now) {
    this.act = { kind: "wander", until: now + rand(20000, 45000) };
    this.nextMove = now + rand(1500, 3500);
  }

  // Each pastime is a few steps, `step` counting them and `nextAt` timing the next.
  pastimeTick(cx, now, a) {
    const px = this.px();
    const sw = SW * px;
    const t = theme();
    switch (a.kind) {
      case "yawn": {
        // A yawn, then a stretch, where it stands.
        if (now < a.nextAt || now < this.walkUntil) return false;
        if (a.step === 2) { this.endPastime(now); return true; }
        a.step++;
        a.nextAt = now + (a.step === 1 ? 1400 : 1200);
        return true;
      }
      case "flower": {
        // It grows, it blooms, Pip smells it (hearts), it fades.
        if (now < a.nextAt || now < this.walkUntil) return false;
        if (a.step === 0) {
          a.fx = a.side > 0 ? this.x + sw : this.x - FLOWER[0].length * px;
          cx.timer.after(40, (cx) => { a.grown = true; cx.notify(); });
        }
        if (a.step === 2) this.heartsUp(cx, now, a.fx + 2 * px, this.ground() - (STEM_H + 6) * px);
        if (a.step === 4) { this.endPastime(now); return true; }
        a.step++;
        a.nextAt = now + [0, 1000, 700, 2600, 700][a.step];
        if (a.step === 4) a.fading = true;
        return true;
      }
      case "dig": {
        // Crouched, dirt flying out behind; then something turns up, and it holds it up.
        if (now < this.walkUntil) return false;
        if (a.step === 1 && now < a.nextAt) {
          const back = a.side > 0 ? this.x + 2 * px : this.x + sw - 2 * px;
          const y = this.ground() - px;
          const colors = [t.dim, t.muted, t.ansi8].filter(Boolean);
          this.puff(cx, now, [0, 1].map(() => ({
            color: pick(colors), x: back, y, x2: back - a.side * rand(10, 30), y2: y - rand(8, 22), w: px * 0.75, h: px * 0.75, ms: rand(450, 650),
          })));
          return true;
        }
        if (now < a.nextAt) return false;
        if (a.step === 3) { this.endPastime(now); return true; }
        a.step++;
        if (a.step === 1) { this.facing = a.side; a.nextAt = now + 2600; }
        if (a.step === 2) {
          a.hx = a.side > 0 ? this.x + 8 * px : this.x + 2 * px;   // under its front paws
          cx.timer.after(40, (cx) => { a.raised = true; cx.notify(); });
          a.nextAt = now + 2400;
        }
        if (a.step === 3) { a.fading = true; a.nextAt = now + 700; }
        return true;
      }
      case "ball":
        return this.ballTick(now, a);
      case "butterfly":
        return this.flyTick(now, a);
    }
    return false;
  }

  // Pip kicks the ball away from itself; it bounces off the edge and rolls back to a
  // stop short of Pip, and Pip walks up and kicks it again. The last kick sends it out.
  // The sidebar is too narrow for anything that needs Pip to get round the ball: a
  // walk round went through it, and a leap over it landed on it at the edge.
  ballTick(now, a) {
    const px = this.px();
    const sw = SW * px;
    const bw = BALL_W * px;
    const b = a.ball;
    if (b.until && now >= b.until) {
      b.until = 0;
      if (a.leaving) { this.endPastime(now); return true; }
      if (a.rebound != null) {
        // Off the edge and back.
        const nx = a.rebound;
        a.rebound = null;
        b.ms = Math.max(400, (Math.abs(nx - b.x) / 80) * 1000);
        b.ease = "ease-out";
        b.until = now + b.ms;
        b.x = nx;
        a.nextAt = now + b.ms + rand(200, 600);
      }
      return true;
    }
    if (now < a.nextAt || now < this.walkUntil || b.until) return false;
    const lo = 2, hi = this.w - bw - 2;
    if (a.step === 0) {
      // Rolling in, to a stop short of Pip on the side it came from.
      a.step = 1;
      const fromLeft = b.x < 0;
      const near = fromLeft ? this.x - bw - 8 : this.x + sw + 8;
      const nx = fromLeft ? Math.max(lo, Math.min(near, rand(8, near))) : Math.min(hi, Math.max(near, rand(near, hi)));
      b.ms = Math.max(500, (Math.abs(nx - b.x) / 90) * 1000);
      b.ease = "ease-out";
      b.until = now + b.ms;
      b.x = nx;
      a.nextAt = now + b.ms + 300;
      return true;
    }
    a.dir = this.x + sw / 2 < b.x + bw / 2 ? 1 : -1;   // away from Pip
    if (!a.lined) {
      // Up to the ball, on the side it is already on.
      const behind = a.dir > 0 ? b.x - sw + px : b.x + bw - px;
      this.walkTo(Math.min(Math.max(0, behind), this.w - sw), now, 60);
      a.lined = true;
      return true;
    }
    a.kicks++;
    a.leaving = a.kicks >= a.maxKicks;
    this.facing = a.dir;
    this.petUntil = now + 450;   // a hop, for the kick
    a.lined = false;
    let nx = b.x + a.dir * rand(50, 100);
    if (a.leaving) {
      nx = a.dir > 0 ? this.w + 4 : -bw - 4;
    } else if (nx < lo || nx > hi) {
      // Into the edge, then back off it, stopping a little short of Pip.
      const wall = nx < lo ? lo : hi;
      const front = a.dir > 0 ? this.x + sw + 6 : this.x - bw - 6;   // just clear of Pip
      const back = wall - a.dir * rand(25, 70);
      const stop = a.dir > 0 ? Math.max(front, back) : Math.min(front, back);
      nx = wall;
      if ((a.dir > 0 && stop < wall) || (a.dir < 0 && stop > wall)) a.rebound = stop;
    }
    b.ms = Math.max(350, (Math.abs(nx - b.x) / 110) * 1000);
    b.ease = a.rebound != null ? "linear" : "ease-out";
    b.until = now + b.ms;
    b.x = nx;
    a.nextAt = now + b.ms + rand(200, 700);
    return true;
  }

  // The butterfly flits about above it; Pip follows underneath, jumping at it now and
  // then, until it flies off.
  flyTick(now, a) {
    const px = this.px();
    const sw = SW * px;
    const f = a.fly;
    if (a.jumpAt && now >= a.jumpAt) { a.jumpAt = 0; this.petUntil = now + 500; return true; }
    if (now < a.nextAt) return false;
    if (a.moves >= a.maxMoves) {
      if (a.leaving) { this.endPastime(now); return true; }
      a.leaving = true;
      f.x = Math.min(Math.max(0, f.x + rand(-40, 40)), this.w - FLY_W * px);
      f.y = -20;
      f.ms = 1600;
      a.nextAt = now + 1800;
      return true;
    }
    const top = this.baseY();
    f.x = rand(0, this.w - FLY_W * px);
    f.y = rand(top - 36, top - 12);   // above its head: lower, it sat on Pip's face
    f.ms = rand(900, 1500);
    this.walkTo(Math.min(Math.max(0, f.x + (FLY_W * px) / 2 - sw / 2), this.w - sw), now, 45);
    if (a.moves > 0 && Math.random() < 0.4) a.jumpAt = Math.max(now, this.walkUntil);   // a jump at it, once under it
    a.moves++;
    a.nextAt = now + f.ms + rand(300, 900);
    return true;
  }

  // ---- geometry --------------------------------------------------------------------

  px() { return this.w < 120 ? 2 : 4; }
  ground() { return this.h - 9; }                   // the grass line
  baseY() { return this.ground() - SH * this.px(); } // the critter's top when standing
  // Pip at the desk, the pair centred together. The desk is placed from wherever Pip
  // is, so a resize cannot leave the two apart.
  workX() { return Math.max(0, (this.w - (SW - DESK_TUCK + DESK_W) * this.px()) / 2); }
  deskX() { return this.x + (SW - DESK_TUCK) * this.px(); }
  deskY() { return this.baseY() + (SH - DESK_H) * this.px(); }

  // ---- render ------------------------------------------------------------------------

  // The prop for what it is doing while agents work, placed from Pip's own x.
  renderActivity(a, { t, now, px, walking, typing, colors }) {
    const out = [];
    const x = this.x;
    const top = this.baseY();
    // A prop is drawn at its spot from the moment Pip sets off, see-through, and the
    // redraw that ends the walk fades it in: it appears as Pip gets there.
    const prop = (id, left, y, w, h) => div().id(id).absolute().left(left).top(y).w(w).h(h)
      .opacity(walking ? 0 : 1).transition("opacity", { duration: 250 });
    // Twinkling all the way out: a yellow held at a fifth is khaki.
    const dot = (id, left, y, size, color, period) => div().id(id).absolute().left(left).top(y).w(size).h(size).bg(color)
      .loop("opacity", { from: 0, to: 1, period });

    switch (a.kind) {
      case "type": {
        const deskColors = {
          t: t.dim ?? "#7f849c",
          d: t.muted ?? "#a6adc8",
          k: t.ansi8 ?? t.dim ?? "#585b70",
          g: t.working ?? t.ansi3 ?? "#f9e2af",
        };
        out.push(prop("pip-desk", this.deskX(), this.deskY(), DESK_W * px, DESK_H * px)
          .children(pixels(DESK_CHASSIS, deskColors, px, "dk"))
          .child(div().id("pip-screen").absolute().left(0).top(0).w(DESK_W * px).h(DESK_H * px)
            // A shimmer, not a pulse: yellow dimmed much further on a dark sidebar
            // is khaki, the trap the aquarium's far fish fell into.
            .loop("opacity", { from: 0.8, to: 1, period: 1600 })
            .children(pixels(DESK_SCREEN, deskColors, px, "sc"))));
        if (typing) {
          const pace = this.pace();
          WORK_PAWS.forEach((p, i) => out.push(
            div().id(`wpaw${i}`).absolute().left(x + p.col * px).top(top + p.row * px).w(px).h(px)
              .child(div().id(`wpaw${i}-m`).absolute().left(0).top(0).w(px).h(px).bg(colors.b)
                .loop("top", { from: i ? -px : 0, to: i ? 0 : -px, period: Math.round(p.period * pace) }))));
        }
        break;
      }
      case "read": {
        const bookColors = {
          k: t.accent ?? t.ansi4 ?? "#89b4fa",
          K: t.ring ?? t.ansi12 ?? "#b4befe",
          g: t.working ?? t.ansi3 ?? "#f9e2af",
          b: colors.b,
        };
        out.push(prop("pip-book", x, top + BOOK_ROW * px, SW * px, BOOK.length * px)
          .children(pixels(BOOK, bookColors, px, "bk")));
        break;
      }
      case "build": {
        const sx = x + (SW + 1) * px;
        const unit = BLOCK * px;
        for (const b of a.blocks) {
          const [bx, by] = STACK[b.slot];
          const y = this.ground() - (by + 1) * unit;
          // Dropped in from above, onto its place in the pyramid.
          out.push(div().id(`blk${b.id}`).absolute().left(sx + bx * unit).w(unit).h(unit).bg(b.color)
            .top(b.phase ? y : y - 20).opacity(b.phase && !b.fading ? 1 : 0)
            .transition("top", { duration: 300, easing: "ease-in" })
            .transition("opacity", { duration: 300 }));
        }
        break;
      }
      case "bugs": {
        const bug = a.bug;
        if (!bug) break;
        const rows = bug.dir > 0 ? BUG : BUG.map((r) => [...r].reverse().join(""));
        const bugColors = { r: t.ansi1 ?? "#f38ba8", e: t.chrome ?? "#11111b", f: t.dim ?? "#7f849c" };
        let legs = div().id(`bug${bug.id}-legs`).absolute().left(0).top(0).w(BUG_W * px).h(BUG_H * px)
          .children(pixels(rows, bugColors, px, `bug${bug.id}`));
        if (now < bug.until) legs = legs.loop("top", { from: 0, to: -1, period: 140 });   // scuttling
        out.push(div().id(`bug${bug.id}`).absolute().left(bug.x).top(this.ground() - BUG_H * px).w(BUG_W * px).h(BUG_H * px)
          .transition("left", { duration: Math.max(1, Math.round(bug.ms)), easing: "linear" })
          .child(legs));
        break;
      }
      case "think": {
        const glow = t.working ?? t.ansi3 ?? "#f9e2af";
        if (!a.ahaUntil) {
          const edge = t.input ?? t.border ?? "#45475a";
          const cloudColors = { q: t.raised ?? "#313244", Q: edge };
          let cloud = prop("pip-cloud", x + CLOUD_COL * px, top - 11 * px, CLOUD[0].length * px, CLOUD.length * px)
            .children(pixels(CLOUD, cloudColors, px, "cl"));
          [900, 1150, 1400].forEach((period, i) =>
            (cloud = cloud.child(dot(`cdot${i}`, (3 + 2 * i) * px, 3 * px, px, t.muted ?? "#a6adc8", period))));
          out.push(
            prop("pip-trail0", x + 12 * px, top - px, px, px).bg(edge),
            prop("pip-trail1", x + 13 * px, top - 4 * px, 2 * px, 2 * px).bg(edge),
            cloud);
        } else {
          // The idea: a lightbulb over its head, twinkling.
          out.push(div().id("pip-bulb").absolute().left(x + 5 * px).top(top - 9 * px).w(5 * px).h(BULB.length * px)
            .children(pixels(BULB, { y: glow, s: t.muted ?? "#a6adc8" }, px, "bl")));
          [[3, -8, 500], [11, -9, 650], [2, -4, 580], [12, -5, 720]].forEach(([c, r, period], i) =>
            out.push(dot(`twk${i}`, x + c * px, top + r * px, px, glow, period)));
        }
        break;
      }

      // ---- idle pastimes ----
      case "ball": {
        const b = a.ball;
        const bw = BALL_W * px;
        let shine = div().id(`ball${a.id}-hi`).absolute().left(px).top(px).w(px).h(px).bg(t.foreground ?? "#cdd6f4");
        if (now < b.until) shine = shine.loop("left", { from: 0, to: 2 * px, period: 300, easing: "linear" });
        out.push(div().id(`ball${a.id}`).absolute().left(b.x).top(this.ground() - BALL.length * px).w(bw).h(bw)
          .transition("left", { duration: Math.max(1, Math.round(b.ms)), easing: b.ease ?? "ease-out" })
          .children(pixels(BALL, { r: t.ansi1 ?? "#f38ba8" }, px, `ball${a.id}`))
          .child(shine));
        break;
      }
      case "butterfly": {
        const f = a.fly;
        const wing = (side) => div().id(`fly${a.id}-w${side}`).absolute().left(side < 0 ? 0 : 3 * px).top(0)
          .w(2 * px).h(2 * px).bg(f.color).loop("height", { from: 2 * px, to: px * 0.5, period: 240 });
        out.push(div().id(`fly${a.id}`).absolute().left(f.x).top(f.y).w(FLY_W * px).h(3 * px)
          .transition("left", { duration: Math.max(1, Math.round(f.ms)), easing: "ease-in-out" })
          .transition("top", { duration: Math.max(1, Math.round(f.ms)), easing: "ease-in-out" })
          .child(div().id(`fly${a.id}-bob`).absolute().left(0).top(0).w(FLY_W * px).h(3 * px)
            .loop("top", { from: 0, to: -3, period: 520 })
            .child(wing(-1)).child(wing(1))
            .child(div().id(`fly${a.id}-l0`).absolute().left(px).top(2 * px).w(px).h(px).bg(f.color))
            .child(div().id(`fly${a.id}-l1`).absolute().left(3 * px).top(2 * px).w(px).h(px).bg(f.color))
            .child(div().id(`fly${a.id}-b`).absolute().left(2 * px).top(0).w(px).h(3 * px).bg(t.dim ?? "#7f849c"))));
        break;
      }
      case "flower": {
        if (a.fx == null) break;
        const g = this.ground();
        const stem = STEM_H * px;
        const bloomTop = g - stem - FLOWER.length * px;
        out.push(div().id(`flw${a.id}`).absolute().left(a.fx).top(bloomTop).w(FLOWER[0].length * px).h(g - bloomTop)
          .opacity(a.fading ? 0 : 1).transition("opacity", { duration: 600 })
          // the stem grows up out of the grass
          .child(div().id(`flw${a.id}-stem`).absolute().left(2 * px).w(px).bg(t.ansi2 ?? "#a6e3a1")
            .top(a.grown ? FLOWER.length * px : g - bloomTop).h(a.grown ? stem : 0)
            .transition("top", { duration: 900, easing: "ease-out" })
            .transition("height", { duration: 900, easing: "ease-out" }))
          // and the flower opens on top of it
          .child(div().id(`flw${a.id}-bloom`).absolute().left(0).top(0).w(FLOWER[0].length * px).h(FLOWER.length * px)
            .opacity(a.step >= 2 ? 1 : 0).transition("opacity", { duration: 300 })
            .children(pixels(FLOWER, { p: a.color, y: t.ansi3 ?? "#f9e2af" }, px, `flw${a.id}`))));
        break;
      }
      case "dig": {
        if (a.step < 2 || a.hx == null) break;
        const g = this.ground();
        // the hole: a dark patch in the grass, under its front paws
        out.push(div().id(`hole${a.id}`).absolute().left(a.hx).top(g).w(4 * px).h(3).bg(t.chrome ?? "#11111b")
          .opacity(a.fading ? 0 : 1).transition("opacity", { duration: 600 }));
        // what turned up: out of the hole and up over its head
        const shape = TREASURES[a.treasure];
        const tw = shape[0].length * px;
        const colors = { g: a.treasure === "star" ? (t.ansi3 ?? "#f9e2af") : (t.ansi6 ?? "#94e2d5") };
        const held = { x: x + (SW * px - tw) / 2, y: top - (shape.length + 1) * px };
        const fromHole = { x: a.hx + 2 * px - tw / 2, y: g - shape.length * px };
        const at = a.raised ? held : fromHole;
        out.push(div().id(`trs${a.id}`).absolute().left(at.x).top(at.y).w(tw).h(shape.length * px)
          .opacity(a.fading ? 0 : 1)
          .transition("left", { duration: 700, easing: "ease-out" })
          .transition("top", { duration: 700, easing: "ease-out" })
          .transition("opacity", { duration: 600 })
          .children(pixels(shape, colors, px, `trs${a.id}`)));
        if (a.raised && !a.fading) {
          [[-2, 0, 520], [tw / px + 1, 1, 610], [-1, shape.length, 560], [tw / px, shape.length + 1, 680]].forEach(([c, r, period], i) =>
            out.push(dot(`trw${i}`, held.x + c * px, held.y + r * px - px, px, colors.g, period)));
        }
        break;
      }
    }
    return out;
  }

  render(cx) {
    const t = theme();
    const now = Date.now();
    const px = this.px();
    const sw = SW * px;
    if (this.x === null) this.x = Math.max(0, (this.w - sw) / 2);

    const fake = (status, n = 1) => Array.from({ length: n }, () => ({ status, program: { name: "Claude Code" }, space_name: "demo", address: null }));
    const list = this.demo === "knock" ? fake("blocked")
      : this.demo === "party" ? fake("done")
      : this.demo === "work" ? fake("working")
      : this.demo === "busy" ? fake("working", 3)
      : this.demo === "idle" ? []
      : agents();
    const needing = list.filter((a) => a.status === "blocked");
    const done = list.filter((a) => a.status === "done");
    this.working = list.filter((a) => a.status === "working").length;
    const away = this.away || this.demo === "sleep";
    const mode = needing.length ? "knock" : done.length ? "party" : away ? "sleep" : this.working ? "work" : "idle";
    if (mode !== this.mode) {
      if (mode === "party") this.burstAt = 0;
      if (mode === "idle") this.nextMove = now + rand(1500, 3500);
      this.mode = mode;
      // Work starts at the laptop, the one you know (or what `work-<kind>` asked for);
      // the tick moves it on from there.
      this.act = null;
      if (mode === "work") {
        this.walkUntil = 0;
        this.startActivity(now, this.wantAct ?? "type");
        this.wantAct = null;
      }
      // Idle opens with a wander; the first pastime comes a little sooner than later ones.
      if (mode === "idle") {
        if (this.wantPastime) this.startPastime(now, this.wantPastime);
        else this.act = { kind: "wander", until: now + rand(10000, 25000) };
        this.wantPastime = null;
      }
    }

    // Knocking and dancing happen in the middle.
    const center = Math.max(0, (this.w - sw) / 2);
    let x = this.x;
    let walkMs = this.walkMs;
    if (mode === "knock" || mode === "party") {
      walkMs = Math.max(300, (Math.abs(center - this.x) / 90) * 1000);
      x = this.x = center;
    }
    const walking = now < this.walkUntil;
    const petting = now < this.petUntil;

    // What it is doing, once it has got there.
    const act = mode === "work" || mode === "idle" ? this.act : null;
    const at = act && !walking ? act.kind : null;
    // At the desk or the blocks it faces them from now on, so a pet or a break does
    // not turn it back the way it walked in.
    if (at === "type" || at === "build") this.facing = 1;
    const onBreak = at === "type" && now < this.breakUntil;
    const cheering = at === "build" && now < act.cheerUntil;
    const yawning = at === "yawn" && act.step === 1;
    const holdingUp = at === "dig" && act.step === 2;
    const stretching = (onBreak && this.breakKind === "stretch") || cheering || (at === "yawn" && act.step === 2) || holdingUp;
    const typing = at === "type" && !onBreak && !petting;
    const reading = at === "read";
    const stalking = act?.kind === "bugs" && Boolean(act.bug) && !act.pouncing;
    const pondering = at === "think" && !act.ahaUntil;
    const digging = at === "dig" && act.step === 1;
    const sniffing = at === "flower" && act.step === 3;
    const flyWatch = act?.kind === "butterfly" && act.moves > 0;
    const towards = (x0, w0) => (x0 + w0 / 2 >= this.x + sw / 2 ? 1 : -1);
    // Stalking, it keeps its eyes on the bug, even when backing off from one coming
    // its way (facing the way it walked read as running from it). The same goes for
    // a ball or a butterfly, once it has stopped to watch.
    if (stalking) this.facing = towards(act.bug.x, BUG_W * px);
    if (act?.kind === "ball" && !walking && act.step > 0) this.facing = towards(act.ball.x, BALL_W * px);
    if (flyWatch && !walking) this.facing = towards(act.fly.x, FLY_W * px);
    if ((at === "flower" || at === "dig") && act.step > 0) this.facing = act.side;

    let face;
    if (mode === "sleep") face = { eyes: "closed", mouth: "none", lying: true };
    else if (mode === "knock") face = { eyes: "wide", mouth: "open" };
    else if (mode === "party" || petting) face = { eyes: "happy", mouth: "smile", wag: true };
    else if (stretching) face = { eyes: "happy", mouth: "smile", arms: true };
    else if (yawning) face = { eyes: "closed", mouth: "open" };
    else if (stalking) face = { eyes: this.blink ? "blink" : "open", mouth: "small", lying: true };   // crouched
    else if (digging) face = { eyes: "happy", mouth: "small", lying: true };
    else if (pondering || flyWatch) face = { eyes: "up", mouth: pondering ? "small" : "smile" };
    else if (sniffing) face = { eyes: "happy", mouth: "smile" };
    else if (reading) face = { eyes: this.blink ? "blink" : "open", mouth: "none" };
    else face = { eyes: this.blink ? "blink" : "open", mouth: typing ? "small" : "smile" };
    const facesSomething = stalking || walking || flyWatch || digging || at === "flower" || (act?.kind === "ball" && act.step > 0);
    const look = typing ? 1 : reading ? act.look : yawning ? 0 : facesSomething ? this.facing : 0;
    const grid = sprite({ ...face, look, facing: this.facing });
    const colors = { b: t.ansi5 ?? "#f5c2e7", c: t.ansi1 ?? "#f38ba8" };

    // body <- hop/bob <- rattle <- pixels
    let shake = div().id("pip-shake").absolute().left(0).top(0).w(sw).h(SH * px).children(pixels(grid, colors, px, "px"));
    if (mode === "knock") shake = shake.loop("left", { from: -1.5, to: 1.5, period: 140, easing: "linear" });

    let bounce = div().id("pip-bounce").absolute().left(0).top(0).w(sw).h(SH * px).child(shake);
    if (mode === "party") bounce = bounce.loop("top", { from: 0, to: -12, period: 520, easing: "ease-out" });
    else if (mode === "knock") bounce = bounce.loop("top", { from: 0, to: -4, period: 560 });
    else if (petting) bounce = bounce.loop("top", { from: 0, to: -6, period: 380, easing: "ease-out" });
    else if (stretching) bounce = bounce.loop("top", { from: 0, to: -px, period: 900 });
    else if (walking) bounce = bounce.loop("top", { from: 0, to: -2, period: 260 });
    else if (typing) bounce = bounce.loop("top", { from: 0, to: -1.5, period: 320 });
    else if (digging) bounce = bounce.loop("top", { from: 0, to: -px * 0.75, period: 200 });

    const lift = face.lying ? px : 0;
    let body = div().id("pip-body").absolute().left(x).top(this.baseY() + lift).w(sw).h(SH * px)
      .transition("left", { duration: Math.round(walkMs), easing: "ease-in-out" })
      .child(bounce);

    if (mode === "knock") {
      // Paws pressed on the glass, either side of its head.
      for (const side of [-1, 1]) {
        const pawX = sw / 2 - 2.5 * px + side * 8 * px;
        body = body.child(div().id(`paw${side}`).absolute().left(pawX).top(px).w(5 * px).h(4 * px)
          .loop("opacity", { from: 0.15, to: 1, period: side > 0 ? 600 : 760 })
          .children(pixels(PAW, colors, px, `paw${side}`)));
      }
    }
    if (mode === "sleep") {
      body = body.child(div().id("zzz").absolute().left(sw - 2).top(-14).text_size(11).text_color(t.ansi4 ?? t.accent)
        .frames(["z", "z z", "z z Z", "  z Z", "    Z", ""], 520));
    }

    const children = [];
    // grass, with a few tufts that stay put
    children.push(div().id("grass").absolute().left(0).right(0).top(this.ground()).h(3).bg(t.ansi2 ?? "#a6e3a1").opacity(0.85));
    for (let i = 0; i * 37 < this.w; i++) {
      const gx = (i * 37 + 11) % Math.max(1, this.w);
      children.push(div().id(`tuft${i}`).absolute().left(gx).top(this.ground() - 3).w(3).h(3).bg(t.ansi2 ?? "#a6e3a1").opacity(0.85));
    }
    children.push(body);

    if (act) children.push(...this.renderActivity(act, { t, now, px, walking, typing, colors }));

    for (const p of this.sparks) {
      children.push(div().id(`sp${p.id}`).absolute().w(p.w).h(p.h).bg(p.color)
        .left(p.phase ? p.x2 : p.x).top(p.phase ? p.y2 : p.y).opacity(p.phase ? 0 : 0.95)
        .transition("top", { duration: Math.round(p.ms), easing: "ease-out" })
        .transition("left", { duration: Math.round(p.ms), easing: "ease-out" })
        .transition("opacity", { duration: Math.round(p.ms * 0.7), delay: Math.round(p.ms * 0.3) }));
    }
    for (const p of this.confetti) {
      children.push(div().id(`cf${p.id}`).absolute().w(3).h(3).bg(p.color)
        .left(p.phase ? p.x2 : p.x).top(p.phase ? p.y2 : p.y).opacity(p.phase ? 0 : 1)
        .transition("top", { duration: Math.round(p.ms), easing: "ease-in" })
        .transition("left", { duration: Math.round(p.ms), easing: "ease-out" })
        .transition("opacity", { duration: 700, delay: Math.round(p.ms + 2200) }));
    }
    for (const p of this.hearts) {
      children.push(div().id(`ht${p.id}`).absolute().left(p.x).top(p.phase ? p.y - 30 : p.y)
        .opacity(p.phase ? 0 : 1).text_size(12).text_color(t.ansi1 ?? "#f38ba8").child("♥")
        .transition("top", { duration: 1300, easing: "ease-out" })
        .transition("opacity", { duration: 900, delay: 400 }));
    }

    // The sign over its head.
    let sign = null;
    if (mode === "knock") {
      const spaces = unique(needing.map((a) => a.space_name));
      sign = { title: needing.length === 1 ? `${who(needing[0])} needs you!` : `${needing.length} agents need you!`, sub: spaces.join(", "), color: t.blocked ?? t.ansi3 };
    } else if (mode === "party") {
      const spaces = unique(done.map((a) => a.space_name));
      sign = { title: done.length === 1 ? `${who(done[0])}'s done!` : `${done.length} agents are done!`, sub: spaces.join(", "), color: t.done ?? t.ansi2 };
    } else if (this.bubble) {
      sign = this.bubble;
    }
    if (sign && this.w >= 120) {
      const tone = sign.color ?? t.foreground;
      children.push(h_flex().id("sign").absolute().left(6).right(6).top(4).justify_center()
        .child(v_flex().id("sign-card").max_w(this.w - 12).px(10).py(3).rounded(8).bg(t.raised).border_1().border_color(tone).items_center()
          .child(div().truncate().text_size(12).font_semibold().text_color(tone).child(sign.title))
          .when(Boolean(sign.sub), (el) => el.child(div().truncate().text_size(11).text_color(t.muted).child(sign.sub)))));
    }

    return div().id("pip").relative().size_full().overflow_hidden().cursor_pointer()
      .on_size_change((size, cx) => {
        // The size comes with Herdr's zoom in it, and everything placed here is zoomed again:
        // at 120% Pip stood below the block's bottom edge, cut off with the grass.
        const zoom = Number(settings().zoom) || 1;
        this.w = size.width / zoom;
        this.h = size.height / zoom;
        this.x = Math.min(this.x ?? 0, Math.max(0, this.w - SW * this.px()));
        // A spot picked for the old width may not leave room for the prop any more
        // (the first activity is picked before the first layout): walk to a new one.
        // Not mid-pastime or mid-hunt, which would walk it away from its ball or flower.
        const kind = this.act?.kind;
        const settled = ["read", "build", "think", "wander"].includes(kind);
        if (kind === "type" || (settled && this.x + this.span(kind) * this.px() > this.w)) {
          this.walkTo(this.spotFor(kind), Date.now(), 60);
        }
        cx.notify();
      })
      .on_click((_event, cx) => {
        // Pip only ever points at an agent; the click takes you there.
        const target = needing[0] ?? done[0];
        if (target?.address) run("agent.focus", { terminal: target.address });
        else this.pet(cx, Date.now());
        cx.notify();
      })
      .children(children);
  }
}
