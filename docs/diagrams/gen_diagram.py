"""Generates docs/diagrams/covenant-architecture.{drawio,svg} from one node/edge list.
The SVG embeds the draw.io XML (content attribute) so draw.io can reopen the SVG directly."""
import html, sys, os

# palette (matches README mermaid classDefs)
C = {
    "caller":   ("#e3f2fd", "#1565c0", "#0d47a1"),
    "govern":   ("#fff3e0", "#ef6c00", "#e65100"),
    "provider": ("#e8f5e9", "#2e7d32", "#1b5e20"),
    "evidence": ("#f3e5f5", "#6a1b9a", "#4a148c"),
    "denied":   ("#ffebee", "#c62828", "#b71c1c"),
    "neutral":  ("#f5f5f5", "#616161", "#212121"),
    "none":     ("none",    "#616161", "#212121"),
}

# id, kind, x, y, w, h, lines, opts
N_DETAIL = [
    ("perimeter", "none", 260, 60, 920, 830, ["Customer perimeter — no prompt, response, or metadata leaves; egress only to the allow-listed models"], {"container": True, "dashed": True}),
    ("appliance", "none", 290, 120, 690, 560, ["Covenant appliance — single .NET process, NativeAOT target"], {"container": True}),

    ("c_openai", "caller", 30, 190, 190, 70, ["OpenAI-compatible clients", "SDKs · Open WebUI · Bearer key"], {}),
    ("c_anthropic", "caller", 30, 290, 190, 70, ["Anthropic clients", "x-api-key · Messages API"], {}),
    ("c_admin", "caller", 30, 720, 190, 70, ["Admins / auditors", "admin token"], {}),

    ("ingress", "caller", 310, 180, 150, 200, ["Ingress", "/v1/chat/completions", "/v1/messages", "/v1/models", "", "→ one canonical request"], {}),

    ("audit", "evidence", 490, 160, 470, 250, ["Audit stage — outermost; records allow, deny, and error alike", "metadata + SHA-256 prompt fingerprint — never content"], {"container": True}),
    ("s_auth", "govern", 510, 210, 100, 55, ["Auth + shape", "keys · text only"], {}),
    ("s_classify", "govern", 620, 210, 100, 55, ["Classify", "PII / PHI"], {}),
    ("s_policy", "govern", 730, 210, 100, 55, ["Policy", "route, complexity"], {}),
    ("s_cache", "govern", 840, 210, 100, 55, ["Cache", "team-scoped TTL"], {}),
    ("s_rate", "govern", 840, 320, 100, 55, ["Rate limit", "team + global"], {}),
    ("s_budget", "govern", 730, 320, 100, 55, ["Budget", "caps, kill switch"], {}),
    ("s_provider", "provider", 620, 320, 100, 55, ["Provider call", "adapter:model"], {}),
    ("s_attr", "evidence", 510, 320, 100, 55, ["Attribute cost", "team, workflow"], {}),

    ("denied", "denied", 490, 440, 210, 70, ["Denied — fail-closed", "400 · 401 · 403 · 429 · 502", "no provider call, still audited"], {}),
    ("response", "caller", 730, 440, 230, 70, ["Response + usage", "OpenAI or Anthropic wire", "buffered or SSE stream"], {}),

    ("log", "evidence", 310, 560, 200, 80, ["Hash-chained audit log", "append-only · SHA-256 links", "tampered chain refuses boot"], {}),
    ("anchors", "evidence", 530, 560, 220, 80, ["Chain-head anchors", "Nth head hash → separate storage", "end-truncation detectable (ADR-0007)"], {}),
    ("ledger", "evidence", 770, 560, 190, 80, ["Spend ledger", "replayed from the log at boot", "read by the Budget stage"], {}),

    ("local", "provider", 1000, 380, 160, 70, ["Local model", "Ollama / vLLM", "in-perimeter, PII/PHI"], {}),

    ("p_openai", "provider", 1220, 180, 160, 60, ["OpenAI", "openai adapter"], {}),
    ("p_anthropic", "provider", 1220, 270, 160, 60, ["Anthropic", "anthropic adapter"], {}),
    ("egress", "none", 1200, 130, 200, 30, ["Egress allow-list only"], {"plain": True}),

    ("dash", "caller", 310, 730, 200, 80, ["Admin dashboard", "/admin/ui · FinOps savings", "kill switch · live SSE · reset"], {}),
    ("export", "evidence", 540, 730, 200, 80, ["Evidence export", "/admin/evidence", "verifies chain + anchors"], {}),
    ("neo4j", "evidence", 770, 730, 190, 80, ["Neo4j evidence graph", "optional (ADR-0006)", "verified prefix only"], {"dashed": True}),
    ("otel", "neutral", 990, 730, 170, 80, ["OTel collector", "optional (ADR-0003)", "spans, never content"], {"dashed": True}),
]

