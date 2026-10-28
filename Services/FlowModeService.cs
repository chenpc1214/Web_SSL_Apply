namespace SslWebConsole.Services;

public enum FlowMode
{
    /// <summary>續約／新申請——完整走步驟1~6，會呼叫付費的 /ssl/place。</summary>
    Renew,

    /// <summary>免費重簽／更新憑證——沿用現有訂閱，步驟1不需要 ProductCode/Years，步驟3只走 /ssl/replace。</summary>
    Reissue,
}

/// <summary>
/// 記錄「目前是續約還是重發/更新模式」，供側邊欄跟步驟1~6頁面共用。單例——這是單一操作者
/// 用的內部工具，不用每個瀏覽器分頁各自獨立狀態。
/// 沒選過模式（Current 是 null）時，側邊欄的步驟1~6連結不開放，頁面本身也會擋掉直接網址存取。
/// </summary>
public sealed class FlowModeService
{
    public FlowMode? Current { get; private set; }

    public void Set(FlowMode mode) => Current = mode;

    public void Clear() => Current = null;
}
