import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const directory = new URL('../src/Jellyfin.Plugin.Companion/Web/', import.meta.url);
for (const name of ['Companion', 'Resolver', 'Metadata', 'Artwork']) {
  const html = fs.readFileSync(new URL(name + '.html', directory), 'utf8');
  assert(!html.includes('\uFFFD'), `${name}: corrupt UTF-8`);
  assert(html.includes('0a281de0-d3c8-43ef-bf2d-5fac17fbb8c6'), `${name}: plugin ID`);
  assert(!/(c61d7897-a923|a8f3c2e1-4b5d|6f2d1a54-9c6e)/.test(html), `${name}: legacy plugin ID`);
  for (const [, script] of html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/gi)) new vm.Script(script, { filename: name + '.html' });
  console.log(`PASS: ${name} JavaScript syntax, UTF-8 and plugin identity`);
}
const normalizeId = value => value.replace(/-/g, '').toLowerCase();
assert.equal(normalizeId('ABCDEF12-1234-5678-9ABC-DEF012345678'), normalizeId('abcdef12123456789abcdef012345678'));
const resolver = fs.readFileSync(new URL('Resolver.html', directory), 'utf8');
assert(!resolver.includes('config.Enabled'), 'Resolver page must not toggle the entire plugin');
