const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const callbacks = [], commands = [], entries = {};
let queued = false;
const context = { IsValid: () => true, GetParent: () => null, BHasClass: () => queued, FindChildTraverse: id => entries[id] };
vm.runInNewContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_entry.js', 'utf8'), {
    AbilityDraftServer: '127.0.0.1:27067', AbilityDraftPublicServer: '127.0.0.1:27068',
    $: { GetContextPanel: () => context, Schedule: (_, fn) => callbacks.push(fn), Msg: () => {}, DispatchEvent: (event, command) => commands.push([event, command]) }
});
callbacks.shift()(); // Native Play page has not created its buttons yet.
assert.equal(commands.length, 0);
for (const id of ['AbilityDraftPublic', 'AbilityDraftCustom']) entries[id] = { SetPanelEvent: (_, fn) => entries[id].activate = fn };
callbacks.shift()();
entries.AbilityDraftPublic.activate(); entries.AbilityDraftCustom.activate();
assert.deepEqual(commands.map(c => c[1]), ['alias ad_menu_entry "dw_ad_entry public"', 'connect 127.0.0.1:27068', 'alias ad_menu_entry "dw_ad_entry custom"', 'connect 127.0.0.1:27067']);
queued = true; callbacks.shift()();
assert.equal(entries.AbilityDraftPublic.enabled, false);
assert.equal(entries.AbilityDraftCustom.enabled, false);
entries.AbilityDraftPublic.activate(); assert.equal(commands.length, 4);
console.log('PASS Play entries bind after native layout creation, connect to the correct hub, and respect native matchmaking.');
