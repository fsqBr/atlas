import "@testing-library/jest-dom/vitest";

// Recharts measures its container; jsdom has no layout, so give ResponsiveContainer a size.
Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get: () => 600 });
Object.defineProperty(HTMLElement.prototype, "offsetHeight", { configurable: true, get: () => 300 });
Element.prototype.getBoundingClientRect = () =>
  ({ width: 600, height: 300, top: 0, left: 0, right: 600, bottom: 300, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
if (typeof window !== "undefined" && !("ResizeObserver" in window)) {
  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  (window as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;
}
