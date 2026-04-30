import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches

fig, ax = plt.subplots(figsize=(18, 13))
ax.set_xlim(0, 18)
ax.set_ylim(0, 13)
ax.axis("off")
fig.patch.set_facecolor("#F8F9FA")

HEADER_TOOL   = "#2E86AB"
HEADER_MODEL  = "#A23B72"
HEADER_CONST  = "#F18F01"
HEADER_PROG   = "#4CAF50"
HEADER_EXT    = "#888888"
TEXT_WHITE    = "white"
TEXT_DARK     = "#222222"

def draw_class(ax, x, y, w, h, title, header_color, sections,
               stereotype=None, header_h=0.55):
    ax.add_patch(mpatches.FancyBboxPatch(
        (x, y), w, h, boxstyle="round,pad=0.05",
        linewidth=1.2, edgecolor=header_color, facecolor="white", zorder=2))
    ax.add_patch(mpatches.FancyBboxPatch(
        (x, y + h - header_h), w, header_h, boxstyle="round,pad=0.04",
        linewidth=0, facecolor=header_color, zorder=3))
    cy = y + h - header_h / 2
    if stereotype:
        ax.text(x + w/2, cy + 0.13, f"«{stereotype}»",
                ha="center", va="center", fontsize=6.5,
                color=TEXT_WHITE, zorder=4, style="italic")
        ax.text(x + w/2, cy - 0.14, title,
                ha="center", va="center", fontsize=8.5,
                color=TEXT_WHITE, fontweight="bold", zorder=4)
    else:
        ax.text(x + w/2, cy, title,
                ha="center", va="center", fontsize=9,
                color=TEXT_WHITE, fontweight="bold", zorder=4)
    current_y = y + h - header_h
    for section in sections:
        ax.plot([x, x+w], [current_y, current_y],
                color=header_color, linewidth=0.8, zorder=3)
        for i, line in enumerate(section):
            ax.text(x + 0.12, current_y - 0.18 - i * 0.22, line,
                    ha="left", va="top", fontsize=6.8,
                    color=TEXT_DARK, zorder=4, fontfamily="monospace")
        current_y -= len(section) * 0.22 + 0.12

def draw_ext(ax, x, y, w, h, label):
    ax.add_patch(mpatches.FancyBboxPatch(
        (x, y), w, h, boxstyle="round,pad=0.05",
        linewidth=1, edgecolor=HEADER_EXT, facecolor="#EEEEEE",
        zorder=2, linestyle="dashed"))
    ax.text(x + w/2, y + h/2, label,
            ha="center", va="center", fontsize=7.5,
            color="#555555", style="italic", zorder=3)

def arrow(ax, x1, y1, x2, y2, color="#555", dashed=False):
    ax.annotate("", xy=(x2, y2), xytext=(x1, y1),
                arrowprops=dict(arrowstyle="->", color=color, lw=1.1,
                                linestyle=(0,(5,4)) if dashed else "solid"),
                zorder=1)

# ── PROGRAM (top-left) ───────────────────────────────────────────────────────
draw_class(ax, 0.4, 10.5, 3.2, 2.6, "Program", HEADER_PROG, sections=[
    ["+ CreateHostBuilder()"],
    ["  ► Registers: BlobServiceClient",
     "  ► Registers: AppInsights",
     "  ► ConfigureMcpTool(echo_message)",
     "  ► AddAzureFunctionsWorker"],
])

# ── TOOLS INFORMATION (top-right) ────────────────────────────────────────────
draw_class(ax, 13.2, 10.5, 4.5, 2.6, "ToolsInformation", HEADER_CONST,
           stereotype="sealed constants", sections=[
    ["EchoToolName / EchoToolDescription",
     "HelloToolName / HelloToolDescription",
     "HelloToolWithAuthName / ...",
     "GetSnippetToolName / ...",
     "SaveSnippetToolName / ...",
     "BatchSaveSnippetsToolName / ..."],
])

# ── HELLO TOOL ───────────────────────────────────────────────────────────────
draw_class(ax, 0.4, 6.8, 3.5, 3.1, "HelloTool", HEADER_TOOL,
           stereotype="McpTool", sections=[
    ["─ logger: ILogger<HelloTool>"],
    ["+ HelloTool(ILogger)"],
    ["+ SayHello(context) : string",
     "+ EchoMessage(context) : string"],
])

# ── HELLO TOOL WITH AUTH ──────────────────────────────────────────────────────
draw_class(ax, 4.5, 6.6, 4.2, 3.5, "HelloToolWithAuth", HEADER_TOOL,
           stereotype="McpTool", sections=[
    ["─ logger: ILogger<HelloToolWithAuth>",
     "─ hostEnv: IHostEnvironment",
     "─ GraphScopes: string[]"],
    ["+ HelloToolWithAuth(ILogger, IHostEnvironment)"],
    ["+ Run(context) : Task<string>",
     "─ BuildOnBehalfOfCredential(ctx)",
     "─ GetUserToken(transport)",
     "─ GetTenantId(transport)",
     "─ BuildClientAssertionCallback()"],
])

# ── SNIPPETS TOOL ─────────────────────────────────────────────────────────────
draw_class(ax, 9.3, 6.2, 4.3, 4.0, "SnippetsTool", HEADER_TOOL,
           stereotype="McpTool", sections=[
    ["─ logger: ILogger<SnippetsTool>",
     '─ BlobPath = "snippets/{name}.json"'],
    ["+ SnippetsTool(ILogger)"],
    ["+ GetSnippet(ctx, name, blob) : Snippet?",
     "+ GetSnippetWithMetadata(ctx, name, blob)",
     "    : CallToolResult",
     "+ SaveSnippet(snippet, ctx) : string",
     "+ BatchSaveSnippets(ctx, items)",
     "    : Task<string>",
     "─ GetBlobServiceClient() : BlobServiceClient"],
])

