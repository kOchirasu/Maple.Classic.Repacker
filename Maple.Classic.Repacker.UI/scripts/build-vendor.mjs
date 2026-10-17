import { build } from 'esbuild';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(__dirname, '..');
const outVendorRoot = resolve(projectRoot, 'obj', 'ts', 'wwwroot', 'vendor');

const cmOutDir = resolve(outVendorRoot, 'codemirror');
const msgpackOutDir = resolve(outVendorRoot, 'msgpack');

mkdirSync(cmOutDir, { recursive: true });
mkdirSync(msgpackOutDir, { recursive: true });

await Promise.all([
    build({
        entryPoints: [resolve(__dirname, 'cm6-entry.mjs')],
        bundle: true,
        minify: true,
        format: 'iife',
        target: ['es2022'],
        outfile: resolve(cmOutDir, 'cm6.bundle.js'),
    }),
    build({
        entryPoints: ['@msgpack/msgpack'],
        absWorkingDir: projectRoot,
        bundle: true,
        minify: true,
        format: 'iife',
        globalName: 'MessagePack',
        target: ['es2022'],
        outfile: resolve(msgpackOutDir, 'msgpack.min.js'),
    }),
]);
