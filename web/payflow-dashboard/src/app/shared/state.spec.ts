import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiError } from '../core/api';
import { Loadable, LoadStatus, loadable } from '../core/loadable';
import { settle } from '../../testing/axe';
import { StateView } from './state';

@Component({
  imports: [StateView],
  template: `<pf-state [of]="data" emptyText="No payments yet."><p class="content">Loaded content</p></pf-state>`,
})
class Host {
  data!: Loadable<string[]>;
}

function fake(status: LoadStatus, error?: ApiError): Loadable<string[]> {
  return {
    status: signal(status),
    value: signal(status === 'ready' ? ['x'] : undefined),
    error: signal(error),
    load: vi.fn(async () => undefined),
  };
}

describe('StateView', () => {
  function render(data: Loadable<string[]>) {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.data = data;
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('announces loading politely and hides the content', () => {
    const el = render(fake('loading'));
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Loading');
    expect(el.querySelector('.content')).toBeNull();
  });

  it('shows the empty message instead of an empty table', () => {
    const el = render(fake('empty'));
    expect(el.textContent).toContain('No payments yet.');
    expect(el.querySelector('.content')).toBeNull();
  });

  it('shows the error with its correlation id and retries on request', () => {
    const data = fake('error', new ApiError(500, { detail: 'Database unavailable.', correlationId: 'abc123' }));
    const el = render(data);
    const alert = el.querySelector('[role="alert"]')!;
    expect(alert.textContent).toContain('Database unavailable.');
    expect(alert.textContent).toContain('abc123');
    (alert.querySelector('button') as HTMLButtonElement).click();
    expect(data.load).toHaveBeenCalledTimes(1);
    expect(el.querySelector('.content')).toBeNull();
  });

  it('renders the content only when ready', () => {
    expect(render(fake('ready')).querySelector('.content')?.textContent).toBe('Loaded content');
  });
});

describe('loadable', () => {
  it('moves through loading to ready, empty or error, and ignores stale responses', async () => {
    let resolveFirst!: (v: string[]) => void;
    const answers = [new Promise<string[]>((r) => (resolveFirst = r)), Promise.resolve(['second'])];
    const l = loadable(() => answers.shift()!, (v) => v.length === 0);

    const first = l.load();
    expect(l.status()).toBe('loading');
    await l.load(); // a newer request (e.g. filter changed) finishes first
    resolveFirst(['stale']);
    await first;
    expect(l.value()).toEqual(['second']);
    expect(l.status()).toBe('ready');

    const empty = loadable(async () => [] as string[], (v) => v.length === 0);
    await empty.load();
    expect(empty.status()).toBe('empty');

    const failing = loadable(async () => {
      throw new ApiError(503, { title: 'Unavailable' });
    });
    await failing.load();
    expect(failing.status()).toBe('error');
    expect(failing.error()?.status).toBe(503);
  });

  it('is used by components without zone.js', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.data = loadable(async () => ['a']);
    void fixture.componentInstance.data.load();
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('.content')).not.toBeNull();
  });
});
