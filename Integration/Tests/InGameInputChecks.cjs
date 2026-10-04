const assert = require('node:assert/strict'), fs = require('node:fs'), vm = require('node:vm');
const nodes = {}, values = {}, clicks = {}, sent = [], timers = [], focus = [];
const panel = { get:(key, fallback)=>values[key] ?? fallback,
    text(id, text) { this.find(id).text = text; },
    find:id=>nodes[id] ??= { text:'', SetPanelEvent(e, fn){this[e]=fn;}, SetTopOfInputContext(){throw Error('Chat must not capture the draft input context');}, SetFocus(){this.focused=true;} },
    onClick:(id,fn)=>clicks[id]=fn, send:(event,payload)=>{if(event === 'command') sent.push(JSON.parse(decodeURIComponent(payload)));} };
const context = { $:{Schedule:(_delay,fn)=>timers.push(fn)} };
vm.runInNewContext(fs.readFileSync('Integration/Addon/panorama/scripts/ability_draft_chat.js','utf8'),context);
const chat = context.AbilityDraftChat;
chat.init(panel, open=>focus.push(open));
values.nativeChat = JSON.stringify({id:'a'.repeat(32),roomCode:'ONE',scope:'Allies'});
chat.render(true);
assert(chat.isOpen()); assert(nodes.NativeChatInput.focused); assert(nodes.NativeChatDock.visible);
// TextEntry supplies composed text. No key-to-letter translation is involved.
nodes.NativeChatInput.text = 'Привет — français 中文 한국어 العربية 😀';
const composedText = nodes.NativeChatInput.text;
nodes.NativeChatInput.focused = false;
values.nativeChat = JSON.stringify({id:'a'.repeat(32),roomCode:'ONE',scope:'All',revision:1}); chat.render(true);
assert.equal(nodes.NativeChatHeading.text,'ALL'); assert(nodes.NativeChatInput.focused);
assert.equal(nodes.NativeChatInput.text,composedText);
nodes.NativeChatInput.focused = false;
chat.render(true); assert(!nodes.NativeChatInput.focused); // Polls must not steal focus from draft cards.
// Two fast toggles can return to the same audience before the next poll.
values.nativeChat = JSON.stringify({id:'a'.repeat(32),roomCode:'ONE',scope:'All',revision:3}); chat.render(true);
assert(nodes.NativeChatInput.focused); assert.equal(nodes.NativeChatInput.text,composedText);
nodes.NativeChatInput.ontextentrysubmit(); clicks.NativeChatSend();
assert.equal(sent.length,1); assert.equal(sent[0].text,nodes.NativeChatInput.text);
chat.render(true); assert.equal(nodes.NativeChatInput.text,sent[0].text);
values.nativeChatResult = JSON.stringify({id:'a'.repeat(32),sequence:1,error:'Retry'}); chat.render(true);
assert(chat.isOpen()); assert.equal(nodes.NativeChatError.text,'Retry'); assert.equal(nodes.NativeChatInput.text,sent[0].text);
clicks.NativeChatSend();
values.nativeChatResult = JSON.stringify({id:'a'.repeat(32),sequence:1,error:null}); chat.render(true);
assert(chat.isOpen()); assert.equal(nodes.NativeChatInput.text,''); assert(nodes.NativeChatDock.visible);
assert(nodes.NativeChatInput.enabled); assert(nodes.NativeChatInput.focused);
nodes.NativeChatInput.text='second message'; nodes.NativeChatInput.ontextentrysubmit();
assert.equal(sent.at(-1).chatSequence,2); assert.equal(sent.at(-1).key,'a'.repeat(32));
chat.render(true); assert.equal(nodes.NativeChatInput.text,'second message'); // Old ack cannot clear the next send.
values.nativeChatResult = JSON.stringify({id:'a'.repeat(32),sequence:2,error:null}); chat.render(true);
assert(chat.isOpen()); assert.equal(nodes.NativeChatInput.text,'');
clicks.NativeChatCancel(); assert(!chat.isOpen()); assert(!nodes.NativeChatDock.visible);
values.nativeChat = JSON.stringify({id:'a'.repeat(32),roomCode:'ONE',scope:'Allies',revision:4}); chat.render(true);
assert(!chat.isOpen()); assert(!nodes.NativeChatDock.visible);
timers.forEach(fn=>fn()); chat.render(true); assert(!chat.isOpen());
values.nativeChat = JSON.stringify({id:'b'.repeat(32),roomCode:'ONE',scope:'All'}); chat.render(true);
nodes.NativeChatInput.text='unsent'; clicks.NativeChatCancel(); assert(!chat.isOpen());
values.nativeChat = JSON.stringify({id:'b'.repeat(32),roomCode:'ONE',scope:'Allies',revision:1}); chat.render(true);
assert(!chat.isOpen()); assert.equal(nodes.NativeChatInput.text,'unsent');
values.nativeChat = JSON.stringify({id:'c'.repeat(32),roomCode:'ONE',scope:'All'}); chat.render(true);
assert.equal(nodes.NativeChatInput.text,'unsent'); chat.render(false); assert(!chat.isOpen());
values.nativeChat = JSON.stringify({id:'d'.repeat(32),roomCode:'TWO',scope:'All'}); chat.render(true);
assert.equal(nodes.NativeChatInput.text,''); chat.destroy(); assert(!chat.isOpen());
assert.equal(focus.at(-1),false);
console.log('PASS: native chat preserves Unicode and unsent text, switches audience while typing, waits for acknowledgement, and ignores stale responses');
