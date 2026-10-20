import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
const publishOutput = resolve('.build/web');
if (!publishOutput.startsWith(resolve(process.cwd(), '.build') + '/')
    && !publishOutput.startsWith(resolve(process.cwd(), '.build') + '\\')) throw new Error('Invalid publish path');
rmSync(publishOutput, { recursive: true, force: true });
const result = spawnSync('dotnet', ['publish', 'src/PclPatcher.Web', '-c', 'Release', '-o', '.build/web'], { stdio: 'inherit', shell: false });
if (result.status !== 0) process.exit(result.status ?? 1);
const dist = resolve('dist');
// Only remove this project's fixed, generated output directory.
if (dist !== resolve(process.cwd(), 'dist')) throw new Error('Invalid output path');
rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });
cpSync('.build/web/wwwroot', dist, { recursive: true });
console.log('Built static website in dist/');
