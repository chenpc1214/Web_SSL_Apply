using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Services;

public sealed record LocalityOption(string Name, string DisplayLabel);

/// <summary>
/// Locality 下拉選單的資料來源。Localities 表是空的時候 seed 台灣 20 個縣市（新竹、嘉義
/// 不分縣市各一筆），之後要增減不用改程式碼、直接在 Localities 表加減即可。下拉選單顯示
/// 中文，但存進 Domains.Locality / CSR 的還是英文（Name）——CA 端的 CSR Subject 只接受英文。
/// </summary>
public sealed class LocalityRepository
{
    private static readonly (string Name, string Zh)[] TaiwanCounties =
    [
        ("Taipei", "台北"), ("New Taipei", "新北"), ("Taoyuan", "桃園"), ("Taichung", "台中"),
        ("Tainan", "台南"), ("Kaohsiung", "高雄"), ("Keelung", "基隆"),
        ("Hsinchu", "新竹"), ("Chiayi", "嘉義"),
        ("Miaoli", "苗栗"), ("Changhua", "彰化"), ("Nantou", "南投"),
        ("Yunlin", "雲林"), ("Pingtung", "屏東"), ("Yilan", "宜蘭"),
        ("Hualien", "花蓮"), ("Taitung", "台東"), ("Penghu", "澎湖"), ("Kinmen", "金門"),
        ("Lienchiang", "連江"),
    ];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public LocalityRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>下拉選單用：value 存英文（Name），顯示文字優先用中文（DisplayNameZh 有填的話）。</summary>
    public List<LocalityOption> GetAllOptions()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Localities
            .OrderBy(l => l.SortOrder ?? int.MaxValue)
            .ThenBy(l => l.Name)
            .AsEnumerable()
            .Select(l => new LocalityOption(l.Name, string.IsNullOrWhiteSpace(l.DisplayNameZh) ? l.Name : $"{l.DisplayNameZh}（{l.Name}）"))
            .ToList();
    }

    public void SeedIfEmpty()
    {
        using var db = _dbFactory.CreateDbContext();
        if (db.Localities.Any()) return;

        for (var i = 0; i < TaiwanCounties.Length; i++)
        {
            var (name, zh) = TaiwanCounties[i];
            db.Localities.Add(new LocalityEntity { Name = name, DisplayNameZh = zh, SortOrder = i });
        }

        db.SaveChanges();
    }
}
