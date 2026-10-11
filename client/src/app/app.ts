import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FetchJobProgress } from './fetch-progress';
import { JobProgress, JobWatch } from './job-progress';

// One page, driven by the query string so the Playwright check can pick a case:
//   ?mode=open|cookie|bearer|fetch  &drop=N  &api=http://other-origin  &creds=1
@Component({
  selector: 'app-root',
  template: `
    <h1>Job {{ jobId }}</h1>
    @if (job(); as job) {
      <progress max="100" [value]="job.state().percent"></progress>
      <p>
        <span data-test="percent">{{ job.state().percent }}</span>% ·
        <span data-test="status">{{ job.state().status }}</span> ·
        last id <span data-test="last-id">{{ job.state().lastEventId }}</span>
      </p>
      <p>ids seen: <span data-test="ids">{{ job.state().ids.join(',') }}</span></p>
    }
  `,
})
export class App {
  private readonly query = new URLSearchParams(location.search);
  private readonly progress = inject(JobProgress);
  private readonly fetchProgress = inject(FetchJobProgress);
  protected readonly jobId = this.query.get('job') ?? '42';
  protected readonly job = signal<JobWatch | undefined>(undefined);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.job()?.close());
    void this.start();
  }

  private async start() {
    const mode = this.query.get('mode') ?? 'open';
    const api = this.query.get('api') ?? '';
    const drop = this.query.get('drop');
    const prefix = mode === 'open' ? '' : mode === 'fetch' ? '/bearer' : `/${mode}`;
    const url = `${api}${prefix}/jobs/${this.jobId}/progress${drop ? `?drop=${drop}` : ''}`;

    if (mode === 'fetch') {
      this.job.set(this.fetchProgress.watch(url, getToken));
      return;
    }
    if (mode === 'cookie') await fetch('/login', { method: 'POST' });
    this.job.set(this.progress.watch(url, { withCredentials: this.query.get('creds') === '1' }));
  }
}

async function getToken(): Promise<string> {
  const response = await fetch('/token', { method: 'POST' });
  return ((await response.json()) as { token: string }).token;
}
