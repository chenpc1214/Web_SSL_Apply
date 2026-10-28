// 分頁切回來（從背景變成可見）時通知 C# 重新載入資料。
// 總覽頁的資料是開頁時撈一次，別的分頁在步驟1存檔、標記待換發之後，切回總覽要看到最新狀態，
// 不用使用者自己按 F5。C# 端：onVisible(DotNetObjectReference, "方法名") 回傳 id，Dispose 時呼叫 off(id)。
window.pageRefresh = {
    _handlers: new Map(),
    _nextId: 0,

    onVisible(dotNetRef, methodName) {
        const id = ++this._nextId;
        const handler = () => {
            if (document.visibilityState === 'visible') dotNetRef.invokeMethodAsync(methodName).catch(() => { });
        };
        document.addEventListener('visibilitychange', handler);
        this._handlers.set(id, handler);
        return id;
    },

    off(id) {
        const handler = this._handlers.get(id);
        if (!handler) return;
        document.removeEventListener('visibilitychange', handler);
        this._handlers.delete(id);
    },
};
