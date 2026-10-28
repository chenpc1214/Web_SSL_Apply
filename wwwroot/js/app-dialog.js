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

    // 二選一（外加取消）：回傳 'confirm'、'deny'，取消或關掉回傳 ''。
    // SweetAlert2 沒載到時退回原生 confirm，只問「要不要選 confirm 那個」，按取消就什麼都不做——
    // 呼叫端要把比較安全的選項（例如不扣款的那個）放在 confirm，不會因為退回原生視窗就誤選另一個。
    choose({ title, text, items = null, confirmText, denyText, cancelText = '取消', icon = 'question' } = {}) {
        const list = Array.isArray(items) ? items : [];
        if (!window.Swal) {
            const ok = window.confirm([title, text, list.join('\n'), `按「確定」＝${confirmText}`].filter(Boolean).join('\n\n'));
            return Promise.resolve(ok ? 'confirm' : '');
        }

        const escape = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
        return Swal.fire({
            icon,
            title,
            html:
                (text ? `<p style="margin:0 0 .5rem">${escape(text)}</p>` : '') +
                (list.length > 0
                    ? '<ul style="text-align:left;max-height:40vh;overflow-y:auto;margin:0;padding-left:1.25rem">' +
                      list.map(i => `<li>${escape(i)}</li>`).join('') + '</ul>'
                    : ''),
            showDenyButton: true,
            showCancelButton: true,
            confirmButtonText: confirmText,
            denyButtonText: denyText,
            cancelButtonText: cancelText,
            confirmButtonColor: '#2f9e7e',
            denyButtonColor: '#dc3545',
            focusCancel: true,
        }).then(r => (r.isConfirmed ? 'confirm' : r.isDenied ? 'deny' : ''));
    },
};
