import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { Booking, BookingStatus, LogEntry, Sailing } from './core/models';

const BADGES: Record<BookingStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Issued: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Reversed: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly sailings = signal<Sailing[]>([]);
  readonly selectedCode = signal('');
  readonly bookings = signal<Booking[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly vessel = signal('MV Atlantic Star');
  readonly origin = signal('Santos');
  readonly destination = signal('Rotterdam');
  readonly capacity = signal('10');

  readonly reference = signal('');
  readonly teu = signal('2');

  readonly selected = computed(() => this.sailings().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: BookingStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const sailings = await this.api.sailings();
      this.sailings.set(sailings);
      if (!this.selectedCode() && sailings.length > 0) this.selectedCode.set(sailings[0].code);
      if (this.selectedCode()) this.bookings.set(await this.api.bookings(this.selectedCode()));
      const id = this.logFor();
      if (id) this.log.set(await this.api.events(id));
    } catch {
      this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openSailing(): Promise<void> {
    await this.run(async () => {
      const sailing = await this.api.openSailing({
        code: this.code(),
        vessel: this.vessel(),
        origin: this.origin(),
        destination: this.destination(),
        capacityTeu: Number(this.capacity()),
      });
      this.code.set('');
      this.selectedCode.set(sailing.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      await this.api.book({
        sailingCode: this.selectedCode(),
        reference: this.reference(),
        teu: Number(this.teu()),
      });
      this.reference.set('');
    });
  }

  async reverse(booking: Booking): Promise<void> {
    await this.run(() => this.api.reverse(booking.id));
  }

  async showLog(booking: Booking): Promise<void> {
    this.logFor.set(booking.id);
    this.log.set([]);
    await this.refresh();
  }

  canReverse(booking: Booking): boolean {
    return booking.status === 'Held' || booking.status === 'Issued';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