# ── SNIPPET (model) ───────────────────────────────────────────────────────────
draw_class(ax, 9.3, 3.8, 3.2, 2.0, "Snippet", HEADER_MODEL,
           stereotype="McpContent", sections=[
    ["+ Name    : string  [required]",
     "+ Content : string?"],
])

# ── EXTERNAL DEPENDENCIES ─────────────────────────────────────────────────────
draw_ext(ax, 0.4,  3.6, 2.8, 0.7,  "«interface»\nILogger<T>")
draw_ext(ax, 4.5,  3.6, 2.8, 0.7,  "«interface»\nIHostEnvironment")
draw_ext(ax, 13.2, 3.8, 4.5, 0.7,  "BlobServiceClient\n(Azure Storage)")
draw_ext(ax, 4.5,  2.2, 3.8, 0.9,
         "Azure.Identity\nTokenCredential / ChainedTokenCredential\n"
         "OnBehalfOfCredential / ManagedIdentityCredential")
draw_ext(ax, 9.3,  2.2, 3.0, 0.7,  "Microsoft.Graph\nGraphServiceClient")

# ── ARROWS ────────────────────────────────────────────────────────────────────
# Program → Tool classes
arrow(ax, 2.0, 10.5, 2.15, 9.9,  color=HEADER_PROG)
arrow(ax, 2.0, 10.5, 6.2,  10.1, color=HEADER_PROG)
arrow(ax, 2.0, 10.5, 11.4, 10.2, color=HEADER_PROG)

# Tool classes → ToolsInformation (dashed, uses constants)
for sx, sy in [(2.15, 9.9), (6.2, 10.1), (11.4, 10.2)]:
    arrow(ax, 13.2, 11.4, sx, sy, color=HEADER_CONST, dashed=True)

# ILogger → Tool classes
arrow(ax, 1.8, 4.3, 2.15, 6.8,  color=HEADER_EXT, dashed=True)
arrow(ax, 1.8, 4.3, 6.0,  6.6,  color=HEADER_EXT, dashed=True)
arrow(ax, 1.8, 4.3, 10.8, 6.2,  color=HEADER_EXT, dashed=True)

# IHostEnvironment → HelloToolWithAuth
arrow(ax, 5.9, 4.3, 6.2, 6.6, color=HEADER_EXT, dashed=True)

# SnippetsTool → Snippet
arrow(ax, 11.0, 6.2, 11.0, 5.8, color=HEADER_MODEL)

# SnippetsTool → BlobServiceClient
arrow(ax, 13.6, 6.2, 15.0, 4.5, color=HEADER_EXT, dashed=True)

# HelloToolWithAuth → Azure Identity
arrow(ax, 6.4, 6.6, 6.4, 3.1, color="#9B59B6", dashed=True)

# HelloToolWithAuth → Microsoft Graph
arrow(ax, 8.7, 7.0, 10.8, 2.9, color="#9B59B6", dashed=True)

# ── LEGEND ───────────────────────────────────────────────────────────────────
lx, ly = 0.4, 0.9
ax.text(lx, ly + 0.55, "Legend", fontsize=8, fontweight="bold", color=TEXT_DARK)
for i, (c, label) in enumerate([
    (HEADER_PROG,  "Program (DI / startup)"),
    (HEADER_TOOL,  "MCP Tool class"),
    (HEADER_MODEL, "Model / data class"),
    (HEADER_CONST, "Constants holder"),
    (HEADER_EXT,   "External dependency"),
]):
    rx = lx + i * 3.2
    ax.add_patch(mpatches.FancyBboxPatch(
        (rx, ly), 0.35, 0.35, boxstyle="round,pad=0.03",
        facecolor=c, edgecolor="none", zorder=5))
    ax.text(rx + 0.45, ly + 0.17, label,
            va="center", fontsize=7.5, color=TEXT_DARK)

ax.annotate("", xy=(lx+0.8, ly-0.22), xytext=(lx, ly-0.22),
            arrowprops=dict(arrowstyle="->", color="#333", lw=1.1))
ax.text(lx+0.9, ly-0.22, "uses / composition", va="center", fontsize=7.5, color=TEXT_DARK)

ax.annotate("", xy=(lx+4.8, ly-0.22), xytext=(lx+4.0, ly-0.22),
            arrowprops=dict(arrowstyle="->", color="#333", lw=1.1,
                            linestyle=(0,(5,4))))
ax.text(lx+4.9, ly-0.22, "dependency injection / dashed = optional",
        va="center", fontsize=7.5, color=TEXT_DARK)

# ── TITLE ─────────────────────────────────────────────────────────────────────
ax.text(9.0, 12.75, "FunctionsMcpTool — Klassediagram",
        ha="center", va="top", fontsize=14, fontweight="bold", color=TEXT_DARK)
ax.text(9.0, 12.4, "Azure Functions · Model Context Protocol (MCP)",
        ha="center", va="top", fontsize=9, color="#666666", style="italic")

plt.tight_layout(pad=0)
out = r"c:\Users\erikk\Desktop\Azure-MCP-server-self-hosted-2\docs\klassediagram.png"
plt.savefig(out, dpi=150, bbox_inches="tight", facecolor=fig.get_facecolor())
print(f"Saved: {out}")
