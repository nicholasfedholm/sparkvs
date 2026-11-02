r"""SparkVS self-contained HTML report (spark-viewer style).

Usage:
  python -I make_report.py --gates <bench-runner\runs\gpugates-*.json> --out <file.html>
         [--run-folder <run folder> ...] [--mods-snapshot <csv>] [--build-sha <hex>] [--title T]

Inputs per run folder: sparkvs-bundle\<capture>\{frames-summary.json, gpu-memory.jsonl, gpu-phases.json,
speedscope.json, summary.json, context.json}, plus run.json and manifest.json in the run folder.
Output: one HTML file, all CSS/JS/SVG inline, zero external requests. Missing data renders as "no data" + reason.
"""
import argparse, csv, datetime as dt, glob, html, json, os, statistics, sys

VRAM_BAR = 10.0       # percent, DXGI local vs PDH Local, settled median of last 60 rows (gate defined in SparkVS source)
PHASE_BAR = 3.0       # percent FPS cost, gpu-phases on vs off (gate defined by the 2026-11-02 task)
PHASES = ["shadow", "opaque", "oit", "post", "final", "ui"]
COLORS = {"shadow": "#7c8cff", "opaque": "#3fb68b", "oit": "#e0a84f", "post": "#d46aa8", "final": "#5bc0de", "ui": "#b0b6c3"}
E = html.escape


