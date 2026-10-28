// 全站共用的確認視窗（SweetAlert2）。
// C# 端用 JS.InvokeAsync<bool>("appDialog.confirm", new { title, text, ... }) 呼叫，回傳 Promise<boolean>，
// Blazor 會等使用者按完才往下走。SweetAlert2 沒載到（例如 CDN 連不上）時退回瀏覽器原生 confirm。
// items：要條列出來的項目（例如批次下單的網域清單），會跳脫成純文字再組成 <ul>，不會被當成 HTML 執行。
window.appDialog = {
    confirm({ title, text, items = null, confirmText = '確定', cancelText = '取消', icon = 'question', danger = false } = {}) {
        const list = Array.isArray(items) ? items : [];
        if (!window.Swal) return Promise.resolve(window.confirm([title, text, list.join('\n')].filter(Boolean).join('\n\n')));

        const escape = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
        const options = {
            icon,
            title,
            showCancelButton: true,
            confirmButtonText: confirmText,
            cancelButtonText: cancelText,
            confirmButtonColor: danger ? '#dc3545' : '#2f9e7e',
            focusCancel: true,
        };
        if (list.length > 0) {
            options.html =
                (text ? `<p style="margin:0 0 .5rem">${escape(text)}</p>` : '') +
                '<ul style="text-align:left;max-height:40vh;overflow-y:auto;margin:0;padding-left:1.25rem">' +
                list.map(i => `<li>${escape(i)}</li>`).join('') +
                '</ul>';
        } else {
            options.text = text;
        }
        return Swal.fire(options).then(r => r.isConfirmed);
    },
};
