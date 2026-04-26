window.deadlockAbilityDraft = window.deadlockAbilityDraft || {};
window.deadlockAbilityDraft.soundStorageKey = "deadlockAbilityDraft.soundMuted";

window.deadlockAbilityDraft.getSoundMuted = () => {
    try {
        return localStorage.getItem(window.deadlockAbilityDraft.soundStorageKey) === "true";
    } catch {
        return false;
    }
};

window.deadlockAbilityDraft.setSoundMuted = (muted) => {
    try {
        localStorage.setItem(window.deadlockAbilityDraft.soundStorageKey, muted ? "true" : "false");
    } catch {
    }
};

window.deadlockAbilityDraft.playSound = (soundPath) => {
    try {
        if (window.deadlockAbilityDraft.getSoundMuted()) {
            return;
        }

        const audio = new Audio(soundPath);
        audio.volume = 0.75;
        const play = audio.play();
        if (play && typeof play.catch === "function") {
            play.catch(() => { });
        }
    } catch {
    }
};

window.deadlockAbilityDraft.copyText = async (text) => {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }

        const textarea = document.createElement("textarea");
        textarea.value = text;
        textarea.setAttribute("readonly", "");
        textarea.style.position = "fixed";
        textarea.style.opacity = "0";
        document.body.appendChild(textarea);
        textarea.select();
        const ok = document.execCommand("copy");
        document.body.removeChild(textarea);
        return ok;
    } catch {
        return false;
    }
};
