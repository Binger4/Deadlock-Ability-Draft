(function () {
    'use strict';
    var context = $.GetContextPanel();
    function inQueue() {
        for (var p = context; p; p = p.GetParent())
            if (p.BHasClass('in_matchmaking')) return true;
        return false;
    }
    function connect(kind) {
        if (inQueue()) return;
        var address = kind === 'public' ? (typeof AbilityDraftPublicServer === 'string' ? AbilityDraftPublicServer : '') :
            (typeof AbilityDraftServer === 'string' ? AbilityDraftServer : '');
        if (!/^[a-zA-Z0-9.-]+:[0-9]{1,5}$/.test(address)) { $.Msg('[AbilityDraft] No valid drafting server configured'); return; }
        // Console aliases belong to the client session, unlike the menu panel which is replaced on connect.
        // Only fixed command names and the two fixed entry kinds enter this command string.
        $.DispatchEvent('CitadelConCommand', 'alias ad_menu_entry "dw_ad_entry ' + kind + '"');
        $.Msg('[AbilityDraft] connecting to drafting server for ' + kind);
        $.DispatchEvent('CitadelConCommand', 'connect ' + address);
    }
    context.FindChildTraverse('AbilityDraftPublic').SetPanelEvent('onactivate', function () { connect('public'); });
    context.FindChildTraverse('AbilityDraftCustom').SetPanelEvent('onactivate', function () { connect('custom'); });
    function updateAvailability() {
        if (!context.IsValid()) return;
        var queued = inQueue();
        ['AbilityDraftPublic', 'AbilityDraftCustom'].forEach(function (id) {
            context.FindChildTraverse(id).enabled = !queued;
        });
        $.Schedule(0.1, updateAvailability);
    }
    updateAvailability();
}());
