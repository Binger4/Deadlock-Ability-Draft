var AbilityDraftMatch = (function () {
    var previous = '', previousRoster = '', tooltipGeneration = 0, hidden = [], tooltipHome = null, activeAnchor = null;
    function label(parent, text, cls) {
        var p = $.CreatePanel('Label', parent, ''); p.text = text; p.AddClass(cls); return p;
    }
    function icon(parent, group, key, cls) {
        var path = AbilityDraftAssets[group + '/' + key];
        if (!path) return;
        var p = $.CreatePanel('Image', parent, ''); p.AddClass(cls); p.SetImage('file://{images}/' + path); return p;
    }
    function hideTooltip(anchor) {
        tooltipGeneration++;
        hidden.forEach(function (saved) { if (saved.panel.IsValid()) saved.panel.visible = saved.visible; });
        hidden = [];
        if (activeAnchor && activeAnchor.IsValid()) $.DispatchEvent('CitadelHideAbilityDetailsTooltip', activeAnchor);
        activeAnchor = null;
        if (tooltipHome && tooltipHome.manager.IsValid() && tooltipHome.parent.IsValid())
            tooltipHome.manager.SetParent(tooltipHome.parent);
        tooltipHome = null;
    }
    function hideUpgradePart(target) {
        hidden.push({ panel: target, visible: target.visible });
        target.visible = false;
    }
    function tooltipLayer(anchor) {
        var root = anchor; while (root.GetParent()) root = root.GetParent();
        var manager = root.FindChildTraverse('TooltipManager');
        if (!manager) return null;
        // Host the existing manager above this screen while hovering. Raising its
        // stock ancestors would also raise the loading screen over the draft panel.
        tooltipHome = { manager: manager, parent: manager.GetParent() };
        manager.SetParent($.GetContextPanel().FindChildTraverse('AbilityDraftTooltipHost'));
        return manager;
    }
    function hover(anchor, player, ability, tier) {
        if (!anchor || ability.token === null || player.playerSlot === null) return;
        anchor.hittest = true;
        anchor.SetPanelEvent('onmouseover', function () {
            hideTooltip(anchor);
            var manager = tooltipLayer(anchor);
            activeAnchor = anchor;
            // Native tooltip arguments: ability CUtlStringToken and CPlayerSlot.
            $.DispatchEvent('CitadelShowAbilityDetailsTooltip', anchor, ability.token, player.playerSlot);
            var generation = ++tooltipGeneration;
            if (!tier) return;
            function focusUpgrade(attempt) {
                if (generation !== tooltipGeneration || !anchor.IsValid()) return;
                var upgrades = manager && manager.FindChildTraverse('AbilityUpgradeList');
                if (!upgrades || upgrades.GetChildCount() < 3) {
                    if (attempt < 5) $.Schedule(0.05, function () { focusUpgrade(attempt + 1); });
                    return;
                }
                var children = upgrades.Children();
                var contents = upgrades.GetParent().GetParent().FindChildTraverse('TooltipContents');
                $.Msg('[AbilityDraft] Upgrade tooltip tier=' + tier + ' rows=' + children.length + ' contents=' + !!contents);
                if (contents) hideUpgradePart(contents);
                children.forEach(function (child, index) {
                    if (index !== tier - 1) hideUpgradePart(child);
                });
            }
            $.Schedule(0.05, function () { focusUpgrade(0); });
        });
        anchor.SetPanelEvent('onmouseout', function () { hideTooltip(anchor); });
    }
    function render(panel) {
        var raw = panel.get('matchRoster', '');
        if (raw === previous) return;
        var view;
        try { view = JSON.parse(raw); } catch (_) { return; }
        previous = raw;
        var phase = view.phase;
        panel.text('MatchPhase', phase === 'Preparation' ? 'PREPARE · ' + view.seconds + 's' : phase === 'Playing' ? 'MATCH IN PROGRESS' : phase === 'Failed' ? 'MATCH PREPARATION FAILED' : phase === 'Completed' ? 'MATCH ENDED' : 'WAITING FOR YOUR TEAM');
        var roster = JSON.stringify(view.players);
        if (roster === previousRoster) return;
        previousRoster = roster;
        hideTooltip($.GetContextPanel());
        ['HiddenKing', 'Archmother'].forEach(function (team) {
            var root = panel.find('Match' + team); root.RemoveAndDeleteChildren();
            view.players.filter(function (p) { return p.team === team; }).forEach(function (player) {
                var row = $.CreatePanel('Panel', root, ''); row.AddClass('MatchPlayer');
                row.SetHasClass('Offline', !player.connected || player.abandoned);
                var heading = $.CreatePanel('Panel', row, ''); heading.AddClass('MatchPlayerHeading');
                icon(heading, 'HeroesMini', player.hero, 'RosterHero');
                var name = $.CreatePanel('Panel', heading, ''); name.AddClass('RosterName');
                label(name, player.name, 'PlayerName');
                label(name, player.heroName + (player.level !== null ? ' · LVL ' + player.level : '') + (player.abandoned ? ' · ABANDONED' : !player.connected ? ' · DISCONNECTED' : ''), 'HeroName');
                var slots = $.CreatePanel('Panel', row, ''); slots.AddClass('RosterSlots');
                player.abilities.forEach(function (ability) {
                    var slot = $.CreatePanel('Panel', slots, ''); slot.AddClass('RosterAbility'); slot.AddClass(ability.state);
                    var image = icon(slot, 'Abilities', ability.key, 'RosterAbilityIcon');
                    hover(image, player, ability, 0);
                    var bars = $.CreatePanel('Panel', slot, ''); bars.AddClass('UpgradeBars');
                    [1, 2, 3].forEach(function (tier) {
                        var bar = $.CreatePanel('Panel', bars, ''); bar.AddClass('UpgradeBar');
                        bar.SetHasClass('Learned', ability.upgradeTier >= tier);
                        hover(bar, player, ability, tier);
                    });
                    label(slot, ability.name, 'AbilityName');

                });
            });
        });
    }
    return { render: render, hideTooltip: hideTooltip,
        reset: function () { previous = ''; previousRoster = ''; hideTooltip(); } };
}());
