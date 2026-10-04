const fs = require('fs'), vm = require('vm'), assert = require('assert/strict');

function panel(type, parent, id) {
    const p = { paneltype: type, parent, id, children: [], style: {}, valid: true,
        Children() { return this.children; }, GetParent() { return this.parent; },
        IsValid() { return this.valid; },
        FindChildTraverse(id) { return this.children.find(p => p.id === id) || this.children.map(p => p.FindChildTraverse(id)).find(Boolean); },
        SetPanelEvent(event, fn) { this[event] = fn; },
        BLoadLayout(path) { this.layout = path; },
        DeleteAsync() { this.valid = false; this.parent.children = this.parent.children.filter(p => p !== this); }
    };
    if (parent) parent.children.push(p);
    return p;
}
const root = panel('Panel', null, 'root'), hud = panel('Panel', root, 'hud_signature');
const pips = [], icons = [], native = [];
for (let i = 0; i < 4; i++) {
    pips.push(panel('CitadelHudAbilityUpgradePips', hud, 'pips' + i));
    native.push(panel('Panel', pips[i], 'native'));
    native[i].style.visibility = 'visible';
    icons.push(panel('CitadelAbilityIcon', hud, 'icon' + i));
    panel('Panel', icons[i], 'button_container');
}
const commands = [], ctx = { $: { GetContextPanel: () => root, CreatePanel: panel, Msg() {},
    DispatchEvent: (...args) => commands.push(args) } };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_upgrades.js', 'utf8'), ctx);
let state = { enabled: true, abilities: [
    { slot: 1, bits: 1, canTrain: true }, { slot: 2, bits: 0, canTrain: true },
    { slot: 3, bits: 3, canTrain: false }, { slot: 4, bits: 15, canTrain: false }
] };
const source = { get: () => JSON.stringify(state) };
const api = ctx.AbilityDraftUpgrades;
api.update(source);
const click = i => icons[i].FindChildTraverse('AbilityDraftTrainClick');
assert(icons.every((_, i) => click(i).paneltype === 'Button'));
click(0).onactivate(); click(1).onactivate(); click(2).onactivate(); click(3).onactivate();
assert.deepEqual(commands, [['CitadelConCommand', 'trainorupgradeability 1'], ['CitadelConCommand', 'trainorupgradeability 2']]);
// Later tiers and already-learned tiers must not spend a point by accident.
pips[0].FindChildTraverse('AbilityDraftTier2').onactivate();
pips[2].FindChildTraverse('AbilityDraftTier1').onactivate();
assert.equal(commands.length, 2);
state.abilities[0] = { slot: 1, bits: 3, canTrain: false };
api.update(source); click(0).onactivate();
assert.equal(commands.length, 2);
assert.equal(pips[0].FindChildTraverse('AbilityDraftTier1').children[0].text, '✓');
state.abilities[0].canTrain = true; api.update(source);
pips[0].FindChildTraverse('AbilityDraftTier2').onactivate();
assert.deepEqual(commands.at(-1), ['CitadelConCommand', 'trainorupgradeability 1']);
assert.equal(state.abilities[0].bits, 3); // Wait for the authoritative update.
state.enabled = false; api.update(source);
assert(native.every(p => p.style.visibility === 'visible'));
assert(icons.every((_, i) => !click(i)));
state.enabled = true; api.update(source);
assert(icons.every((_, i) => click(i)));
api.restore(); assert(native.every(p => p.style.visibility === 'visible'));

// TAB uses the scoreboard class, not just detail view. Keep the mouse target
// hidden during normal casting and visible in either native expanded HUD mode.
const css = fs.readFileSync('Integration/Addon/panorama/styles/ability_draft_upgrade_click.css', 'utf8');
const xml = fs.readFileSync('Integration/Addon/panorama/layout/ability_draft_upgrade_click.xml', 'utf8');
assert.match(css, /visibility:\s*collapse/);
assert.match(css, /\.gScoreboardOpen\.AbilityDraftTrainClick/);
assert.match(css, /\.gScoreboardOpen\s+\.AbilityDraftTrainClick/);
assert.match(xml, /<Button[^>]*hittest="true"[^>]*hittestchildren="false"/);
assert.match(xml, /classes="gDetailView gScoreboardOpen"/);
console.log('PASS: HUD clicks route the right slot, obey affordability and tiers, await server state, restore on teardown, and include TAB scoreboard input');
