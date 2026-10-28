using Microsoft.AspNetCore.Components;

namespace SslWebConsole.Services;

public enum FlowMode
{
    /// <summary>續約／新申請——完整走步驟1~6，會呼叫付費的 /ssl/place。</summary>
    Renew,

    /// <summary>免費重簽／更新憑證——沿用現有訂閱，步驟1不需要 ProductCode/Years，步驟3只走 /ssl/replace。</summary>
    Reissue,
}

/// <summary>
/// 模式放在網址路徑的第一段（/renew/...、/reissue/...），不是存在全站共用的單例裡——
/// 單例的話，分頁 B 在總覽切換模式，分頁 A 還停在步驟3的畫面一按下去就會用錯模式
/// （例如本來要免費重簽，變成付費下單）。放在網址裡每個分頁各自獨立，F5 也不會丟失。
/// 步驟頁在 OnInitialized 讀一次就固定下來，之後按鈕一律用那個值。
/// </summary>
public static class FlowModeRoute
{
    public const string RenewSegment = "renew";
    public const string ReissueSegment = "reissue";

    /// <summary>從目前網址讀出模式；第一段不是 renew/reissue（例如總覽、公用設定）就回 null。</summary>
    public static FlowMode? FromUri(NavigationManager nav)
    {
        var path = nav.ToBaseRelativePath(nav.Uri);
        var end = path.IndexOfAny(['/', '?', '#']);
        var first = end < 0 ? path : path[..end];
        return first.ToLowerInvariant() switch
        {
            RenewSegment => FlowMode.Renew,
            ReissueSegment => FlowMode.Reissue,
            _ => null,
        };
    }

    /// <summary>某個模式下某個步驟的網址，step 例如 "domains"、"order"。</summary>
    public static string Path(FlowMode mode, string step) =>
        $"/{(mode == FlowMode.Reissue ? ReissueSegment : RenewSegment)}/{step}";

    /// <summary>給畫面顯示用的模式名稱。</summary>
    public static string DisplayName(FlowMode mode) => mode == FlowMode.Reissue ? "重發/更新" : "續約";
}