def jload(p):
    try:
        with open(p, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return None


def jsonl(p):
    rows = []
    try:
        with open(p, encoding="utf-8-sig") as f:
            for ln in f:
                ln = ln.strip()
                if ln:
                    try:
                        rows.append(json.loads(ln))
                    except Exception:
                        pass
    except Exception:
        pass
    return rows


def fmt(v, nd=1, unit=""):
    if v is None:
        return "no data"
    try:
        return f"{v:,.{nd}f}{unit}"
    except Exception:
        return E(str(v))


def load_run(rf):
    r = {"folder": rf, "name": os.path.basename(rf.rstrip("\\/"))}
    caps = sorted(glob.glob(os.path.join(rf, "sparkvs-bundle", "*")))
    caps = [c for c in caps if os.path.isdir(c)]
    r["bundle"] = caps[-1] if caps else None
    r["run"] = jload(os.path.join(rf, "run.json")) or {}
    r["manifest"] = jload(os.path.join(rf, "manifest.json")) or {}
    b = r["bundle"]
    if b:
        r["frames"] = jload(os.path.join(b, "frames-summary.json"))
        mem = jsonl(os.path.join(b, "gpu-memory.jsonl"))
        r["mem_header"] = mem[0] if mem and mem[0].get("type") == "header" else {}
        r["mem_rows"] = [x for x in mem if x.get("type") != "header"]
        r["phases"] = jload(os.path.join(b, "gpu-phases.json"))
        r["summary"] = jload(os.path.join(b, "summary.json")) or {}
        r["context"] = jload(os.path.join(b, "context.json")) or {}
        r["speedscope_path"] = os.path.join(b, "speedscope.json")
    gate = (r.get("mem_header") or {}).get("vram_gate")
    if not gate:
        gate = (((r.get("summary") or {}).get("sections") or {}).get("vram_dedicated") or {}).get("data", {}) or {}
        gate = gate.get("vram_gate") if isinstance(gate, dict) else None
    r["vram_gate"] = gate
    return r


def present_mode(r):
    pm = (r.get("run") or {}).get("present_mode")
    if isinstance(pm, dict):
        return pm.get("present_mode_verbatim") or pm.get("present_mode_measure_window")
    return pm


def stat_fps(r):
    s = (r.get("frames") or {})
    s = s.get("stats", s) if isinstance(s, dict) else {}
    return s


# ---------------- CPU tree from speedscope ----------------
def build_tree(path, max_profiles=3, min_pct=0.2, max_depth=40):
    d = jload(path)
    if not d:
        return None, "speedscope.json missing or unreadable"
    frames = [f.get("name", "?") for f in d.get("shared", {}).get("frames", [])]
    profs = [p for p in d.get("profiles", []) if p.get("type") == "sampled" and p.get("samples")]
    profs.sort(key=lambda p: len(p["samples"]), reverse=True)
    if not profs:
        return None, "no sampled profiles in speedscope.json"
    total_all = sum(len(p["samples"]) for p in profs)
    out = []
    for p in profs[:max_profiles]:
        root = {"n": p.get("name", "thread"), "w": 0.0, "c": {}}
        ws = p.get("weights") or [1.0] * len(p["samples"])
        for stack, w in zip(p["samples"], ws):
            root["w"] += w
            node = root
            for idx in stack[:max_depth]:
                ch = node["c"].get(idx)
                if ch is None:
                    ch = node["c"][idx] = {"n": frames[idx] if idx < len(frames) else str(idx), "w": 0.0, "c": {}}
                ch["w"] += w
                node = ch
        tot = root["w"] or 1.0

        def conv(n):
            kids = [conv(c) for c in n["c"].values() if c["w"] / tot * 100 >= min_pct]
            kids.sort(key=lambda k: -k["p"])
            return {"n": n["n"], "p": round(n["w"] / tot * 100, 2), "ms": round(n["w"], 1), "c": kids}
        t = conv(root)
        t["samples"] = len(p["samples"])
        out.append(t)
    note = f"{len(profs)} sampled threads, {total_all:,} samples; top {min(max_profiles, len(profs))} threads shown, nodes under {min_pct}% of their thread pruned"
    return out, note


# ---------------- SVG charts ----------------
def vram_chart(r, w=880, h=260):
    rows = r.get("mem_rows") or []
    t0 = None
    pts = {"dxgi": [], "local": [], "ded": []}
    for x in rows:
        t = x.get("t_ms")
        if t is None:
            continue
        t0 = t if t0 is None else t0
        ts = (t - t0) / 1000.0
        if x.get("dedicated_bytes") is not None and (x.get("source") or "").lower().find("dxgi") >= 0:
            pts["dxgi"].append((ts, x["dedicated_bytes"] / 2**20))
        elif x.get("dedicated_bytes") is not None and x.get("source") is None:
            pts["dxgi"].append((ts, x["dedicated_bytes"] / 2**20))
        if x.get("pdh_local_bytes") is not None:
            pts["local"].append((ts, x["pdh_local_bytes"] / 2**20))
        if x.get("pdh_dedicated_bytes") is not None:
            pts["ded"].append((ts, x["pdh_dedicated_bytes"] / 2**20))
    allp = [p for v in pts.values() for p in v]
    if not allp:
        return '<div class="nodata">no data: gpu-memory.jsonl has no rows with DXGI or PDH values</div>'
    xmax = max(p[0] for p in allp) or 1
    ymin = min(p[1] for p in allp)
    ymax = max(p[1] for p in allp)
    pad = (ymax - ymin) * 0.1 or 50
    ymin, ymax = max(0, ymin - pad), ymax + pad
    L, R, T, B = 60, 16, 12, 30
    X = lambda t: L + (w - L - R) * t / xmax
    Y = lambda v: T + (h - T - B) * (1 - (v - ymin) / (ymax - ymin))
    s = [f'<svg viewBox="0 0 {w} {h}" class="chart" role="img" aria-label="VRAM time series">']
    gate = r.get("vram_gate") or {}
    n = gate.get("settled_rows") or 60
    src = pts["dxgi"] if pts["dxgi"] else allp
    if len(src) >= 2:
        ws_ = src[-n][0] if len(src) >= n else src[0][0]
        s.append(f'<rect x="{X(ws_):.1f}" y="{T}" width="{X(xmax)-X(ws_):.1f}" height="{h-T-B}" fill="#ffffff10" stroke="#ffffff30" stroke-dasharray="3 3"/>')
        s.append(f'<text x="{X(ws_)+4:.1f}" y="{T+12}" class="lbl">settled gate window (last {n} rows)</text>')
    for i in range(5):
        v = ymin + (ymax - ymin) * i / 4
        s.append(f'<line x1="{L}" x2="{w-R}" y1="{Y(v):.1f}" y2="{Y(v):.1f}" class="grid"/><text x="{L-6}" y="{Y(v)+4:.1f}" class="ax" text-anchor="end">{v:,.0f}</text>')
    for i in range(6):
        t = xmax * i / 5
        s.append(f'<text x="{X(t):.1f}" y="{h-10}" class="ax" text-anchor="middle">{t:,.0f}s</text>')
    style = {"dxgi": ("#3fb68b", "", "DXGI local (resident, gated)"), "local": ("#7c8cff", "6 3", "PDH Local Usage (gated pair)"),
             "ded": ("#e0a84f", "2 3", "PDH Dedicated (committed, NOT gated)")}
    for k, (col, dash, _) in style.items():
        if pts[k]:
            d = " ".join(f"{X(a):.1f},{Y(b):.1f}" for a, b in pts[k])
            s.append(f'<polyline points="{d}" fill="none" stroke="{col}" stroke-width="1.6" stroke-dasharray="{dash}"/>')
    s.append(f'<text x="14" y="{(h)/2}" class="ax" transform="rotate(-90 14 {h/2})" text-anchor="middle">MiB</text></svg>')
    leg = "".join(f'<span class="lg"><i style="background:{c}"></i>{E(lbl)}{"" if pts[k] else " (no data)"}</span>' for k, (c, _, lbl) in style.items())
    return "".join(s) + f'<div class="legend">{leg}</div>'


def phase_bar(ph, w=880):
    p = (ph or {}).get("phases") or {}
    vals = [(k, (p.get(k) or {}).get("mean_ms")) for k in PHASES]
    vals = [(k, v) for k, v in vals if isinstance(v, (int, float)) and v >= 0]
    tot = sum(v for _, v in vals)
    if not vals or tot <= 0:
        return '<div class="nodata">no data: gpu-phases.json has no phase timings</div>'
    x, s = 0.0, [f'<svg viewBox="0 0 {w} 46" class="chart" role="img" aria-label="GPU phase stacked bar">']
    for k, v in vals:
        bw = (w - 2) * v / tot
        s.append(f'<rect x="{x+1:.1f}" y="4" width="{max(bw,0.5):.1f}" height="28" fill="{COLORS[k]}"><title>{k}: {v:.3f} ms ({v/tot*100:.1f}%)</title></rect>')
        if bw > 60:
            s.append(f'<text x="{x+6:.1f}" y="23" class="barlbl">{k} {v/tot*100:.0f}%</text>')
        x += bw
    s.append("</svg>")
    return "".join(s)


# ---------------- page ----------------
CSS = """
:root{--bg:#1b1d23;--panel:#24272f;--panel2:#2c303a;--fg:#e6e8ee;--mut:#9aa1ae;--ok:#3fb68b;--bad:#e5534b;--warn:#e0a84f;--acc:#7c8cff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
header{padding:18px 28px;background:#15171c;border-bottom:1px solid #000}header h1{margin:0;font-size:20px}header .sub{color:var(--mut);font-size:12px}
main{padding:18px 28px;max-width:1240px}section{background:var(--panel);border-radius:8px;padding:14px 18px;margin:0 0 16px}
h2{font-size:15px;margin:0 0 10px;letter-spacing:.3px}.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:12px;margin-bottom:16px}
.card{background:var(--panel);border-radius:8px;padding:12px 14px;border-left:4px solid var(--acc)}.card .k{color:var(--mut);font-size:11px;text-transform:uppercase;letter-spacing:.5px}
.card .v{font-size:24px;font-weight:600;margin:2px 0}.card .s{color:var(--mut);font-size:12px}.card.pass{border-color:var(--ok)}.card.fail{border-color:var(--bad)}.card.nd{border-color:#555}
.strip{display:flex;flex-wrap:wrap;gap:8px 22px;font-size:12px}.strip b{color:var(--mut);font-weight:500;margin-right:4px}
table{border-collapse:collapse;width:100%;font-size:12.5px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #353a46}th{color:var(--mut);font-weight:500}
td.num{text-align:right;font-variant-numeric:tabular-nums}.pass{color:var(--ok)}.fail{color:var(--bad)}.nd{color:var(--mut)}
.tag{display:inline-block;font-size:10px;padding:1px 6px;border-radius:3px;background:var(--warn);color:#000;margin-left:8px;vertical-align:middle}
.chart{width:100%;height:auto;background:var(--panel2);border-radius:6px}.grid{stroke:#ffffff14}.ax{fill:var(--mut);font-size:10px}.lbl{fill:#cfd3dc;font-size:10px}.barlbl{fill:#111;font-size:11px;font-weight:600}
.legend{font-size:12px;color:var(--mut);margin:6px 0}.lg{margin-right:16px}.lg i{display:inline-block;width:12px;height:3px;margin-right:5px;vertical-align:middle}
.nodata{color:var(--mut);font-style:italic;padding:8px 0}.tree{font:12px/1.5 Consolas,Menlo,monospace}.tree ul{list-style:none;margin:0;padding-left:16px}.tree>ul{padding-left:0}
.node{cursor:pointer;white-space:nowrap;display:flex;align-items:center;gap:8px;padding:1px 0}.node:hover{background:#ffffff0a}.node .pc{width:58px;text-align:right;color:var(--fg);flex:none}
.node .bar{width:90px;height:7px;background:#ffffff12;border-radius:2px;flex:none;overflow:hidden}.node .bar i{display:block;height:100%;background:var(--acc)}
.node .tw{width:12px;color:var(--mut);flex:none}.node .nm{overflow:hidden;text-overflow:ellipsis}.node .ms{color:var(--mut)}li.c>ul{display:none}
ul.lim li{margin:3px 0}.small{color:var(--mut);font-size:12px}
"""

JS = """
function mk(n,root){var li=document.createElement('li');var has=n.c&&n.c.length;li.className=has?'c':'';
var d=document.createElement('div');d.className='node';
d.innerHTML='<span class="tw">'+(has?'\\u25B8':'')+'</span><span class="pc">'+n.p.toFixed(2)+'%</span><span class="bar"><i style="width:'+Math.min(100,n.p)+'%"></i></span><span class="nm"></span><span class="ms">'+n.ms.toLocaleString()+' ms</span>';
d.querySelector('.nm').textContent=n.n;li.appendChild(d);
if(has){var built=false;d.onclick=function(){if(!built){var ul=document.createElement('ul');n.c.forEach(function(k){ul.appendChild(mk(k))});li.appendChild(ul);built=true}
var open=li.className==='c';li.className=open?'o':'c';d.querySelector('.tw').textContent=open?'\\u25BE':'\\u25B8';
var u=li.querySelector(':scope>ul');if(u)u.style.display=open?'block':'none'}}return li}
document.addEventListener('DOMContentLoaded',function(){var data=JSON.parse(document.getElementById('cpu-data').textContent||'null');var host=document.getElementById('cpu-tree');
if(!data||!host)return;data.forEach(function(t){var h=document.createElement('div');h.className='small';h.textContent='Thread: '+t.n+' ('+t.samples.toLocaleString()+' samples, '+t.ms.toLocaleString()+' ms)';host.appendChild(h);
var ul=document.createElement('ul');t.c.forEach(function(k){ul.appendChild(mk(k))});host.appendChild(ul)})});
"""


def card(k, v, s="", cls=""):
    return f'<div class="card {cls}"><div class="k">{E(k)}</div><div class="v">{v}</div><div class="s">{s}</div></div>'


def verdict_cls(p):
    return "pass" if p is True else "fail" if p is False else "nd"


def verdict_txt(p):
    return "PASS" if p is True else "FAIL" if p is False else "NO DATA"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gates", action="append", default=[], help="bench-runner gpugates-*.json (lists run folders); repeatable")
    ap.add_argument("--run-folder", action="append", default=[])
    ap.add_argument("--mods-snapshot")
    ap.add_argument("--build-sha", help="SparkVS zip sha256 (falls back to manifest.json mods[] sparkvs)")
    ap.add_argument("--title", default="SparkVS GPU report")
    ap.add_argument("--out", required=True)
    ap.add_argument("--note", action="append", default=[], help="extra honest-limits line (blocked steps etc.)")
    a = ap.parse_args()

    folders = list(a.run_folder)
    gate_rc = {}
    for gp in a.gates:
        gates_doc = jload(gp)
        if not isinstance(gates_doc, list):
            continue
        for g in gates_doc:
            if g.get("run_folder") and g.get("rc") in (0, 7):
                folders.append(g["run_folder"])
            if g.get("run_folder"):
                gate_rc[os.path.normcase(g["run_folder"])] = g.get("rc")
    runs = [load_run(f) for f in folders if os.path.isdir(f)]
    limits = list(a.note)
    if not runs:
        limits.append("No run folders could be loaded, so every section below is empty.")

    # ---- aggregates
    fps = [stat_fps(r).get("mean_fps") for r in runs]
    fps = [x for x in fps if isinstance(x, (int, float))]
    low = [stat_fps(r).get("one_percent_low_fps") for r in runs]
    low = [x for x in low if isinstance(x, (int, float))]
    med = [stat_fps(r).get("p50_frame_ms") for r in runs]
    p95 = [stat_fps(r).get("p95_frame_ms") for r in runs]
    mx = [stat_fps(r).get("max_frame_ms") for r in runs]
    med, p95, mx = ([x for x in L if isinstance(x, (int, float))] for L in (med, p95, mx))
    pmodes = sorted({str(present_mode(r)) for r in runs if present_mode(r)})
    pm_txt = " / ".join(pmodes) if pmodes else "present mode: no data"

    vg = [r.get("vram_gate") for r in runs]
    vpass = [g.get("pass") if isinstance(g, dict) else None for g in vg]
    vdiff = [g.get("diff_pct") if isinstance(g, dict) else None for g in vg]
    if runs and all(p is True for p in vpass):
        vram_all = True
    elif any(p is False for p in vpass):
        vram_all = False
    else:
        vram_all = None
    vd_num = [abs(x) for x in vdiff if isinstance(x, (int, float))]

    ab_rows, offs, ons, costs = [], [], [], []
    for r in runs:
        ab = (r.get("phases") or {}).get("overhead_ab") or {}
        if isinstance(ab.get("percent_cost"), (int, float)):
            costs.append(ab["percent_cost"])
        for b in ab.get("blocks") or []:
            ab_rows.append((r["name"], b))
            if isinstance(b.get("mean_fps"), (int, float)):
                (ons if b.get("state") == "on" else offs).append(b["mean_fps"])
    pooled = None
    if offs and ons:
        mo, mn = statistics.mean(offs), statistics.mean(ons)
        pooled = (mo - mn) / mo * 100 if mo else None
    phase_pass = None
    if pooled is not None:
        phase_pass = pooled < PHASE_BAR and all(c < PHASE_BAR for c in costs)

    # ---- header cards
    cards = [
        card("FPS average", fmt(statistics.mean(fps) if fps else None), f"{len(fps)} run(s) | {E(pm_txt)}"),
        card("1% low FPS", fmt(statistics.mean(low) if low else None), f"mean of per-run 1% lows | {E(pm_txt)}"),
        card("Frame time median / p95", f"{fmt(statistics.mean(med) if med else None, 2)} / {fmt(statistics.mean(p95) if p95 else None, 2)}", "ms, mean across runs"),
        card("Frame time max", fmt(max(mx) if mx else None, 1, " ms"), "worst single frame, any run"),
        card("VRAM gate", verdict_txt(vram_all), f"DXGI vs PDH Local, settled |delta| max {fmt(max(vd_num) if vd_num else None, 2, '%')}, bar {VRAM_BAR:.0f}%, {sum(1 for p in vpass if p is True)}/{len(runs)} runs pass", verdict_cls(vram_all)),
        card("gpu-phases overhead", fmt(pooled, 2, "%"), f"{verdict_txt(phase_pass)} vs {PHASE_BAR:.0f}% gate | pooled on/off blocks, EXPERIMENTAL", verdict_cls(phase_pass)),
    ]

    # ---- context strip
    sha = a.build_sha
    if not sha:
        for r in runs:
            for m in (r.get("manifest") or {}).get("mods") or []:
                if str(m.get("modid", "")).lower() == "sparkvs" and m.get("sha256"):
                    sha = m["sha256"]
                    break
            if sha:
                break
    ctx = (runs[0].get("context") if runs else {}) or {}
    gpus = ctx.get("gpus") or []
    gpu_txt = "; ".join(f"{g.get('model')} driver {g.get('driver_version')}" for g in gpus) or "no data"
    world = (ctx.get("world") or {}).get("name") if isinstance(ctx.get("world"), dict) else None
    world = world or (runs[0].get("summary") or {}).get("world") if runs else None
    durs = [r.get("run", {}).get("duration_s") for r in runs]
    snap = "no data"
    if a.mods_snapshot and os.path.exists(a.mods_snapshot):
        with open(a.mods_snapshot, encoding="utf-8-sig") as f:
            snap = f"{sum(1 for _ in csv.DictReader(f))} files | {a.mods_snapshot}"
    caps = [os.path.basename(r["bundle"]) for r in runs if r.get("bundle")]
    strip = [("SparkVS zip sha256", (sha or "no data")[:16] + ("..." if sha else "")), ("GPU / driver", gpu_txt), ("Test world", world or "no data"),
             ("Run durations", ", ".join(f"{d:g} s" for d in durs if isinstance(d, (int, float))) or "no data"),
             ("Mods snapshot", snap), ("Captures", ", ".join(caps) or "no data")]
    strip_html = "".join(f"<span><b>{E(k)}</b>{E(str(v))}</span>" for k, v in strip)

    # ---- VRAM section
    vrows = []
    for r, g in zip(runs, vg):
        g = g if isinstance(g, dict) else {}
        mb = lambda x: fmt(x / 2**20 if isinstance(x, (int, float)) else None, 1)
        why = g.get("reason") or ("within bar" if g.get("pass") else "outside bar" if g.get("pass") is False else "gate object missing")
        vrows.append(f"<tr><td>{E(r['name'])}</td><td class='num'>{mb(g.get('median_dxgi_bytes'))}</td><td class='num'>{mb(g.get('median_pdh_local_bytes'))}</td>"
                     f"<td class='num'>{fmt(g.get('diff_pct'), 3, '%')}</td><td class='num nd'>{mb(g.get('median_pdh_dedicated_bytes'))}</td>"
                     f"<td class='num nd'>{fmt(g.get('dxgi_vs_pdh_dedicated_pct'), 2, '%')}</td><td class='num'>{g.get('paired_rows', 'no data')}</td>"
                     f"<td class='{verdict_cls(g.get('pass'))}'>{verdict_txt(g.get('pass'))}</td><td class='small'>{E(str(why))}</td></tr>")
    vram_html = (vram_chart(runs[0]) if runs else '<div class="nodata">no data: no runs</div>')
    vram_html += (f"<p class='small'>Chart: {E(runs[0]['name'])}. Gate = |median DXGI local - median PDH Local| / PDH Local over the last settled rows, bar {VRAM_BAR:.0f}%. "
                  "PDH Dedicated is a committed figure, shown for context only and never part of the gate.</p>" if runs else "")
    vram_html += ("<table><tr><th>Run</th><th>DXGI local MiB</th><th>PDH Local MiB</th><th>delta</th><th>PDH Dedicated MiB (committed)</th><th>DXGI vs Dedicated</th><th>rows</th><th>verdict</th><th>reason</th></tr>"
                  + "".join(vrows) + "</table>") if vrows else '<div class="nodata">no data</div>'

    # ---- phases section
    ph0 = next((r.get("phases") for r in runs if (r.get("phases") or {}).get("phases")), None)
    ptab = ""
    if ph0:
        p = ph0["phases"]
        tot = sum((p.get(k) or {}).get("mean_ms") or 0 for k in PHASES) or 1
        ptab = "<table><tr><th>phase</th><th>mean ms</th><th>share</th><th>p95 ms</th><th>max ms</th></tr>" + "".join(
            f"<tr><td><span class='lg'><i style='background:{COLORS[k]};height:8px'></i></span>{k}</td><td class='num'>{fmt((p.get(k) or {}).get('mean_ms'), 3)}</td>"
            f"<td class='num'>{fmt(((p.get(k) or {}).get('mean_ms') or 0) / tot * 100, 1, '%')}</td><td class='num'>{fmt((p.get(k) or {}).get('p95_ms'), 3)}</td>"
            f"<td class='num'>{fmt((p.get(k) or {}).get('max_ms'), 3)}</td></tr>" for k in PHASES) + "</table>"
        fr = p.get("frame") or {}
        ptab += f"<p class='small'>GPU frame mean {fmt(fr.get('mean_ms'), 3)} ms, unattributed {fmt((p.get('unattributed') or {}).get('mean_ms'), 3)} ms; frames measured {ph0.get('frames_measured', 'no data')}, dropped (timer not ready) {ph0.get('frames_dropped_not_ready', 'no data')}. Renderer: {E(str(ph0.get('gl_renderer')))}</p>"
    abt = ""
    if ab_rows:
        abt = ("<table><tr><th>run</th><th>block</th><th>state</th><th>start s</th><th>frames</th><th>mean FPS</th></tr>" + "".join(
            f"<tr><td>{E(n)}</td><td class='num'>{b.get('index')}</td><td>{E(str(b.get('state')))}</td><td class='num'>{fmt((b.get('start_ms') or 0)/1000, 0)}</td>"
            f"<td class='num'>{b.get('frames')}</td><td class='num'>{fmt(b.get('mean_fps'), 1)}</td></tr>" for n, b in ab_rows) + "</table>")
    per_run_cost = "".join(f"<tr><td>{E(r['name'])}</td><td class='num'>{fmt(((r.get('phases') or {}).get('overhead_ab') or {}).get('mean_fps_off'), 1)}</td>"
                           f"<td class='num'>{fmt(((r.get('phases') or {}).get('overhead_ab') or {}).get('mean_fps_on'), 1)}</td>"
                           f"<td class='num'>{fmt(((r.get('phases') or {}).get('overhead_ab') or {}).get('percent_cost'), 2, '%')}</td></tr>" for r in runs)
    phases_html = phase_bar(ph0) + ptab
    phases_html += ("<h2 style='margin-top:14px'>On/off A/B overhead</h2><table><tr><th>run</th><th>FPS off</th><th>FPS on</th><th>cost</th></tr>" + per_run_cost +
                    f"<tr><td><b>pooled blocks</b></td><td class='num'>{fmt(statistics.mean(offs) if offs else None, 1)}</td><td class='num'>{fmt(statistics.mean(ons) if ons else None, 1)}</td><td class='num'><b>{fmt(pooled, 2, '%')}</b></td></tr></table>"
                    "<details><summary class='small'>per-block table</summary>" + (abt or '<div class="nodata">no data</div>') + "</details>")

    # ---- CPU tree
    tree, tnote = (None, "no runs")
    for r in runs:
        if r.get("speedscope_path") and os.path.exists(r["speedscope_path"]):
            tree, tnote = build_tree(r["speedscope_path"])
            tnote = f"{r['name']}: {tnote}"
            break
    else:
        tnote = "no speedscope.json in any capture bundle"
    cpu_html = (f"<p class='small'>{E(tnote)}. Click a row to expand. Percent = share of that thread's sampled time.</p><div id='cpu-tree' class='tree'></div>"
                if tree else f"<div class='nodata'>no data: {E(tnote)}</div>")
    cpu_json = json.dumps(tree).replace("</", "<\\/") if tree else "null"

    # ---- gates
    rcs = [gate_rc.get(os.path.normcase(r["folder"])) for r in runs]
    gates = [
        ("VRAM pairing (DXGI local vs PDH Local)", f"|delta| <= {VRAM_BAR:.0f}% settled, all runs", ", ".join(fmt(x, 3, "%") for x in vdiff) or "no data", vram_all,
         "Same quantity through two APIs; pass means adapter pick, interop and process attribution agree." if vram_all else "One or more runs missing a gate value or outside the bar."),
        ("gpu-phases overhead", f"< {PHASE_BAR:.0f}% FPS cost (pooled and each run)", fmt(pooled, 2, "%") + (" | per run " + ", ".join(fmt(c, 2, "%") for c in costs) if costs else ""), phase_pass,
         "Alternating on/off blocks inside one launch cancel drift between launches." if pooled is not None else "No overhead_ab blocks captured."),
        ("Capture completed", "runner rc 0 or 7, bundle has gpu-phases.json", ", ".join(str(x) for x in rcs) or "no data",
         (all(x in (0, 7) for x in rcs) if rcs and all(x is not None for x in rcs) else None), "Runner exit codes from the gates file."),
    ]
    gates_html = "<table><tr><th>gate</th><th>bar</th><th>measured</th><th>verdict</th><th>reasoning</th></tr>" + "".join(
        f"<tr><td>{E(g)}</td><td>{E(b)}</td><td class='num'>{m}</td><td class='{verdict_cls(p)}'>{verdict_txt(p)}</td><td class='small'>{E(why)}</td></tr>" for g, b, m, p, why in gates) + "</table>"

    limits = [
        "VRAM gate: DXGI CurrentUsage and PDH Local Usage both read the same dxgkrnl bookkeeping through different APIs. A pass proves wiring (adapter, interop, process attribution), not physical bytes.",
        "gpu-phases is EXPERIMENTAL: GL timer queries per phase; unattributed time is GPU work between the marked boundaries. Headline FPS figures include the gpu-phases on blocks.",
        "Per-mod GPU time is permanently excluded: draw calls batch across mods, so it cannot be attributed honestly.",
        f"Present mode for every FPS figure: {pm_txt}. FPS shifts about 4.4% between present modes; compare within one mode only.",
    ] + limits
    lim_html = "<ul class='lim'>" + "".join(f"<li>{E(x)}</li>" for x in limits) + "</ul>"

    gen = dt.datetime.now().strftime("%Y-%m-%d %H:%M")
    page = f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{E(a.title)}</title><style>{CSS}</style></head><body>
<header><h1>{E(a.title)}</h1><div class="sub">generated {gen} from {len(runs)} capture run(s) | self-contained, offline</div></header><main>
<div class="cards" id="cards">{''.join(cards)}</div>
<section id="context"><h2>Run context</h2><div class="strip">{strip_html}</div></section>
<section id="vram"><h2>VRAM</h2>{vram_html}</section>
<section id="phases"><h2>GPU phases <span class="tag">EXPERIMENTAL</span></h2>{phases_html}</section>
<section id="cpu"><h2>CPU sampling</h2>{cpu_html}</section>
<section id="gates"><h2>Gate verdicts</h2>{gates_html}</section>
<section id="limits"><h2>Honest limits</h2>{lim_html}</section>
</main><script type="application/json" id="cpu-data">{cpu_json}</script><script>{JS}</script></body></html>"""
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(page)
    print(f"wrote {a.out} {os.path.getsize(a.out):,} bytes | runs {len(runs)} | VRAM {verdict_txt(vram_all)} | phases {fmt(pooled, 2, '%')} {verdict_txt(phase_pass)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
