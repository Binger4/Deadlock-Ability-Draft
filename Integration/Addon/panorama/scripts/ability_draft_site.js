(function () {
    'use strict';
    var panel, browser, alive = false, loadedUrl = '', websiteVisible = false, closed = false, started = false, transferred = '';
    var lastExternalLink = '';
    var lastItemPurchase = '';
    var codeRevealed = false, lastCode = '', exiting = false, confirmAbandon = false, preparing = false, intro = false;
    function send(operation) { panel.send('command', encodeURIComponent(JSON.stringify({ operation: operation }))); }
    function focusWebsite() {
        if (alive && websiteVisible && !exiting && !browser.BHasKeyFocus()) {
            browser.SetFocus();
        }
    }
    function websiteInput(enabled) {
        if (!browser) return;
        panel.find('SiteScreen').SetTopOfInputContext(enabled);
        browser.SetAcceptsFocus(enabled);
        if (typeof browser.SetAcceptsInput === 'function') browser.SetAcceptsInput(enabled);
        if (typeof browser.SetIgnoreCursor === 'function') browser.SetIgnoreCursor(!enabled);
        if (enabled) focusWebsite();
    }
    function openWebsite() {
        var url = panel.get('websiteUrl', '');
        if (!url || url === loadedUrl) return;
        if (url.indexOf(AbilityDraftWebsiteOrigin) !== 0 || !browser || typeof browser.SetURL !== 'function') {
            panel.text('siteStatus', 'Unable to open the draft. Update the Ability Draft mod.');
            panel.find('siteStatus').visible = true;
            return;
        }
        try {
            $.Msg('[AbilityDraftSite] navigating to ' + url.split('?')[0]);
            browser.SetURL(url); loadedUrl = url; focusWebsite();
        }
        catch (error) { $.Msg('[AbilityDraftSite] navigation failed: ' + error); }
    }
    function render() {
        AbilityDraftWaitingStatus.update(panel);
        var purchase = panel.get('itemPurchase', '');
        if (purchase && purchase !== lastItemPurchase) {
            lastItemPurchase = purchase;
            try {
                var request = JSON.parse(purchase);
                if (typeof request.id === 'string' && request.id.length === 32 && /^[a-f0-9]{32}$/.test(request.id) &&
                    typeof request.item === 'string' && /^upgrade_[a-z0-9_]{1,100}$/.test(request.item) && request.item.indexOf('\n') < 0)
                    $.DispatchEvent('CitadelConCommand', 'buyitem ' + request.item);
            } catch (_) { $.Msg('[AbilityDraftSite] Invalid shop request'); }
        }
        var externalId = panel.get('externalLinkId', ''), externalUrl = panel.get('externalUrl', '');
        if (externalId && externalId !== lastExternalLink &&
            ((externalUrl === 'https://github.com/Binger4' || externalUrl === 'https://github.com/Binger4/Deadlock-Ability-Draft' || externalUrl === 'https://discord.gg/SxQjYeA7aW') ||
                (externalUrl.indexOf(AbilityDraftWebsiteOrigin) === 0 && /^(?:presets\/[A-Fa-f0-9]{64}|project-links\/(?:[0-9]|10))$/.test(externalUrl.slice(AbilityDraftWebsiteOrigin.length))))) {
            lastExternalLink = externalId;
            $.DispatchEvent('ExternalBrowserGoToURL', externalUrl);
        }
        if (panel.get('disconnect', '0') === '1' && !exiting) {
            exiting = true; websiteInput(false); AbilityDraftMatch.hideTooltip();
            $.DispatchEvent('CitadelConCommand', 'disconnect');
            return;
        }
        var screen = panel.get('screen', 'connecting');
        var role = panel.get('serverRole', '');
        var transfer = panel.get('transferMatch', '');
        var address = panel.get('matchAddress', '');
        if (transfer && transfer !== transferred && /^[A-Za-z0-9.-]+:[0-9]{2,5}$/.test(address)) {
            transferred = transfer;
            // Address comes only from the trusted server's participant-specific match response.
            $.DispatchEvent('CitadelConCommand', 'connect ' + address);
        }
        var queued = panel.get('queued', '0') === '1';
        var inRoom = panel.get('inRoom', '0') === '1';
        var showWebsite = screen === 'website';
        if (!intro && panel.get('nativeIntro', '0') === '1') { intro = true; closed = true; }
        if (!preparing && panel.get('preparationStarted', '0') === '1') { preparing = true; closed = true; }
        if (!started && panel.get('matchStarted', '0') === '1') { started = true; closed = true; }
        browser.visible = showWebsite;
        panel.find('WaitingScreen').visible = !showWebsite && screen !== 'match';
        panel.find('MatchScreen').visible = screen === 'match';
        if (screen === 'match') AbilityDraftMatch.render(panel);
        panel.text('OpenSiteLabel', 'ABILITY DRAFT');
        panel.find('CreateLobby').visible = role === 'custom' && !queued && !inRoom && screen !== 'connecting';
        panel.find('JoinLobby').visible = role === 'custom' && !queued && !inRoom && screen !== 'connecting';
        panel.find('PublicQueue').visible = role === 'public' && !queued && !inRoom && screen === 'entry';
        panel.find('CancelQueue').visible = queued;
        panel.find('CloseSite').visible = !queued;
        panel.text('CloseSiteLabel', panel.get('draftHub', '0') === '1' ? 'LEAVE SERVER' : 'CLOSE');
        panel.find('ResumeMatch').visible = panel.get('canResumeMatch', '0') === '1';
        panel.text('ResumeMatchLabel', panel.get('spectating', '0') === '1' ? 'WATCH MATCH' : 'RETURN TO MATCH');
        panel.find('AbandonMatch').visible = panel.get('canAbandon', '0') === '1';
        panel.text('AbandonLabel', confirmAbandon ? 'CONFIRM ABANDON' : 'ABANDON MATCH');
        panel.text('EntryKind', role === 'public' ? 'PUBLIC QUEUE' : role === 'match' ? 'MATCH' : 'CUSTOM LOBBY');
        var code = panel.get('roomCode', '');
        if (code !== lastCode) { lastCode = code; codeRevealed = false; }
        var shownCode = codeRevealed ? code : '******';
        panel.text('QueueCount', queued ? panel.get('queueCount', '') : '');
        panel.find('QueueCount').visible = queued;
        panel.text('WaitingTitle', queued ? 'FINDING PLAYERS' : screen === 'match' ? 'PREPARING MATCH' : screen === 'connecting' ? 'CONNECTING' : 'PLAY ABILITY DRAFT');
        panel.text('WaitingHint', queued ? 'Waiting for players.' : screen === 'connecting' ? 'Connecting…' : role === 'public' ? 'Join the public queue.' : 'Create a lobby or enter a room code.');
        panel.text('RoomBadge', inRoom ? 'ROOM ' + shownCode : '');
        panel.find('CopyCode').visible = inRoom && !!code;
        panel.find('ToggleCode').visible = inRoom && !!code;
        panel.text('ToggleCodeLabel', codeRevealed ? 'HIDE' : 'SHOW');
        var status = panel.get('status', '');
        panel.text('siteStatus', status);
        panel.find('siteStatus').visible = !!status;
        var localMatch = panel.get('localMatchTest', '0') === '1';
        panel.find('PlayDraft').visible = panel.get('canPlayMatch', '0') === '1';
        var cooldown = Math.max(0, Number(panel.get('playCooldown', '0')) || 0);
        panel.find('PlayDraft').enabled = cooldown === 0;
        panel.find('PlayDraft').SetHasClass('CoolingDown', cooldown > 0);
        panel.find('PlayDraftCountdown').visible = cooldown > 0;
        panel.text('PlayDraftCountdown', '0:' + (cooldown < 10 ? '0' : '') + cooldown);
        panel.find('FinalizeResult').visible = !localMatch && panel.get('runtimeEnabled', '0') === '1' && panel.get('canFinalize', '0') === '1';
        panel.find('ApplySelf').visible = !localMatch && panel.get('runtimeEnabled', '0') === '1' && panel.get('canApply', '0') === '1' && panel.get('loadoutPrepared', '0') !== '1';
        panel.find('BeginMatch').visible = !localMatch && panel.get('canStartMatch', '0') === '1';
        panel.find('SiteScreen').visible = !closed;
        panel.find('OpenSite').visible = closed;
        // Focus only after the HTML panel and its parent are visible. Toolbar
        // clicks and navigation can otherwise leave keyboard input in Panorama.
        if (websiteVisible !== (showWebsite && !closed)) { websiteVisible = showWebsite && !closed; websiteInput(websiteVisible); }
        AbilityDraftChat.render(websiteVisible && !exiting);
        if (closed || screen !== 'match') AbilityDraftMatch.hideTooltip();
        openWebsite();
    }
    DW.registerPanel({
        init: function (p) {
            panel = p; alive = true;
            browser = p.find('DraftWebsite');
            AbilityDraftChat.init(p, function (open) {
                // Keep HTML mouse input enabled while native text has keyboard
                // focus. Disabling it leaves CHTML's cursor at the Message
                // button, so the next click elsewhere can activate chat again.
                if (!open) focusWebsite();
            });
            loadedUrl = ''; websiteVisible = false; closed = false; started = false; transferred = '';
            codeRevealed = false; lastCode = ''; exiting = false; confirmAbandon = false; preparing = false; intro = false;
            lastItemPurchase = p.get('itemPurchase', '');
            AbilityDraftMatch.reset();
            browser.SetPanelEvent('onactivate', focusWebsite);
            p.onClick('CloseSite', function () {
                // A draft hub has no playable pawn behind the website. Hiding the UI
                // there leaves a black screen; leave the hub, retaining reconnect rules.
                if (panel.get('draftHub', '0') === '1') send('leaveServer');
                else { closed = true; websiteInput(false); render(); }
            });
            p.onClick('OpenSite', function () { closed = false; render(); });

            p.onClick('CreateLobby', function () { send('openCreate'); });
            p.onClick('JoinLobby', function () { send('openJoin'); });
            p.onClick('PublicQueue', function () { send('queueJoin'); });
            p.onClick('CancelQueue', function () { send('queueLeave'); });
            p.onClick('FinalizeResult', function () { send('finalize'); });
            p.onClick('PlayDraft', function () { if (Number(panel.get('playCooldown', '0')) <= 0) send('playMatch'); });
            p.onClick('ResumeMatch', function () { send('resumeMatch'); });
            p.onClick('AbandonMatch', function () {
                if (confirmAbandon) { send('abandon'); confirmAbandon = false; }
                else { confirmAbandon = true; $.Schedule(5, function () { if (alive) { confirmAbandon = false; render(); } }); }
                render();
            });
            p.onClick('ToggleCode', function () { codeRevealed = !codeRevealed; render(); });
            p.onClick('CopyCode', function () {
                $.DispatchEvent('CopyStringToClipboard', panel.get('roomCode', ''), 'Room code copied');
            });
            p.onClick('ApplySelf', function () { send('applySelf'); });
            p.onClick('BeginMatch', function () { send('startMatch'); });
            render();
            p.send('shopReady', '1');
            // The engine creates/replaces the native HUD after the addon and can update its
            // visibility independently of DW state. Continue even while our overlay is closed.
            function watchWaitingHud() {
                if (!alive) return;
                AbilityDraftWaitingStatus.update(panel);
                AbilityDraftUpgrades.update(panel);
                $.Schedule(0.2, watchWaitingHud);
            }
            $.Schedule(0.2, watchWaitingHud);
            $.Schedule(1, function () {
                if (!alive) return;
                $.DispatchEvent('CitadelConCommand', 'ad_menu_entry');
                $.DispatchEvent('CitadelConCommand', 'alias ad_menu_entry ""');
            });
        },
        render: render,
        onDestroy: function () { AbilityDraftChat.destroy(); websiteInput(false); alive = false; websiteVisible = false; AbilityDraftMatch.reset(); AbilityDraftWaitingStatus.restore(); AbilityDraftUpgrades.restore(); }
    });
}());