# source, target, label, dashed, svg points (explicit polyline for the SVG render)
E_DETAIL = [
    ("c_openai", "ingress", "", False, [(220, 225), (310, 225)]),
    ("c_anthropic", "ingress", "", False, [(220, 325), (310, 325)]),
    ("ingress", "s_auth", "", False, [(460, 237), (510, 237)]),
    ("s_auth", "s_classify", "", False, [(610, 237), (620, 237)]),
    ("s_classify", "s_policy", "", False, [(720, 237), (730, 237)]),
    ("s_policy", "s_cache", "", False, [(830, 237), (840, 237)]),
    ("s_cache", "s_rate", "miss", False, [(890, 265), (890, 320)]),
    ("s_rate", "s_budget", "", False, [(840, 347), (830, 347)]),
    ("s_budget", "s_provider", "", False, [(730, 347), (720, 347)]),
    ("s_provider", "s_attr", "", False, [(620, 347), (610, 347)]),
    ("s_attr", "response", "", False, [(560, 375), (560, 395), (845, 395), (845, 440)]),
    ("audit", "denied", "any stage denies", False, [(595, 410), (595, 440)]),
    ("s_provider", "local", "", False, [(670, 375), (670, 425), (1000, 425)]),
    ("s_provider", "p_openai", "", False, [(985, 425), (985, 210), (1220, 210)]),
    ("s_provider", "p_anthropic", "", False, [(985, 290), (1220, 290)]),
    ("audit", "log", "every request", False, [(490, 395), (475, 395), (475, 545), (410, 545), (410, 560)]),
    ("log", "anchors", "", False, [(510, 600), (530, 600)]),
    ("log", "ledger", "boot replay", False, [(510, 625), (525, 625), (525, 655), (755, 655), (755, 625), (770, 625)]),
    ("log", "export", "", False, [(400, 640), (400, 690), (640, 690), (640, 730)]),
    ("log", "neo4j", "", True, [(420, 640), (420, 705), (865, 705), (865, 730)]),
    ("audit", "otel", "", True, [(960, 390), (970, 390), (970, 720), (1075, 720), (1075, 730)]),
    ("c_admin", "dash", "", False, [(220, 755), (310, 755)]),
    ("c_admin", "export", "", False, [(220, 770), (270, 770), (270, 825), (640, 825), (640, 810)]),
]

N, E, SIZE, TITLE = [], [], [None], [None]

