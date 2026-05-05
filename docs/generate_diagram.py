import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches

fig, ax = plt.subplots(figsize=(16, 10))
ax.set_xlim(0, 16)
ax.set_ylim(0, 10)
ax.axis("off")
fig.patch.set_facecolor("white")

TOOL_COLOR   = "#AED6F1"
HELPER_COLOR = "#A9DFBF"
MODEL_COLOR  = "#FAD7A0"
BORDER       = "#2C3E50"

def box(x, y, w, h, label, color):
    ax.add_patch(mpatches.FancyBboxPatch(
        (x, y), w, h,
        boxstyle="round,pad=0.07",
        linewidth=1.3, edgecolor=BORDER, facecolor=color, zorder=2))
    ax.text(x + w / 2, y + h / 2, label,
            ha="center", va="center", fontsize=8.5,
            fontweight="bold", color="#1A1A1A", zorder=3,
            multialignment="center")

def arr(x1, y1, x2, y2):
    ax.annotate("", xy=(x2, y2), xytext=(x1, y1),
                arrowprops=dict(arrowstyle="->", color="#555555",
                                lw=1.1, connectionstyle="arc3,rad=0.0"),
                zorder=1)

# ── Tool classes ──────────────────────────────────────────────
box(0.3,  8.2, 2.6, 0.9, "HelloTool",                TOOL_COLOR)
box(3.3,  8.2, 2.8, 0.9, "HelloToolWithAuth",        TOOL_COLOR)
box(6.5,  8.2, 3.2, 0.9, "ApplicationInsightsTool",  TOOL_COLOR)
box(10.3, 8.2, 2.6, 0.9, "SnippetsTool",             TOOL_COLOR)

# ── Helper / Service classes ──────────────────────────────────
box(0.3,  5.6, 2.6, 0.9, "CredentialBuilder",        HELPER_COLOR)
box(3.3,  5.6, 3.0, 0.9, "ResourceDiscovery\nService", HELPER_COLOR)
box(6.8,  5.6, 2.4, 0.9, "OutputFormatter",          HELPER_COLOR)
box(9.8,  5.6, 2.4, 0.9, "ReportBuilder",            HELPER_COLOR)
box(12.8, 5.6, 2.8, 0.9, "LogsTableReader",          HELPER_COLOR)

box(5.0,  3.4, 2.8, 0.9, "KqlQueryService",          HELPER_COLOR)
box(8.4,  3.4, 2.4, 0.9, "KqlQueries\n(static)",     HELPER_COLOR)

# ── Model / Record classes ────────────────────────────────────
box(0.3,  1.2, 2.4, 0.9, "Snippet",                  MODEL_COLOR)
box(3.8,  1.2, 2.8, 0.9, "AiDiscoveryResult",        MODEL_COLOR)
box(7.2,  1.2, 3.0, 0.9, "AppInsightsResource",      MODEL_COLOR)

# ── Arrows: Tools → Helpers/Models ───────────────────────────
arr(4.7,  8.2, 1.6,  6.5)   # HelloToolWithAuth → CredentialBuilder
arr(7.2,  8.2, 1.6,  6.5)   # AppInsightsTool   → CredentialBuilder
arr(7.8,  8.2, 4.8,  6.5)   # AppInsightsTool   → ResourceDiscoveryService
arr(8.1,  8.2, 8.0,  6.5)   # AppInsightsTool   → OutputFormatter
arr(8.5,  8.2, 11.0, 6.5)   # AppInsightsTool   → ReportBuilder
arr(11.6, 8.2, 1.5,  2.1)   # SnippetsTool      → Snippet

# ── Arrows: Helpers → Helpers ─────────────────────────────────
arr(11.0, 5.6, 6.4,  4.3)   # ReportBuilder     → KqlQueryService
arr(11.4, 5.6, 13.8, 6.5)   # ReportBuilder     → LogsTableReader  (up-right)
arr(10.6, 5.6, 8.0,  6.5)   # ReportBuilder     → OutputFormatter
arr(6.4,  3.8, 7.6,  6.5)   # KqlQueryService   → OutputFormatter
arr(6.8,  3.8, 14.2, 6.5)   # KqlQueryService   → LogsTableReader
arr(7.8,  3.85, 8.4, 3.85)  # KqlQueryService   → KqlQueries

# ── Arrows: Helpers → Models ─────────────────────────────────
arr(4.8,  5.6, 5.2,  2.1)   # ResourceDiscoveryService → AiDiscoveryResult
arr(5.5,  5.6, 8.7,  2.1)   # ResourceDiscoveryService → AppInsightsResource

# ── Legend ───────────────────────────────────────────────────
legend_items = [
    (TOOL_COLOR,   "Tool (Azure Function)"),
    (HELPER_COLOR, "Helper / Service"),
    (MODEL_COLOR,  "Model / Record"),
]
sq = 0.32   # square side length
lx = 12.2   # left edge of legend box
ly = 0.25   # bottom edge of legend box
lw = 3.5
lh = len(legend_items) * 0.52 + 0.22

ax.add_patch(mpatches.FancyBboxPatch(
    (lx, ly), lw, lh,
    boxstyle="round,pad=0.07",
    linewidth=1, edgecolor="#AAAAAA", facecolor="#F9F9F9", zorder=4))

for i, (color, label) in enumerate(legend_items):
    iy = ly + lh - 0.42 - i * 0.52
    ax.add_patch(mpatches.FancyBboxPatch(
        (lx + 0.18, iy), sq, sq,
        boxstyle="round,pad=0.02",
        linewidth=1.1, edgecolor=BORDER, facecolor=color, zorder=5))
    ax.text(lx + 0.18 + sq + 0.15, iy + sq / 2, label,
            ha="left", va="center", fontsize=8, color="#1A1A1A", zorder=5)

ax.set_title("FunctionsMcpTool – Class Diagram",
             fontsize=13, fontweight="bold", pad=10, color="#1A1A1A")

plt.tight_layout()
out = r"c:\Users\erikk\Desktop\Azure-MCP-server-self-hosted-2\docs\klassediagram.png"
plt.savefig(out, dpi=150, bbox_inches="tight", facecolor="white")
print(f"Saved: {out}")
