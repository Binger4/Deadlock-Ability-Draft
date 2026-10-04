(function () {
    'use strict';
    var lastSent = -Infinity;
    function activity(event) {
        if (!event.isTrusted || Date.now() - lastSent < 3000) return;
        var query = new URLSearchParams(location.search), payload;
        var room = /^\/room\/([^/]+)\/(?:lobby|draft)\/?$/.exec(location.pathname);
        if (room && query.get('inGame') === 'true' && query.get('playerId'))
            payload = { roomCode: decodeURIComponent(room[1]), playerId: query.get('playerId') };
        else if (/^\/(?:create|join)\/?$/.test(location.pathname) && query.get('gameEntry'))
            payload = { gameEntry: query.get('gameEntry') };
        else return;
        lastSent = Date.now();
        fetch('/ingame-activity', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload) }).catch(function () {});
    }
    // Presence polls, timers and automatic Blazor renders are not user activity.
    ['pointerdown', 'pointermove', 'keydown', 'input', 'wheel'].forEach(function (name) {
        document.addEventListener(name, activity, { capture: true, passive: true });
    });
}());
