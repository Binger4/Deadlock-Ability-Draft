/* Panorama renders only server-projected state. All draft validation lives in ASP.NET. */
(function () {
    'use strict';
    var panel, room = null, lastRoom, team = 'HiddenKing', mode = 'FreePick', scope = 'Allies';
    var selectedCard, movingSlot, muted = {}, alive = false, remaining = 0, clockAt = 0;
    function send(operation, fields) {
        var command = fields || {}; command.operation = operation;
        panel.send('command', encodeURIComponent(JSON.stringify(command)));
    }
    function label(parent, text, cls) {
        var item = $.CreatePanel('Label', parent, ''); item.text = text;
        if (cls) item.AddClass(cls); return item;
    }
    function icon(parent, group, key, cls) {
        var path = typeof AbilityDraftAssets !== 'undefined' && AbilityDraftAssets[group + '/' + key];
        if (!path) return;
        var item = $.CreatePanel('Image', parent, ''); item.SetImage('file://{images}/' + path);
        if (cls) item.AddClass(cls);
    }
    function self() { return room && room.players.filter(function (p) { return p.id === room.selfId; })[0]; }
    function createOrJoin(operation) {
        send(operation, { name: panel.find('Name').text, roomCode: panel.find('RoomCode').text, team: team, mode: mode });
    }
    function cards(id, items) {
        var root = panel.find(id); root.RemoveAndDeleteChildren();
        items.forEach(function (card) {
            var node = $.CreatePanel('Button', root, ''); node.AddClass('Card');
            node.SetHasClass('Picked', card.picked); node.SetHasClass('Available', card.canPick); node.SetHasClass('Hidden', card.hidden);
            icon(node, card.kind === 'Hero' ? 'Heroes' : 'Abilities', card.iconKey);
            label(node, card.name);
            if (card.kind !== 'Hero') icon(node, 'HeroesMini', card.sourceHeroKey, 'Mini');
            node.SetPanelEvent('onactivate', function () { if (card.canPick) send('pick', { key: card.id }); });
            node.SetPanelEvent('oncontextmenu', function () {
                selectedCard = card.id; panel.text('RecommendationName', card.name);
                panel.find('Recommendation').visible = true;
            });
        });
    }
    function renderRoom() {
        var me = self(), inRoom = !!room;
        panel.find('EntryForm').visible = !inRoom; panel.find('LinkForm').visible = !inRoom;
        panel.find('RoomActions').visible = inRoom;
        if (!room) {
            ['Turns','Heroes','Abilities','Ultimates','Players','Chat'].forEach(function (id) { panel.find(id).RemoveAndDeleteChildren(); });
            panel.text('RoomTitle', 'Create or join a room'); return;
        }
        panel.text('RoomTitle', room.code + ' · ' + room.mode + ' · ' + room.status);
        panel.text('AbilityTitle', room.blind ? 'ABILITIES · BLIND DRAFT' : 'ABILITIES');
        panel.text('ReadyLabel', me && me.ready ? 'UNREADY' : 'READY');
        panel.find('Ready').enabled = room.status === 'Lobby' && me && me.team !== 'Spectator';
        panel.find('ChangeTeam').enabled = panel.find('Ready').enabled;
        panel.find('Start').visible = !!(me && me.host && room.status === 'Lobby');
        panel.find('Export').visible = !!(me && me.host && me.team !== 'Spectator' && room.status === 'Completed');
        var runtime = panel.get('runtimeEnabled', '0') === '1';
        panel.find('Finalize').visible = !!(runtime && me && me.host && room.status === 'Completed' && !room.runtimeResultId);
        panel.find('ApplySelf').visible = !!(runtime && me && me.team !== 'Spectator' && room.runtimeResultId);
        panel.find('ApplyAll').visible = !!(runtime && me && me.host && room.runtimeResultId);
        panel.find('Send').enabled = !room.disableChat;
        cards('Heroes', room.heroes);
        cards('Abilities', room.abilities.filter(function (c) { return c.kind !== 'UltimateAbility'; }));
        cards('Ultimates', room.abilities.filter(function (c) { return c.kind === 'UltimateAbility'; }));
        var turns = panel.find('Turns'); turns.RemoveAndDeleteChildren();
        room.turns.forEach(function (t, i) {
            var node = label(turns, (i < room.currentTurnIndex ? '✓ ' : '') + '#' + t.slot + ' ' + t.kind, 'Turn');
            node.SetHasClass('Current', i === room.currentTurnIndex);
        });
        var players = panel.find('Players'); players.RemoveAndDeleteChildren();
        room.players.forEach(function (p) {
            var node = $.CreatePanel('Panel', players, ''); node.AddClass('Player'); node.AddClass(p.team);
            var title = $.CreatePanel('Panel', node, ''); title.AddClass('Row');
            icon(title, 'HeroesMini', p.heroKey, 'PlayerHero');
            var name = label(title, (p.slot ? '#' + p.slot + ' ' : '') + p.name + (p.host ? ' ★' : '') +
                (p.team === 'Spectator' ? ' · spectator' : p.ready ? ' · ready' : '') + (!p.connected ? ' · offline' : ''));
            name.SetPanelEvent('oncontextmenu', function () { muted[p.id] = !muted[p.id]; renderChat(); });
            var slots = $.CreatePanel('Panel', node, ''); slots.AddClass('Loadout');
            p.abilities.forEach(function (key, i) {
                var button = $.CreatePanel('Button', slots, '');
                if (key && key !== '?') icon(button, 'Abilities', key); else label(button, key || '—');
                button.SetPanelEvent('onactivate', function () {
                    if (!p.canReorder || (!room.flexibleSlots && i === 3)) return;
                    if (movingSlot && movingSlot.slot === p.slot) {
                        send('reorder', { slot: p.slot, from: movingSlot.index, to: i }); movingSlot = null;
                    } else { movingSlot = { slot: p.slot, index: i }; panel.text('status', 'Select another ability slot to swap.'); }
                });
            });
        });
        renderChat();
    }
    function renderChat() {
        var root = panel.find('Chat'); root.RemoveAndDeleteChildren();
        if (!room) return;
        room.chat.forEach(function (m) {
            if (muted[m.senderId]) return;
            label(root, '[' + m.scope + '] ' + m.sender + ': ' + m.text, m.team);
        });
    }
    function tick() {
        if (!alive) return;
        var value = room && room.timerPhase !== 'None' ? Math.max(0, Math.ceil(remaining - (Date.now() - clockAt) / 1000)) : null;
        panel.text('Timer', value === null ? '—' : String(value));
        $.Schedule(0.25, tick);
    }
    DW.registerPanel({
        init: function (p) {
            panel = p; alive = true; lastRoom = undefined; room = null;
            p.onClick('Entry', function () { p.find('Screen').visible = true; });
            p.onClick('Close', function () { p.find('Screen').visible = false; });
            p.onClick('Create', function () { createOrJoin('create'); });
            p.onClick('Join', function () { createOrJoin('join'); });
            p.onClick('Spectate', function () { createOrJoin('spectate'); });
            p.onClick('Team', function () { team = team === 'HiddenKing' ? 'Archmother' : 'HiddenKing'; p.text('TeamLabel', team === 'HiddenKing' ? 'The Hidden King' : 'The Archmother'); });
            p.onClick('Mode', function () { var modes = ['FreePick','Classic','RandomHero','Custom']; mode = modes[(modes.indexOf(mode) + 1) % modes.length]; p.text('ModeLabel', mode); });
            p.onClick('Link', function () { send('link', { key: p.find('LinkCode').text }); p.find('LinkCode').text = ''; });
            p.onClick('Reconnect', function () { send('reconnect'); });
            p.onClick('Ready', function () { send('ready', { ready: !self().ready }); });
            p.onClick('ChangeTeam', function () { send('team', { team: self().team === 'HiddenKing' ? 'Archmother' : 'HiddenKing' }); });
            [['Start','start'],['Leave','leave'],['Export','export'],['Finalize','finalize'],['ApplySelf','applySelf'],['ApplyAll','applyAll']].forEach(function (a) { p.onClick(a[0], function () { send(a[1]); }); });
            p.onClick('ChatScope', function () { scope = scope === 'Allies' ? 'All' : 'Allies'; p.text('ChatScopeLabel', scope.toUpperCase()); });
            p.onClick('Send', function () { send('chat', { text: p.find('ChatInput').text, scope: scope }); p.find('ChatInput').text = ''; });
            [['Want','want'],['Recommend','recommend']].forEach(function (a) { p.onClick(a[0], function () { send(a[1], { key: selectedCard }); p.find('Recommendation').visible = false; }); });
            $.Msg('[AbilityDraft] Panorama initialized'); tick();
        },
        render: function (p, state, changed) {
            var raw = p.get('room', 'null');
            if (raw !== lastRoom) {
                try { room = JSON.parse(raw); lastRoom = raw; renderRoom(); }
                catch (error) { $.Msg('[AbilityDraft] Invalid room state: ' + error); }
            }
            if (!changed || changed.indexOf('seconds') >= 0) { remaining = p.num('seconds', 0); clockAt = Date.now(); }
        },
        onDestroy: function () { alive = false; }
    });
}());
