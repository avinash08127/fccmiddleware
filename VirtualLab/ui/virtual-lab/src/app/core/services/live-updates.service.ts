import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { runtimeConfig } from '../config/runtime-config';
import {
  type NozzleSimulationSnapshot,
  type PreAuthSessionRecord,
  type TransactionSimulationSummary,
} from './lab-api.service';

export interface ForecourtLiveEvent {
  eventType: 'forecourt-action';
  action: string;
  occurredAtUtc: string;
  correlationId: string;
  message: string;
  transactionGenerated: boolean;
  faulted: boolean;
  nozzle: NozzleSimulationSnapshot | null;
  transaction: TransactionSimulationSummary | null;
}

export interface PreAuthLiveEvent {
  eventType: 'preauth-action';
  action: string;
  occurredAtUtc: string;
  siteCode: string;
  correlationId: string;
  responseStatusCode: number;
  message: string;
  responseBody: string;
  session: PreAuthSessionRecord | null;
}

export interface GenericLiveEvent {
  eventType: string;
  occurredAtUtc?: string;
  correlationId?: string;
}

export type LabLiveEvent = ForecourtLiveEvent | PreAuthLiveEvent | GenericLiveEvent;

@Injectable({ providedIn: 'root' })
export class LiveUpdatesService {
  readonly connectionState = signal<'idle' | 'connecting' | 'connected' | 'error'>('idle');
  readonly events$: Observable<LabLiveEvent>;

  private connection?: HubConnection;
  private readonly events = new Subject<LabLiveEvent>();

  constructor() {
    this.events$ = this.events.asObservable();
  }

  async start(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) {
      this.connectionState.set('connected');
      return;
    }

    this.connectionState.set('connecting');
    this.connection = new HubConnectionBuilder()
      .withUrl(runtimeConfig.signalRHubUrl)
      .withAutomaticReconnect()
      .build();

    this.connection.on('lab-event', payload => {
      this.events.next(payload as LabLiveEvent);
    });

    this.connection.onclose(() => this.connectionState.set('error'));

    try {
      await this.connection.start();
      this.connectionState.set('connected');
    } catch {
      this.connectionState.set('error');
    }
  }
}
