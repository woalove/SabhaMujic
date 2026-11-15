// Drives tests/SabhaMujic.RemoteE2EHost over its stdin/stdout JSON-line protocol.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const hostDll = join(here, '..', '..', '..', 'tests', 'SabhaMujic.RemoteE2EHost', 'bin', 'Debug', 'net10.0', 'SabhaMujic.RemoteE2EHost.dll');

export class E2EHost {
  constructor() {
    /** @type {any[]} */
    this.events = [];
    /** @type {{ event: string, resolve: (value: any) => void }[]} */
    this.waiters = [];
    this.stderr = '';
  }

  async start() {
    if (!existsSync(hostDll)) throw new Error(`E2E host not built: ${hostDll} (run "npm run build:e2e-host")`);
    this.process = spawn('dotnet', [hostDll], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.process.stderr.on('data', (chunk) => {
      this.stderr += chunk.toString();
      if (process.env.E2E_VERBOSE) process.stderr.write(chunk);
    });
    createInterface({ input: this.process.stdout }).on('line', (line) => {
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        return;
      }
      const index = this.waiters.findIndex((w) => w.event === message.event);
      if (index >= 0) this.waiters.splice(index, 1)[0].resolve(message);
      else this.events.push(message);
    });
    this.ready = await this.next('ready', 60000);
    return this.ready;
  }

  /**
   * @param {string} event
   * @param {number} [timeoutMs]
   */
  next(event, timeoutMs = 30000) {
    const queued = this.events.findIndex((m) => m.event === event);
    if (queued >= 0) return Promise.resolve(this.events.splice(queued, 1)[0]);
    return new Promise((resolve, reject) => {
      const waiter = { event, resolve };
      this.waiters.push(waiter);
      setTimeout(() => {
        const i = this.waiters.indexOf(waiter);
        if (i >= 0) {
          this.waiters.splice(i, 1);
          reject(new Error(`E2E host did not report "${event}" within ${timeoutMs} ms.\n${this.stderr}`));
        }
      }, timeoutMs).unref();
    });
  }

  /** @param {string} command */
  send(command) {
    this.process?.stdin.write(command + '\n');
  }

  /** @param {'Admin' | 'Volunteer'} role */
  async pairingCode(role) {
    this.send(`code ${role}`);
    return this.next('code');
  }

  async stop() {
    if (!this.process || this.process.exitCode !== null) return;
    this.send('quit');
    const exited = new Promise((resolve) => this.process?.once('exit', resolve));
    const timeout = new Promise((resolve) => setTimeout(resolve, 10000).unref());
    await Promise.race([exited, timeout]);
    if (this.process.exitCode === null) this.process.kill('SIGKILL');
  }
}
