/* Suppress the native connection countdown only while connected to a draft-only hub.
   Restore the original style on transfer/unload; normal matches keep their native HUD. */
var AbilityDraftWaitingStatus = (function () {
    var hidden = null, previous = '', previousOpacity = '', warned = false;
    function restore() {
        if (hidden && hidden.IsValid()) { hidden.style.visibility = previous; hidden.style.opacity = previousOpacity; }
        hidden = null;
    }
    function update(panel) {
        if (panel.get('draftHub', '0') !== '1' && panel.get('serverRole', '') !== 'match') { restore(); return; }
        var root = $.GetContextPanel();
        while (root.GetParent()) root = root.GetParent();
        var status = root.FindChildTraverse('WaitingForPlayersStatus');
        if (!status) {
            if (!warned) { $.Msg('[AbilityDraftSite] waiting HUD not created yet; will retry'); warned = true; }
            return;
        }
        if (status !== hidden) {
            restore(); hidden = status; previous = status.style.visibility; previousOpacity = status.style.opacity;
            $.Msg('[AbilityDraftSite] suppressing native connecting HUD');
        }
        // The native status owner can rewrite visibility when its timer updates.
        // Opacity keeps those rewrites from flashing the infinite countdown between polls.
        status.style.opacity = '0';
        status.style.visibility = 'collapse';
    }
    return { update: update, restore: restore };
}());
