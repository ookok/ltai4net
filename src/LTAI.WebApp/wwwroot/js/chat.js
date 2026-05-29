// LTAI Chat — input UX enhancements
// Auto-focus, auto-resize, file-picker interop

window.ltaiChat = {
    focusInput: function (id) {
        const el = document.getElementById(id);
        if (el) setTimeout(function () { el.focus(); el.click(); }, 100);
    },

    autoResize: function (id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 300) + 'px';
    },

    triggerFilePicker: function (inputId, dotnetRef, callbackMethod) {
        const input = document.getElementById(inputId);
        if (!input) return;
        input.onchange = function () {
            if (!input.files || input.files.length === 0) return;
            const file = input.files[0];
            const reader = new FileReader();
            reader.onload = function (e) {
                const payload = {
                    name: file.name,
                    content: e.target.result,
                    size: file.size
                };
                dotnetRef.invokeMethodAsync(callbackMethod, JSON.stringify(payload));
            };
            reader.readAsText(file);
            input.value = '';
        };
        input.click();
    }
};