# ---------------- draw.io ----------------
def drawio():
    cells = ['<mxCell id="0"/>', '<mxCell id="1" parent="0"/>']
    for nid, kind, x, y, w, h, lines, o in N:
        fill, stroke, font = C[kind]
        label = "&lt;b&gt;" + html.escape(lines[0]) + "&lt;/b&gt;" + ("&lt;br&gt;" + "&lt;br&gt;".join(html.escape(l) for l in lines[1:]) if len(lines) > 1 else "")
        style = f"rounded=1;whiteSpace=wrap;html=1;fillColor={fill};strokeColor={stroke};fontColor={font};"
        if o.get("container"): style += "verticalAlign=top;align=left;spacingLeft=8;spacingTop=2;container=1;"
        if o.get("dashed"): style += "dashed=1;"
        if o.get("plain"): style = "text;html=1;align=center;verticalAlign=middle;fontStyle=2;fontColor=#616161;"
        if kind == "none" and not o.get("plain"): style += "strokeWidth=2;"
        cells.append(f'<mxCell id="{nid}" value="{label}" style="{style}" vertex="1" parent="1"><mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry"/></mxCell>')
    for i, (s, t, label, dashed, _) in enumerate(E):
        style = "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=block;endFill=1;strokeColor=#455a64;" + ("dashed=1;" if dashed else "")
        cells.append(f'<mxCell id="e{i}" value="{html.escape(label)}" style="{style}" edge="1" parent="1" source="{s}" target="{t}"><mxGeometry relative="1" as="geometry"/></mxCell>')
    return ('<mxfile host="app.diagrams.net" agent="Covenant gen_diagram.py" version="24.0.0">'
            '<diagram name="Covenant architecture" id="covenant-arch">'
            f'<mxGraphModel dx="{SIZE[0][0]}" dy="{SIZE[0][1]}" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="{SIZE[0][0]+30}" pageHeight="{SIZE[0][1]+10}" math="0" shadow="0">'
            '<root>' + "".join(cells) + '</root></mxGraphModel></diagram></mxfile>')

# ---------------- SVG ----------------
def svg(embed_xml):
    W, H = SIZE[0]
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
           f'font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif" font-size="12" content="{html.escape(embed_xml, quote=True)}">',
           '',
           f'<rect width="{W}" height="{H}" fill="#ffffff"/>',
           f'<text x="20" y="34" font-size="20" font-weight="700" fill="#212121">{html.escape(TITLE[0])}</text>',
           (SIZE[0][0] > 1300 and '<text x="20" y="54" font-size="12" fill="#616161">Every request passes the same ordered governance pipeline; the hash-chained audit log is the evidence of record, everything else is a derived view.</text>' or '')]
    for nid, kind, x, y, w, h, lines, o in N:
        fill, stroke, font = C[kind]
        dash = ' stroke-dasharray="6 4"' if o.get("dashed") else ""
        if o.get("plain"):
            out.append(f'<text x="{x + w/2}" y="{y + h/2 + 4}" text-anchor="middle" font-style="italic" fill="#616161">{html.escape(lines[0])}</text>')
            continue
        sw = 2 if kind == "none" else 1.2
        out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"{dash}/>')
        if o.get("container"):
            out.append(f'<text x="{x + 10}" y="{y + 18}" font-weight="700" fill="{font}">{html.escape(lines[0])}</text>')
            for j, l in enumerate(lines[1:]):
                out.append(f'<text x="{x + 10}" y="{y + 34 + j*14}" font-size="11" fill="{font}" fill-opacity="0.85">{html.escape(l)}</text>')
            continue
        n = len(lines); lh = 15
        y0 = y + h/2 - (n - 1) * lh / 2 + 4
        for i, l in enumerate(lines):
            fw = ' font-weight="700"' if i == 0 else ' fill-opacity="0.85"'
            out.append(f'<text x="{x + w/2}" y="{y0 + i*lh}" text-anchor="middle" fill="{font}"{fw}>{html.escape(l)}</text>')
    for s, t, label, dashed, pts in E:
        d = " ".join(f"{px},{py}" for px, py in pts)
        dash = ' stroke-dasharray="6 4"' if dashed else ""
        out.append(f'<polyline points="{d}" fill="none" stroke="#455a64" stroke-width="1.4"{dash}/>')
        # explicit arrowhead (renderer-independent): triangle along the final segment
        (x1, y1), (x2, y2) = pts[-2], pts[-1]
        dx, dy = x2 - x1, y2 - y1
        L = (dx*dx + dy*dy) ** 0.5 or 1
        ux, uy = dx / L, dy / L
        bx, by = x2 - ux * 9, y2 - uy * 9
        out.append(f'<polygon points="{x2},{y2} {bx - uy*4:.1f},{by + ux*4:.1f} {bx + uy*4:.1f},{by - ux*4:.1f}" fill="#455a64"/>')
        if label:
            segs = list(zip(pts, pts[1:]))
            (ax, ay), (cx, cy) = max(segs, key=lambda p: abs(p[1][0]-p[0][0]) + abs(p[1][1]-p[0][1]))
            mx, my = (ax + cx) / 2, (ay + cy) / 2
            w = len(label) * 6.2 + 8
            out.append(f'<rect x="{mx - w/2:.1f}" y="{my - 7}" width="{w:.1f}" height="14" rx="3" fill="#ffffff"/>')
            out.append(f'<text x="{mx}" y="{my + 4}" text-anchor="middle" font-size="10.5" fill="#455a64">{html.escape(label)}</text>')
    # legend
    lx, ly = 20, 100
    for i, (name, kind) in enumerate([("Callers / wire", "caller"), ("Governance stages", "govern"), ("Providers", "provider"), ("Evidence", "evidence"), ("Denial (fail-closed)", "denied")]):
        fill, stroke, font = C[kind]
        out.append(f'<rect x="{lx}" y="{ly + i*20 - 10}" width="14" height="14" rx="3" fill="{fill}" stroke="{stroke}"/>')
        out.append(f'<text x="{lx + 20}" y="{ly + i*20 + 1}" font-size="11" fill="#212121">{name}</text>')
    out.append('</svg>')
    return "\n".join(out)

