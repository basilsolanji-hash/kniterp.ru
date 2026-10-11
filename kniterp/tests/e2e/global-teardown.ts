import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

/** Остановка приложения. База e2e остаётся на тестовом сервере для разбора падений; имя — в .auth/run.json. */
export default async function globalTeardown() {
  const pidFile = join(__dirname, '.server.pid');
  if (existsSync(pidFile)) {
    const pid = Number(readFileSync(pidFile, 'utf8'));
    try {
      process.kill(-pid, 'SIGTERM');
    } catch {
      // Процесс уже завершился.
    }
    rmSync(pidFile);
  }
}
