const assert = require('node:assert/strict'), fs = require('node:fs'), vm = require('node:vm');
const handlers = {}, calls = [];
let now = 10000;
const location = { pathname: '/room/ABC123/draft', search: '?playerId=private&inGame=true' };
const ctx = { location, URLSearchParams, Date: { now: () => now },
    document: { addEventListener: (name, handler) => handlers[name] = handler },
    fetch: (url, options) => { calls.push({url, body:JSON.parse(options.body)}); return Promise.resolve(); } };
vm.runInNewContext(fs.readFileSync('wwwroot/ingame-activity.js','utf8'), ctx);
assert.equal(calls.length, 0); // Loading and rendering cannot keep an idle player alive.
handlers.pointermove({isTrusted:false}); assert.equal(calls.length,0);
handlers.pointerdown({isTrusted:true}); assert.equal(calls.length,1);
assert.equal(calls[0].body.playerId,'private'); assert.equal(calls[0].body.roomCode,'ABC123');
now += 1000; handlers.keydown({isTrusted:true}); assert.equal(calls.length,1);
now += 2000; handlers.input({isTrusted:true}); assert.equal(calls.length,2);
now += 3000; handlers.wheel({isTrusted:true}); assert.equal(calls.length,3);
now += 3000; location.search='?playerId=private'; handlers.pointermove({isTrusted:true}); assert.equal(calls.length,3);
location.pathname='/create'; location.search='?gameEntry=private-entry'; handlers.pointerdown({isTrusted:true});
assert.equal(calls.at(-1).body.gameEntry,'private-entry');
now += 3000; location.pathname='/admin'; handlers.keydown({isTrusted:true}); assert.equal(calls.length,4);
console.log('PASS: custom website activity follows real interaction, is throttled, and ignores idle renders, synthetic events and unrelated pages');
