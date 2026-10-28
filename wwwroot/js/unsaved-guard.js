// 站內離開提醒。
// 這個專案的路由不是互動式的（App.razor 的 <Routes /> 沒有 rendermode），點側邊欄等站內連結
// 走的是 blazor.web.js 的增強式導覽，伺服器端 NavigationLock 的 OnBeforeInternalNavigation
// 根本不會被呼叫——所以改在瀏覽器這端攔連結點擊。頁面元件每次 render 完呼叫
// unsavedGuard.update() 告訴這裡「現在該不該攔、要顯示什麼訊息」。
// F5／關分頁仍交給 NavigationLock 的 ConfirmExternalNavigation（瀏覽器原生視窗，文字無法自訂）。
window.unsavedGuard = (() => {
    let message = null; // null = 目前頁面沒有註冊提醒
    let dirty = false;
    // 欄位剛改完就馬上點連結時，伺服器那邊「change 事件 → 重新 render → update(true)」這一趟
    // 可能還沒回來；先在瀏覽器端記一筆「有輸入過」，寧可多問一次也不要讓資料不見。
    // 只算標了 data-unsaved-guard 的區塊（例如步驟3的勾選框就不算）。
    let pendingInput = false;

    const isGuarding = () => message !== null && (dirty || pendingInput);

    const reset = () => { message = null; dirty = false; pendingInput = false; };

    const markInput = e => {
        if (message !== null && e.target instanceof Element && e.target.closest('[data-unsaved-guard]'))
            pendingInput = true;
    };
    document.addEventListener('input', markInput, true);
    document.addEventListener('change', markInput, true);

    const confirmLeave = () => appDialog.confirm({
        icon: 'warning',
        title: '確定要離開這頁嗎？',
        text: message,
        confirmText: '確定離開',
        cancelText: '留在這頁',
        danger: true,
    });

    const go = href => {
        reset(); // 使用者已經確認要離開，不要再擋
        if (window.Blazor?.navigateTo) Blazor.navigateTo(href);
        else location.href = href;
    };

    // 掛在 window 的捕獲階段，比 blazor.web.js 掛在 document 上的導覽處理先拿到點擊。
    window.addEventListener('click', e => {
        if (!isGuarding() || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        const link = e.target instanceof Element ? e.target.closest('a[href]') : null;
        if (!link || link.hasAttribute('download') || (link.target && link.target !== '_self')) return;

        const url = new URL(link.href, document.baseURI);
        if (url.origin !== location.origin) return; // 站外連結會真的卸載頁面，交給瀏覽器原生提醒
        if (url.hash && url.pathname === location.pathname && url.search === location.search) return; // 同頁錨點不會離開

        e.preventDefault();
        e.stopImmediatePropagation();
        confirmLeave().then(ok => { if (ok) go(url.href); });
    }, true);

    return {
        /** 頁面元件每次 render 完呼叫：isDirty 以伺服器算的為準，同時清掉瀏覽器端暫記的輸入。 */
        update(isDirty, msg) { message = msg; dirty = isDirty; pendingInput = false; },
        /** 頁面元件 Dispose 時呼叫。 */
        clear: reset,
        /** 給程式導覽用（例如側邊欄「切換」）：跟點連結一樣先檢查再走。 */
        navigate(href) {
            const url = new URL(href, document.baseURI).href;
            if (!isGuarding()) { go(url); return; }
            confirmLeave().then(ok => { if (ok) go(url); });
        },
    };
})();
