import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { environment } from '../../../environments/environment';

@Injectable({ providedIn: 'root' })
export class LiveUpdatesService {
  readonly connectionState = signal<'idle' | 'connecting' | 'connected' | 'error'>('idle');

  private connection?: HubConnection;

  async start(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) {
      this.connectionState.set('connected');
      return;
    }

    this.connectionState.set('connecting');
    this.connection = new HubConnectionBuilder()
      .withUrl(environment.signalRHubUrl)
      .withAutomaticReconnect()
      .build();

    this.connection.onclose(() => this.connectionState.set('error'));

    try {
      await this.connection.start();
      this.connectionState.set('connected');
    } catch {
      this.connectionState.set('error');
    }
  }
}
