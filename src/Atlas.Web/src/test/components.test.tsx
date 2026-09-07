import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Markdown } from "../components/Markdown";
import { HBars, MirrorBars, RangeBars, RoadmapGantt, ScoreRing, TrendLine } from "../components/charts";
import { EmptyState, Tile, scoreTone, signed } from "../components/ui";

describe("Markdown", () => {
  it("renders the subset and never injects markup", () => {
    const { container } = render(<Markdown text={"## Phase\nText with **bold** and `code` <b>raw</b>\n- one\n- two\n1. step"} />);
    expect(container.querySelector("h3")?.textContent).toBe("Phase");
    expect(container.querySelector("strong")?.textContent).toBe("bold");
    expect(container.querySelector("code")?.textContent).toBe("code");
    expect(container.querySelectorAll("ul li")).toHaveLength(2);
    expect(container.querySelectorAll("ol li")).toHaveLength(1);
    expect(container.querySelector("b")).toBeNull();
    expect(container.textContent).toContain("<b>raw</b>");
  });

  it("renders fenced code with diff line classes", () => {
    const md = ["## Patch", "```diff", "--- a/x.cs", "+++ b/x.cs", "@@ -1,2 +1,2 @@", "-var a = 1;", "+var a = 2;", " context", "```", "After"].join("\n");
    const { container } = render(<Markdown text={md} />);
    const pre = container.querySelector("pre.code.lang-diff");
    expect(pre).not.toBeNull();
    expect(pre!.querySelectorAll(".diff-add")).toHaveLength(1);
    expect(pre!.querySelectorAll(".diff-del")).toHaveLength(1);
    expect(pre!.querySelectorAll(".diff-file")).toHaveLength(2);
    expect(pre!.querySelector(".diff-hunk")?.textContent).toContain("@@ -1,2 +1,2 @@");
    expect(container.querySelector("p")?.textContent).toBe("After");
  });
});

describe("ui", () => {
  it("maps scores to tones like the health model", () => {
    expect(scoreTone(null)).toBe("neutral");
    expect(scoreTone(10)).toBe("critical");
    expect(scoreTone(45)).toBe("high");
    expect(scoreTone(70)).toBe("medium");
    expect(scoreTone(95)).toBe("ok");
    expect(signed(3)).toBe("+3");
    expect(signed(-2)).toBe("-2");
    expect(signed(null)).toBe("—");
  });

  it("tile shows value, label and a signed delta with direction", () => {
    render(<Tile value="42" unit="/100" label="Health" delta={-5} />);
    expect(screen.getByText("42")).toBeInTheDocument();
    expect(screen.getByText("Health")).toBeInTheDocument();
    expect(screen.getByText("▼ -5")).toHaveClass("down");
  });

  it("empty state carries the call to action", () => {
    render(<EmptyState title="Nothing yet" text="Start here" action={<button>Go</button>} />);
    expect(screen.getByRole("button", { name: "Go" })).toBeInTheDocument();
  });
});

describe("charts", () => {
  it("score ring reads the score and falls back to a dash", () => {
    const { container, rerender } = render(<ScoreRing score={56} risk="High" />);
    expect(container.querySelector(".ring")).toHaveClass("risk-high");
    expect(container.textContent).toContain("56");
    rerender(<ScoreRing score={null} />);
    expect(container.textContent).toContain("—");
    expect(container.querySelector(".ring")).toHaveClass("risk-none");
  });

  it("bar charts show the empty text instead of crashing on no data", () => {
    render(<HBars data={[]} emptyText="No data" />);
    expect(screen.getByText("No data")).toBeInTheDocument();
    render(<TrendLine points={[{ x: "#1", value: 50 }]} emptyText="Need two runs" />);
    expect(screen.getByText("Need two runs")).toBeInTheDocument();
  });

  it("bar charts render an svg when there is data", () => {
    const { container } = render(<HBars data={[{ name: "Security", value: 12 }, { name: "Quality", value: 3 }]} />);
    expect(container.querySelector(".chart")).toBeInTheDocument();
    const mirror = render(<MirrorBars rows={[{ name: "Critical", a: 1, b: 5 }]} labelA="A" labelB="B" />);
    expect(mirror.container.querySelector(".chart")).toBeInTheDocument();
  });

  it("gantt lays phases out sequentially and range bars place the band", () => {
    const { container } = render(
      <RoadmapGantt
        unit="mo"
        phases={[
          { key: "a", name: "Baseline", durationMonths: { optimistic: 0.5, likely: 1, conservative: 2 }, effortShare: 0.25 },
          { key: "b", name: "Security", durationMonths: { optimistic: 1, likely: 2, conservative: 2 }, effortShare: 0.75 },
        ]}
      />,
    );
    const bars = container.querySelectorAll<HTMLElement>(".gantt-bar");
    expect(bars).toHaveLength(2);
    expect(bars[0].style.left).toBe("0%");
    expect(bars[1].style.left).toBe("25%"); // starts where the first likely bar ends (1 of 4 total months)
    expect(container.querySelectorAll(".gantt-tail")).toHaveLength(1); // only the phase with a conservative tail

    const ranges = render(<RangeBars rows={[{ name: "Health", p25: 40, p50: 60, p75: 80, best: 95, worst: 10 }]} />);
    const band = ranges.container.querySelector<HTMLElement>(".range-band")!;
    expect(band.style.left).toBe("40%");
    expect(band.style.width).toBe("40%");
    expect(ranges.container.textContent).toContain("60");
  });
});
