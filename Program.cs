using System.Text;
using Microsoft.EntityFrameworkCore;
using SslWebConsole.Components;
using SslWebConsole.Data;
using SslWebConsole.Services;

// 讓 Encoding.GetEncoding("big5") 這類舊編碼拿得到——客戶的 web.config 可能不是 UTF-8，
// 注入放行規則時要用原檔的編碼讀寫，不然中文會變亂碼（見 WebConfigPatchService.DetectEncoding）。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<ScriptsRootOptions>(o => o.ScriptsRoot = builder.Configuration["ScriptsRoot"] ?? "");

// sslautomation.db 放在 SslWebConsole 專案資料夾本身（不是 ScriptsRoot），
// 跟程式碼一起、不混進 PowerShell_Scripts 那個純資料/腳本用的資料夾。
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "sslautomation.db");

builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDataProtection();

builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<NicSrsConfigLoader>();
builder.Services.AddSingleton<WorkRootResolver>();
builder.Services.AddSingleton<CsvDomainStore>();
builder.Services.AddSingleton<DomainRepository>();
builder.Services.AddSingleton<CsvSeedImporter>();
builder.Services.AddSingleton<LocalityRepository>();
builder.Services.AddSingleton<CertificateFileRepository>();
builder.Services.AddSingleton<CertificateOrderRepository>();
builder.Services.AddSingleton<DnsCheckService>();
builder.Services.AddSingleton<OpenSslRunner>();
builder.Services.AddSingleton<CsrService>();
builder.Services.AddSingleton<OrderStatusService>();
builder.Services.AddSingleton<WinScpBridgeClient>();
builder.Services.AddSingleton<ValidationUploadService>();
builder.Services.AddSingleton<WebConfigPatchService>();
builder.Services.AddSingleton<NicSrsApiClient>();
builder.Services.AddSingleton<CertCollectService>();
builder.Services.AddSingleton<CertFileScanService>();
builder.Services.AddSingleton<PfxPasswordService>();
builder.Services.AddSingleton<PfxService>();
builder.Services.AddSingleton<PlaceOrderService>();
builder.Services.AddSingleton<ReissueService>();
builder.Services.AddSingleton<CrmSettingsRepository>();
builder.Services.AddSingleton<CrmVerifyService>();
builder.Services.AddSingleton<CertArchiveService>();
// 人工模式（其他憑證商）：不存資料庫，資料在品牌 CSV、進度看網域資料夾（見 ManualWorkspace）。
builder.Services.AddSingleton<ManualWorkspace>();
builder.Services.AddSingleton<BrandCsvStore>();
builder.Services.AddSingleton<CertZipImportService>();
builder.Services.AddSingleton<ManualArchiveService>();

var app = builder.Build();

// 建表（EnsureCreated：單機個人工具，不用 EF Migrations）+ 種子資料（縣市清單、
// 首次啟動時把既有 SSL_List.csv 匯進 Domains 表）。
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using (var db = dbFactory.CreateDbContext())
    {
        db.Database.EnsureCreated();
        // EnsureCreated 只在資料庫不存在時建表，不會幫既有資料表加新欄位——後來才加的欄位在這裡補，
        // 部署到既有的 sslautomation.db 不用手動跑 ALTER TABLE。
        EnsureColumn(db, "Settings", "ArchiveRoot", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "Domains", "OnHoldSince", "TEXT NULL");
        EnsureColumn(db, "Domains", "PendingMode", "TEXT NULL");
        EnsureColumn(db, "Settings", "ManualWorkRoot", "TEXT NOT NULL DEFAULT ''");
    }

    scope.ServiceProvider.GetRequiredService<LocalityRepository>().SeedIfEmpty();
    scope.ServiceProvider.GetRequiredService<CsvSeedImporter>().ImportIfEmpty();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void EnsureColumn(AppDbContext db, string table, string column, string definition)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();

    using (var check = conn.CreateCommand())
    {
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
    }

    using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
    alter.ExecuteNonQuery();
}
