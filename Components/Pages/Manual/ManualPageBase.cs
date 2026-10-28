using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SslWebConsole.Models;
using SslWebConsole.Services;

namespace SslWebConsole.Components.Pages.Manual;

/// <summary>
/// 人工模式步驟1選的品牌，存在瀏覽器的 sessionStorage：每個分頁各自一份、F5 還在，
/// 一個分頁做 A 品牌、另一個分頁做 B 品牌也不會互相影響。不放網址（使用者的決定）。
/// sessionStorage 不能用（例如瀏覽器封鎖）就當沒選過，回步驟1重選。
/// </summary>
public static class ManualBrandSession
{
    private const string Key = "manualBrand";

    public static async Task<string?> GetAsync(IJSRuntime js)
    {
        try { return await js.InvokeAsync<string?>("sessionStorage.getItem", Key); }
        catch (JSException) { return null; }
    }

    public static async Task SetAsync(IJSRuntime js, string brand)
    {
        try { await js.InvokeVoidAsync("sessionStorage.setItem", Key, brand); }
        catch (JSException) { }
    }
}

/// <summary>
/// 人工模式步驟2~5共用：開頁時讀出步驟1選的品牌、載入那個品牌 CSV 的網域。品牌存在
/// sessionStorage，只有互動式渲染後才讀得到，所以在第一次 OnAfterRender 才載入，載入前畫面顯示「載入中」。
/// 沒設定工作區、沒選品牌、品牌 CSV 讀不了（例如被 Excel 鎖住）都放在 Problem，頁面顯示提示。
/// </summary>
public abstract class ManualPageBase : ComponentBase
{
    [Inject] protected ManualWorkspace Workspace { get; set; } = default!;
    [Inject] protected BrandCsvStore CsvStore { get; set; } = default!;
    [Inject] protected NicSrsConfigLoader ConfigLoader { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected string Brand { get; private set; } = "";
    protected List<DomainRecord> Rows { get; private set; } = [];
    protected string? Problem { get; private set; }
    protected bool Ready { get; private set; }

    protected int PageSize { get; private set; } = 20;
    protected int CurrentPage { get; set; } = 1;
    protected List<DomainRecord> PagedRows => Rows.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await LoadBrandAsync();
        Ready = true;
        StateHasChanged();
    }

    private async Task LoadBrandAsync()
    {
        if (!Workspace.IsConfigured)
        {
            Problem = "尚未設定「其他憑證商工作區」，請先到公用設定填寫。";
            return;
        }

        var brand = await ManualBrandSession.GetAsync(JS);
        if (!Workspace.BrandExists(brand))
        {
            Problem = "還沒選擇要處理的品牌，請先到步驟1選擇。";
            return;
        }

        Brand = brand!;
        PageSize = ConfigLoader.Load().PageSize;
        try
        {
            Workspace.EnsureFolders();
            Rows = CsvStore.Load(Brand).Rows;
            OnBrandLoaded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"讀不了 {Brand}.csv：{ex.Message}（如果在 Excel 裡開著，請先關閉再重新整理）";
        }
    }

    /// <summary>重新讀過品牌 CSV 之後換掉清單（例如封存後有網域搬到「_完成.csv」）。</summary>
    protected void ReplaceRows(List<DomainRecord> rows) => Rows = rows;

    /// <summary>品牌 CSV 載入完之後，各頁要做的額外準備（例如算每個網域的檔案狀態）。</summary>
    protected virtual void OnBrandLoaded() { }

    protected string DomainDir(DomainRecord row) => Workspace.DomainDir(row.CommonName);

    protected bool HasFile(DomainRecord row, string pattern)
    {
        var dir = DomainDir(row);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
    }
}
