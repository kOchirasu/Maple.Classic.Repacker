// Publishes the cross-platform (net10.0) backend as a self-contained single-file executable into build/backend,
// which electron-builder bundles under resources/backend. Usage: node scripts/publish-backend.mjs [rid]
import { spawnSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const electronDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const projectPath = resolve(electronDir, '..', 'Maple.Classic.Repacker.UI.csproj');
const outputDir = resolve(electronDir, 'build', 'backend');

const osPart = { win32: 'win', darwin: 'osx', linux: 'linux' }[process.platform];
const archPart = { x64: 'x64', arm64: 'arm64' }[process.arch];
const rid = process.argv[2] ?? (osPart && archPart ? `${osPart}-${archPart}` : undefined);
if (!rid) {
    console.error(`Unsupported platform ${process.platform}/${process.arch}; pass a runtime identifier, e.g. linux-x64.`);
    process.exit(1);
}

rmSync(outputDir, { recursive: true, force: true });
console.log(`Publishing backend for ${rid} -> ${outputDir}`);
const result = spawnSync(
    'dotnet',
    ['publish', projectPath, '-c', 'Release', '-f', 'net10.0', '-r', rid, '--self-contained', 'true', '-o', outputDir],
    { stdio: 'inherit', shell: process.platform === 'win32' }
);
process.exit(result.status ?? 1);