# ---------------- high-level overview (one glance) ----------------
N_HL = [
    ("perimeter", "none", 250, 50, 700, 420, ["Customer perimeter — nothing leaves except allow-listed model egress"], {"container": True, "dashed": True}),
    ("clients", "caller", 30, 230, 180, 90, ["Any AI client", "OpenAI or Anthropic wire", "SDKs, Open WebUI, agents"], {}),
    ("covenant", "govern", 290, 120, 360, 150, ["Covenant — one governed pipeline", "auth → shape → classify → policy → cache → rate limit", "→ budget / kill switch → provider → attribute cost", "", "fail-closed: no match, no call"], {}),
    ("evidence", "evidence", 290, 320, 360, 110, ["Tamper-evident evidence", "hash-chained audit log + anchored head", "spend ledger · evidence export · dashboard"], {}),
    ("local", "provider", 700, 320, 210, 90, ["In-perimeter model", "Ollama / vLLM", "PII / PHI never leave"], {}),
    ("external", "provider", 1000, 130, 200, 110, ["Allow-listed providers", "OpenAI · Anthropic", "egress only to these"], {}),
    ("admins", "caller", 30, 360, 180, 70, ["Admins / auditors", "kill switch · evidence"], {}),
]
E_HL = [
    ("clients", "covenant", "every request", False, [(210, 265), (250, 265), (250, 195), (290, 195)]),
    ("covenant", "external", "permitted routes", False, [(650, 185), (1000, 185)]),
    ("covenant", "local", "sensitive data", False, [(650, 245), (805, 245), (805, 320)]),
    ("covenant", "evidence", "allow, deny, error alike", False, [(470, 270), (470, 320)]),
    ("admins", "evidence", "", False, [(210, 395), (290, 395)]),
]

if __name__ == "__main__":
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    for name, nodes, edges, size, title in [
        ("covenant-architecture", N_DETAIL, E_DETAIL, (1420, 910), "Covenant — in-perimeter AI inference governance & FinOps control plane"),
        ("covenant-overview", N_HL, E_HL, (1240, 500), "Covenant at a glance"),
    ]:
        N[:] = nodes; E[:] = edges; SIZE[0] = size; TITLE[0] = title
        xml = drawio()
        open(os.path.join(outdir, f"{name}.drawio"), "w").write(xml)
        open(os.path.join(outdir, f"{name}.svg"), "w").write(svg(xml))
    print("ok")
