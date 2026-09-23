const fs = require('fs'), vm = require('vm'), assert = require('assert');
let status = { style: { visibility: 'visible', opacity: '1' }, IsValid: () => true }, hub = '1', role = 'custom';
const root = { GetParent: () => null, FindChildTraverse: id => id === 'WaitingForPlayersStatus' ? status : null };
const ctx = { $: { GetContextPanel: () => ({ GetParent: () => root }), Msg: () => {} } };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_waiting_status.js', 'utf8'), ctx);
const api = ctx.AbilityDraftWaitingStatus, panel = { get: key => key === 'draftHub' ? hub : role };
api.update(panel); assert.equal(status.style.visibility, 'collapse');
status.style.visibility = 'visible'; api.update(panel);
assert.equal(status.style.visibility, 'collapse'); assert.equal(status.style.opacity, '0');
api.update(panel); hub = '0'; api.update(panel); assert.equal(status.style.visibility, 'visible');
role = 'match'; api.update(panel); assert.equal(status.style.visibility, 'collapse');
api.restore(); assert.equal(status.style.visibility, 'visible'); assert.equal(status.style.opacity, '1');
hub = '1'; api.update(panel); api.restore(); assert.equal(status.style.visibility, 'visible');
status = null; api.update(panel);
console.log('PASS: connecting HUD hidden for hubs/workers, native visibility rewrites suppressed, teardown restores, missing panel safe');
const buttons = { AbilityDraftPublic: {}, AbilityDraftCustom: {} }, dispatches = [];
let queued = false, scheduled;
const menu = { IsValid: () => true, BHasClass: c => c === 'in_matchmaking' && queued, GetParent: () => null,
 FindChildTraverse: id => buttons[id] };
Object.values(buttons).forEach(b => b.SetPanelEvent = (name, cb) => { b[name] = cb; });
const entryCtx = { $: { GetContextPanel: () => menu, Schedule: (time, cb) => scheduled = cb, Msg: () => {},
 DispatchEvent: (...args) => dispatches.push(args) }, AbilityDraftServer: '127.0.0.1:27067', AbilityDraftPublicServer: '127.0.0.1:27068' };
vm.createContext(entryCtx); vm.runInContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_entry.js', 'utf8'), entryCtx);
assert.equal(buttons.AbilityDraftPublic.enabled, true);
queued = true; scheduled();
assert.equal(buttons.AbilityDraftPublic.enabled, false); assert.equal(buttons.AbilityDraftCustom.enabled, false);
buttons.AbilityDraftPublic.onactivate(); buttons.AbilityDraftCustom.onactivate(); assert.equal(dispatches.length, 0);
queued = false; scheduled(); buttons.AbilityDraftPublic.onactivate(); assert.equal(dispatches.length, 2);
console.log('PASS: native queue disables both entries, guards activation and restores entry after cancellation');

// The native ability tooltip is shared with the normal HUD. Upgrade-only filtering
// must survive rapid hovers/roster rebuilds without changing native inline styles.
const nodes = [], later = [];
function node(id, parent) {
 const n = { id, parent, children: [], classes: new Set(), style: {}, visible: true, valid: true,
  IsValid() { return this.valid; }, GetParent() { return this.parent; },
  GetChildCount() { return this.children.length; }, Children() { return this.children; },
  FindChildTraverse(id) { return this.children.find(n => n.id === id) || this.children.map(n => n.FindChildTraverse(id)).find(Boolean); },
  SetParent(p) { this.parent.children = this.parent.children.filter(n => n !== this); this.parent = p; p.children.push(this); },
  AddClass(c) { this.classes.add(c); }, RemoveClass(c) { this.classes.delete(c); },
  SetHasClass(c, b) { b ? this.AddClass(c) : this.RemoveClass(c); },
  SetPanelEvent(event, fn) { this[event] = fn; }, SetImage() {},
  RemoveAndDeleteChildren() { this.children.forEach(n => n.valid = false); this.children = []; }
 };
 if (parent) parent.children.push(n); nodes.push(n); return n;
}
const gameRoot = node('game'), nativeOverlay = node('native', gameRoot), addon = node('addon', gameRoot);
const tooltipHost = node('AbilityDraftTooltipHost', addon), manager = node('TooltipManager', nativeOverlay);
const tooltip = node('tooltip', manager), contents = node('TooltipContents', tooltip), listParent = node('list', tooltip);
const upgrades = node('AbilityUpgradeList', listParent), tiers = [1,2,3].map(i => node('tier'+i, upgrades));
node('MatchHiddenKing', addon); node('MatchArchmother', addon);
const roster = [{ team:'HiddenKing', hero:'hero', heroName:'Hero', name:'Player', connected:true, level:1, playerSlot:2,
 abilities:[{key:'test',name:'Skill',slot:1,token:123,state:'Locked',upgradeTier:0}]}];
