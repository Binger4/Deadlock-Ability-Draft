// Panorama TextEntry receives Windows text/IME input. CHTML currently delivers
// physical keydowns without character events; never synthesize letters from keys.
var AbilityDraftChat = (function () {
    'use strict';
    var panel, changeFocus, dialog, input, request, lastId = '', lastResult = '', open = false, sending = false, generation = 0, sequence = 1;
    var lastActivity = 0;
    function activity() { if (Date.now() - lastActivity >= 3000) { lastActivity = Date.now(); panel.send('activity', '1'); } }
    function close() {
        open = false; sending = false; generation++;
        input.enabled = true;
        dialog.visible = false;
        changeFocus(false);
    }
    function submit() {
        if (!open || sending || !input.text.trim()) return;
        sending = true;
        input.enabled = false;
        panel.find('NativeChatSend').enabled = false;
        panel.text('NativeChatError', 'Sending…');
        panel.send('command', encodeURIComponent(JSON.stringify({ operation: 'chat', key: request.id, chatSequence: sequence, text: input.text })));
        var current = ++generation;
        $.Schedule(8, function () {
            if (!open || current !== generation) return;
            sending = false; input.enabled = true; panel.find('NativeChatSend').enabled = true;
            panel.text('NativeChatError', 'No response yet. You can try again.');
        });
    }
    return {
        isOpen: function () { return open; },
        init: function (p, focus) {
            panel = p; changeFocus = focus; dialog = p.find('NativeChatDock'); input = p.find('NativeChatInput');
            open = false; sending = false; request = null; lastId = ''; lastResult = ''; generation++;
            dialog.visible = false;
            p.onClick('NativeChatSend', submit);
            p.onClick('NativeChatCancel', function () { activity(); close(); });
            input.SetPanelEvent('ontextentrychange', activity);
            input.SetPanelEvent('ontextentrysubmit', submit);
            dialog.SetPanelEvent('oncancel', close);
        },
        render: function (visible) {
            if (!visible) { if (open) close(); return; }
            var result = panel.get('nativeChatResult', '');
            if (result && result !== lastResult) {
                lastResult = result;
                try {
                    var reply = JSON.parse(result);
                    if (open && request && reply.id === request.id && reply.sequence === sequence) {
                        generation++; sending = false; input.enabled = true; panel.find('NativeChatSend').enabled = true;
                        if (!reply.error) { input.text = ''; sequence++; panel.text('NativeChatError', ''); input.SetFocus(); }
                        else panel.text('NativeChatError', reply.error);
                    }
                } catch (_) { }
            }
            try {
                var next = JSON.parse(panel.get('nativeChat', 'null'));
                if (!next || !/^[a-f0-9]{32}$/.test(next.id) ||
                    ['All', 'Allies', 'Spectators'].indexOf(next.scope) < 0) return;
                if (next.id === lastId) {
                    // The website's audience toggle updates the current entry.
                    // Keep composed text/caret intact and return keyboard focus
                    // after clicking the HTML button. Never reopen a closed dock.
                    if (open && request && (next.scope !== request.scope || next.revision !== request.revision)) {
                        request = next;
                        panel.text('NativeChatHeading', next.scope.toUpperCase());
                        if (!sending) input.SetFocus();
                    }
                    return;
                }
                if (request && request.roomCode !== next.roomCode) input.text = '';
                lastId = next.id; request = next; open = true; sending = false; input.enabled = true; sequence = 1; generation++;
                panel.text('NativeChatHeading', next.scope.toUpperCase());
                panel.text('NativeChatError', ''); panel.find('NativeChatSend').enabled = true;
                // This dock is part of SiteScreen's existing input context.
                // Only the TextEntry gets keyboard focus; the draft stays clickable.
                dialog.visible = true; changeFocus(true); input.SetFocus();
            } catch (_) { }
        },
        destroy: function () { if (dialog) close(); request = null; }
    };
}());
