import axe from 'axe-core';

// Component tests render Material in jsdom and run axe; on a loaded CI machine, and several times
// slower again under coverage instrumentation, that can exceed Vitest's 5-second default. A slow
// test is not a failing one.
vi.setConfig({ testTimeout: 60_000 });

/**
 * Runs axe-core (WCAG 2.2 A/AA rules) on a rendered element and returns readable violations.
 * jsdom has no layout or rendering, so colour contrast cannot be measured here; the palette in
 * styles.scss is checked by hand against 4.5:1 and in the browser during the walkthrough.
 */
export async function axeViolations(element: Element): Promise<string[]> {
  const result = await axe.run(element, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
    rules: { 'color-contrast': { enabled: false } },
  });
  return result.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
}

/** Lets pending promises (loaders) and change detection settle. */
export async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
    await fixture.whenStable();
  }
}