const tooltipEvents = [];
const matchCtx = { AbilityDraftAssets:{'HeroesMini/hero':'hero','Abilities/test':'skill'}, $:{
 GetContextPanel:()=>addon, CreatePanel:(_type,p,id)=>node(id,p),
 DispatchEvent:(...args)=>tooltipEvents.push(args), Schedule:(_time,fn)=>later.push(fn), Msg(){}
}};
vm.createContext(matchCtx); vm.runInContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_match.js','utf8'),matchCtx);
const match = matchCtx.AbilityDraftMatch;
match.render({ get:()=>JSON.stringify({phase:'Playing',players:roster}), text(){}, find:id=>addon.FindChildTraverse(id) });
const skill = nodes.find(n=>n.classes.has('RosterAbilityIcon')), bars = nodes.filter(n=>n.classes.has('UpgradeBar'));
bars[1].onmouseover(); later.splice(0).forEach(fn=>fn());
assert.equal(manager.parent, tooltipHost); assert.equal(contents.visible, false);
assert.equal(tiers[0].visible, false); assert.equal(tiers[1].visible, true);
skill.onmouseover();
assert.equal(contents.visible, true); assert(tiers.every(t=>t.visible));
assert.deepEqual(tooltipEvents.at(-1).slice(0,1), ['CitadelShowAbilityDetailsTooltip']);
bars[0].onmouseover(); match.hideTooltip(); later.splice(0).forEach(fn=>fn());
assert.equal(manager.parent,nativeOverlay); assert.equal(contents.visible, true);
assert(nodes.every(n=>Object.keys(n.style).length === 0));
bars[2].onmouseover(); later.splice(0).forEach(fn=>fn()); match.reset();
assert.equal(manager.parent,nativeOverlay); assert(tiers.every(t=>t.visible));
console.log('PASS: skill hover restores full description; upgrade filtering and delayed callbacks cannot leak into native HUD after close/reset');

// Exercise the actual game shell: support destinations must leave the HTML panel.
let siteShell;
const linkEvents = [], embeddedUrls = [], siteState = {}, shellNodes = {};
const shellPanel = { get:(key,fallback)=>siteState[key] ?? fallback, text(){}, onClick(){},
 find:id=>shellNodes[id] ??= { SetHasClass(){}, SetURL(url){ embeddedUrls.push(url); }, SetFocus(){} } };
const shellCtx = { AbilityDraftWebsiteOrigin:'https://draft.example.org/',
 AbilityDraftWaitingStatus:{update(){},restore(){}}, AbilityDraftMatch:{reset(){},hideTooltip(){}},
 DW:{registerPanel:config=>siteShell=config}, $:{Msg(){},Schedule(){},DispatchEvent:(...args)=>linkEvents.push(args)} };
vm.createContext(shellCtx);
vm.runInContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_site.js','utf8'),shellCtx);
siteShell.init(shellPanel);
siteState.externalLinkId = 'support-click'; siteState.externalUrl = 'https://draft.example.org/project-links/3';
siteShell.render(); siteShell.render();
assert.deepEqual(linkEvents, [['ExternalBrowserGoToURL',siteState.externalUrl]]);
assert.equal(embeddedUrls.length,0);
siteState.externalLinkId = 'unsafe-click'; siteState.externalUrl = 'https://unrelated.example.org/project-links/3';
siteShell.render(); assert.equal(linkEvents.length,1);
siteState.externalUrl = 'https://draft.example.org/project-links/3?url=https://unrelated.example.org';
siteShell.render(); assert.equal(linkEvents.length,1);
siteState.externalUrl = 'https://draft.example.org/project-links/10';
siteShell.render(); assert.equal(linkEvents.length,2);
console.log('PASS: support links open the external browser once, never navigate embedded HTML, and reject foreign or injected routes');
