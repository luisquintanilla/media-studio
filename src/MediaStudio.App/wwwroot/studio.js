window.studioDialog = (dialog, open) => {
    if (open && !dialog.open) {
        dialog._returnFocus = document.activeElement;
        dialog.showModal();
    } else if (!open && dialog.open) {
        dialog.close();
        if (dialog._returnFocus?.isConnected) dialog._returnFocus.focus();
        return true;
    }
    return false;
};
window.studioFollowConversation = (history, key) => {
    if (history._followTail === undefined) {
        history._followTail = true;
        history.addEventListener("scroll", () => {
            history._followTail = history.scrollHeight - history.scrollTop - history.clientHeight < 48;
        });
    }
    if (history._contentKey === key) return;
    history._contentKey = key;
    if (history._followTail) history.scrollTop = history.scrollHeight;
};
window.studioConversationVisible = () => {
    const panel = document.getElementById("studio-conversation");
    if (!panel) throw new Error("The authoring conversation is not available.");
    return getComputedStyle(panel).display !== "none";
};
document.addEventListener("dragover", event => {
    if (event.target.closest("[data-source-drop]")) event.preventDefault();
});
document.addEventListener("drop", event => {
    const target = event.target.closest("[data-source-drop]");
    if (!target) return;
    event.preventDefault();
    const input = document.getElementById(target.dataset.sourceDrop);
    if (!input || input.disabled) return;
    input.files = event.dataTransfer.files;
    input.dispatchEvent(new Event("change", { bubbles: true }));
});
