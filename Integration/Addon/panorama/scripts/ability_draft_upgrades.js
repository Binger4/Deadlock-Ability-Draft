var AbilityDraftUpgrades = (function () {
    'use strict';
    var bindings = [], lastHud = null, warned = false;
    function root() { var p = $.GetContextPanel(); while (p.GetParent()) p = p.GetParent(); return p; }
    function descendants(p, type, out) {
        if (p.paneltype === type) out.push(p);
        p.Children().forEach(function (c) { descendants(c, type, out); });
        return out;
    }
    function style(p, values) { Object.keys(values).forEach(function (k) { p.style[k] = values[k]; }); }
    function train(binding) {
        if (!binding.state || !binding.state.canTrain) return;
        // Same validated server/native path as ALT+number. Never set upgrade bits
        // or decrement points optimistically on the client.
        $.DispatchEvent('CitadelConCommand', 'trainorupgradeability ' + binding.slot);
    }
    function bind(pips, icon, slot) {
        var saved = [];
        pips.Children().forEach(function (child) {
            saved.push({ panel: child, visibility: child.style.visibility });
            child.style.visibility = 'collapse';
        });
        var container = $.CreatePanel('Panel', pips, 'AbilityDraftUpgradePips');
        style(container, { width: '100%', height: '100%', flowChildren: 'up' });
        var binding = { pips: pips, container: container, slot: slot, saved: saved, rows: [], state: null };
        for (var tier = 1; tier <= 3; tier++) {
            var row = $.CreatePanel('Button', container, 'AbilityDraftTier' + tier);
            style(row, { width: '100%', height: '40px', marginTop: '5px', borderRadius: '100px' });
            var text = $.CreatePanel('Label', row, '');
            style(text, { horizontalAlign: 'center', verticalAlign: 'center', fontSize: '22px', fontWeight: 'bold' });
            (function (t, button) { button.SetPanelEvent('onactivate', function () {
                if (binding.state && (binding.state.bits & (1 << t)) === 0 && t === nextTier(binding.state.bits)) train(binding);
            }); }(tier, row));
            binding.rows.push({ panel: row, label: text, tier: tier });
        }
        var button = icon.FindChildTraverse('button_container');
        if (button) {
            binding.click = $.CreatePanel('Button', button, 'AbilityDraftTrainClick');
            // TAB opens gScoreboardOpen; gDetailView is a separate HUD mode.
            // Use a real button above the native image so M1 emits onactivate.
            binding.click.BLoadLayout('file://{resources}/layout/ability_draft_upgrade_click.xml', false, false);
            binding.click.SetPanelEvent('onactivate', function () { train(binding); });
        }
        return binding;
    }
    function nextTier(bits) { for (var tier = 1; tier <= 3; tier++) if ((bits & (1 << tier)) === 0) return tier; return 4; }
    function restore() {
        bindings.forEach(function (b) {
            b.saved.forEach(function (s) { if (s.panel.IsValid()) s.panel.style.visibility = s.visibility || null; });
            if (b.container.IsValid()) b.container.DeleteAsync(0);
            if (b.click && b.click.IsValid()) b.click.DeleteAsync(0);
        });
        bindings = []; lastHud = null;
    }
    function update(panel) {
        var state;
        try { state = JSON.parse(panel.get('nativeSkills', '')); } catch (_) { return; }
        if (!state.enabled || state.abilities.length !== 4) { restore(); return; }
        var hud = root().FindChildTraverse('hud_signature');
        if (!hud) return;
        if (hud !== lastHud || bindings.some(function (b) { return !b.pips.IsValid(); })) {
            restore();
            var pips = descendants(hud, 'CitadelHudAbilityUpgradePips', []);
            var icons = descendants(hud, 'CitadelAbilityIcon', []);
            if (pips.length !== 4 || icons.length !== 4) {
                if (!warned) { $.Msg('[AbilityDraft] Native upgrade HUD not ready: pips=' + pips.length + ' icons=' + icons.length); warned = true; }
                return;
            }
            lastHud = hud;
            for (var i = 0; i < 4; i++) bindings.push(bind(pips[i], icons[i], i + 1));
            $.Msg('[AbilityDraft] Drafted upgrade HUD bound to all four slots');
        }
        bindings.forEach(function (b, i) {
            var ability = state.abilities[i];
            if (ability.slot !== b.slot) return;
            b.state = ability;
            b.saved.forEach(function (s) { if (s.panel.IsValid()) s.panel.style.visibility = 'collapse'; });
            b.rows.forEach(function (row) {
                var learned = (ability.bits & (1 << row.tier)) !== 0;
                var next = row.tier === nextTier(ability.bits);
                var ready = !learned && next && ability.canTrain;
                row.panel.enabled = ready;
                row.panel.style.opacity = learned || ready ? '1' : '0.45';
                row.panel.style.backgroundColor = learned ? '#c8b0f5' : ready ? '#69d897' : '#191c1b';
                row.label.style.color = learned || ready ? '#17221c' : '#c3c6c3';
                row.label.text = learned ? '✓' : !(ability.bits & 1) && next ? (ready ? 'UNLOCK' : 'LOCKED') : String([1, 2, 5][row.tier - 1]);
            });
        });
    }
    return { update: update, restore: restore };
}());
