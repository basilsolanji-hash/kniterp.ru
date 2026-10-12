using KnitErp.Application;
using KnitErp.Application.Authentication;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Infrastructure;
using KnitErp.Infrastructure.Persistence;
using KnitErp.Infrastructure.Security;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using KnitErp.Web.Components;
using KnitErp.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

// «dotnet KnitErp.Web.dll new-master-key» — создать мастер-ключ для секретов сервера и выйти. Ключ печатается один раз.
if (args.Contains("new-master-key", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(KeyRing.NewKeyBase64());
    return;
}

// Команды сервера с параметрами (их параметры не передаются в конфигурацию приложения):
// «create-organization --inn …» — создать рабочую организацию с Владельцем (docs/pilot.md);
// «emergency-access --email … --reason …» — аварийно восстановить вход пользователю (D06, docs/operations.md).
var provisioning = Array.FindIndex(args, a => a.Equals(OrganizationProvisioning.CommandName, StringComparison.OrdinalIgnoreCase)
                                              || a.Equals(EmergencyCommand.Name, StringComparison.OrdinalIgnoreCase));
var builder = WebApplication.CreateBuilder(provisioning >= 0 ? args[..provisioning] : args);

// Мастер-ключ: шифрование секретов 2FA и ключей cookie, подпись журнала аудита и движений склада.
// Хранится в секретах сервера, не в базе и не в репозитории; рабочая среда без него не запускается.
var masterKey = SecuritySetup.LoadMasterKey(
    builder.Configuration["Security:MasterKey"], builder.Configuration["Security:PreviousMasterKeys"], builder.Environment.IsDevelopment());
KeyRing.Configure(masterKey.Ring);

var connectionString = builder.Configuration.GetConnectionString("KnitErp")
                       ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:KnitErp.");
if (!builder.Environment.IsDevelopment())
{
    SecuritySetup.EnsureEncryptedConnection(connectionString, builder.Configuration.GetValue<bool>("Security:AllowUnencryptedDatabase"));
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddKnitErpApplication();
// D648 (сессия CRM): адрес MAX Bot API для чатов с клиентами; пустая строка в настройках — MAX выключен.
builder.Services.AddSingleton(new KnitErp.Infrastructure.External.MaxOptions(builder.Configuration["Max:BaseUrl"] ?? "https://platform-api.max.ru"));
builder.Services.AddKnitErpInfrastructure(
    connectionString,
    // ИИ-помощник включается ключом из секретов сервера; без ключа он отвечает по справочному центру.
    builder.Configuration["Assistant:ApiKey"] ?? builder.Configuration["ANTHROPIC_API_KEY"],
    builder.Configuration["Assistant:Model"],
    // Заполнение реквизитов по ИНН (DaData, данные ЕГРЮЛ/ЕГРИП) — ключ тоже только из секретов сервера.
    builder.Configuration["Requisites:ApiKey"],
    // Служебная почта (D81): ящик и пароль приложения — из секретов сервера; адрес системы для ссылок — из настроек, не из запроса.
    new KnitErp.Application.Common.EmailOptions(builder.Configuration["Email:Url"], builder.Configuration["Email:User"],
        builder.Configuration["Email:Password"], builder.Configuration["Email:From"], builder.Configuration["App:PublicUrl"],
        builder.Configuration["Email:PickupDirectory"]));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<KnitErp.Application.Workspace.IUiText, KnitErp.Web.Localization.WebUiText>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();

builder.Services.AddAuthentication(AuthSchemes.Session)
    .AddCookie(AuthSchemes.Session, o =>
    {
        o.Cookie.Name = "knitERP.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.LoginPath = "/account/login";
        o.AccessDeniedPath = "/account/login";
        // Допущение D27: сессия живёт 12 часов (рабочая смена) и продлевается при активности.
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionValidation.OnValidatePrincipal;
    })
    .AddCookie(AuthSchemes.Pending, o =>
    {
        o.Cookie.Name = "knitERP.pending";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        o.SlidingExpiration = false;
    })
    // D88: собственный API — только по токену в заголовке Authorization; cookie сессии для API не действуют.
    .AddScheme<AuthenticationSchemeOptions, KnitErp.Web.Api.ApiTokenAuthenticationHandler>(KnitErp.Web.Api.ApiTokenAuthenticationHandler.SchemeName, null);

// D88: интеграции — вебхуки (внутренняя сеть и HTTP — только для разработки и тестов), описание API OpenAPI.
var integrationSettings = new KnitErp.Application.Integrations.IntegrationSettings(builder.Configuration.GetValue<bool>("Webhooks:AllowPrivateNetworks"));
builder.Services.AddSingleton(integrationSettings);
builder.Services.AddSingleton<KnitErp.Application.Integrations.IWebhookSender, KnitErp.Web.Api.HttpWebhookSender>();
if (!builder.Configuration.GetValue<bool>("Webhooks:Disabled"))
{
    builder.Services.AddHostedService<KnitErp.Web.Api.WebhookWorker>();
}

// Модуль «Сотрудники»: статус «В отпуске» по приказам Т-6 (D210).
if (!builder.Configuration.GetValue<bool>("Hr:StatusWorkerDisabled"))
{
    builder.Services.AddHostedService<KnitErp.Web.Api.HrStatusWorker>();
}

// D310 (сессия 4): фоновая загрузка из МойСклад; в тестовых стендах отключается ключом MoySklad:WorkerDisabled.
if (!builder.Configuration.GetValue<bool>("MoySklad:WorkerDisabled"))
{
    builder.Services.AddHostedService<KnitErp.Web.Exchange.MoySkladImportWorker>();
}

// D102: тело запросов API — как и ответы: перечисления строками («Receipt»), имена в camelCase.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1", o =>
{
    o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "knitERP API",
            Version = "v1",
            Description = "Собственный API knitERP. Вход — заголовок Authorization: Bearer knt_… (токен выпускается в разделе «Интеграции»). "
                          + "Изменяющие запросы принимают Idempotency-Key. Ошибки — application/problem+json с полем code. Лимит — 100 запросов за 5 секунд на токен.",
        };
        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http, Scheme = "bearer", Description = "Токен knitERP: knt_…",
        };
        return Task.CompletedTask;
    });
});

// Запрет по умолчанию: любая страница требует входа, кроме явно открытых страниц входа.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// За обратным прокси (Caddy на сервере, deploy/beget): адрес клиента и HTTPS берутся из заголовков X-Forwarded-*,
// но только от прокси из доверенной сети — иначе любой мог бы подставить чужой адрес и обойти ограничение попыток входа.
var proxyNetwork = builder.Configuration["ReverseProxy:KnownNetwork"];
if (!string.IsNullOrWhiteSpace(proxyNetwork))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxyNetwork));
        o.ForwardLimit = 1;
    });
}

builder.Services.AddScoped<KnitErp.Web.Exports.ReportExports>();
builder.Services.AddScoped<KnitErp.Web.Components.Pages.DevGate>(); // D718: очередь запросов разработки в вкладке
builder.Services.AddHealthChecks().AddCheck<KnitErp.Web.Security.DatabaseHealthCheck>("database");

// Ключи cookie и антиподделки — в базе, зашифрованные мастер-ключом: сессии переживают перезапуск и несколько серверов,
// а копия базы ключей не раскрывает.
builder.Services.AddDataProtection()
    .SetApplicationName("knitERP")
    .PersistKeysToDbContext<KnitErpDbContext>()
    .AddKeyManagementOptions(o => o.XmlEncryptor = new MasterKeyXmlEncryptor());

// Подбор паролей и кодов: не больше 10 отправок форм входа в минуту с одного адреса (плюс блокировка учётной записи D25).
// Аудит 10.10.2026: cookie антиподделки — только по HTTPS, как и cookie сессии.
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        // D314 (сессия 4): фид каталога без входа — не больше 30 запросов в минуту с адреса (файл к тому же кэшируется на 5 минут).
        http.Request.Path.StartsWithSegments("/feeds")
            ? RateLimitPartition.GetFixedWindowLimiter("feeds:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) :
        // D121 (база, проверка CRM): вебхук MAX без входа — не больше 120 запросов в минуту с адреса.
        http.Request.Path.StartsWithSegments("/hooks")
            ? RateLimitPartition.GetFixedWindowLimiter("hooks:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) :
        // D88: API — 100 запросов за 5 секунд на токен (ключ — отпечаток заголовка Authorization, без самого секрета).
        http.Request.Path.StartsWithSegments("/api")
            ? RateLimitPartition.GetFixedWindowLimiter("api:" + KnitErp.Web.Api.ApiErrors.RateKey(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromSeconds(5), QueueLimit = 0 })
            : HttpMethods.IsPost(http.Request.Method) && http.Request.Path.StartsWithSegments("/account")
                                                      && !http.Request.Path.StartsWithSegments("/account/logout")
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
    o.OnRejected = async (context, ct) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.HttpContext.Response.Headers.RetryAfter = "5";
            await context.HttpContext.Response.WriteAsJsonAsync(
                KnitErp.Web.Api.ApiErrors.Problem(429, "rate_limit", "Не больше 100 запросов за 5 секунд на токен — повторите через 5 секунд."),
                KnitErp.Web.Api.ApiJson.Options, "application/problem+json", ct);
            return;
        }

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        if (context.HttpContext.Request.Path.StartsWithSegments("/feeds"))
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            await context.HttpContext.Response.WriteAsync("Too many feed requests, retry in a minute.", ct);
            return;
        }

        await context.HttpContext.Response.WriteAsync("Слишком много попыток входа с этого адреса. Подождите минуту и попробуйте снова.", ct);
    };
});

builder.Services.AddSingleton<KnitErp.Web.Exchange.FeedCache>(); // D314: кэш файлов фида каталога (свой, с лимитом объёма)

// D1102 (продажи): вкладка «Условия продаж» в карточке контрагента — вид цены клиента, менеджер, соглашения.
KnitErp.Web.Modules.ModuleUi.AddCounterpartyTab("sales", KnitErp.Web.Modules.ModuleUi.Text("Условия продаж"),
    typeof(KnitErp.Web.Components.Pages.SalesCustomerTermsTab), KnitErp.Domain.Access.Permissions.SalesView);

// D616 (CRM): вкладка «Контакты и сделки» в карточке контрагента — через точку подключения D117.
KnitErp.Web.Modules.ModuleUi.AddCounterpartyTab("crm", KnitErp.Web.Modules.ModuleUi.Text("Контакты и сделки"),
    typeof(KnitErp.Web.Components.Shared.CrmCounterpartyPanel), KnitErp.Domain.Access.Permissions.CrmView);

var app = builder.Build();
app.Logger.LogInformation("Защита данных: {Source}", masterKey.Description);
// D914: индекс базы знаний помощника строится один раз при запуске, в фоне — первый вопрос не ждёт.
_ = Task.Run(KnitErp.Application.Workspace.Knowledge.KnowledgeSearch.Warm);

if (provisioning >= 0)
{
    Environment.ExitCode = args[provisioning].Equals(EmergencyCommand.Name, StringComparison.OrdinalIgnoreCase)
        ? await EmergencyAccessAsync(app, args[(provisioning + 1)..])
        : await CreateOrganizationAsync(app, args[(provisioning + 1)..]);
    return;
}

// «dotnet KnitErp.Web.dll migrate» — отдельный шаг выпуска: применить миграции и выйти.
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    await MigrateAsync(app);
    return;
}

if (app.Environment.IsDevelopment())
{
    await MigrateAsync(app);
    await SeedDevelopmentAsync(app);
}
else
{
    await EnsureSchemaIsCurrentAsync(app);
}

// Сначала заголовки доверенного прокси (схема https от Caddy), и только потом обработчики, которым нужен протокол
// запроса: HSTS добавляется лишь к HTTPS-ответам (аудит 10.10.2026, п. 6).
if (!string.IsNullOrWhiteSpace(proxyNetwork))
{
    // За прокси на HTTPS переводит сам прокси; приложение слушает только внутренний HTTP.
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

if (string.IsNullOrWhiteSpace(proxyNetwork))
{
    app.UseHttpsRedirection();
}
app.Use(SecurityHeaders.Apply);
// Язык интерфейса (D60) — из cookie; форматы чисел и дат у всех русские.
app.UseRequestLocalization(o =>
{
    o.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("ru-RU");
    o.SupportedCultures = [new System.Globalization.CultureInfo("ru-RU")];
    o.SupportedUICultures = KnitErp.Domain.Access.UiLanguages.All.Select(l => new System.Globalization.CultureInfo(l.Culture)).ToList();
    o.RequestCultureProviders = [new Microsoft.AspNetCore.Localization.CookieRequestCultureProvider()];
});
// D88: тело изменяющих запросов API читается дважды — для действия и для хэша Idempotency-Key.
app.Use(async (http, next) =>
{
    if (!http.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    if (!HttpMethods.IsGet(http.Request.Method))
    {
        http.Request.EnableBuffering();
    }

    try
    {
        await next();
    }
    catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
    {
        // Неразборчивый JSON или параметр — 400 problem+json, а не страница ошибки.
        http.Response.Clear();
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(KnitErp.Web.Api.ApiErrors.Problem(400, "format", ex.InnerException?.Message ?? ex.Message),
            KnitErp.Web.Api.ApiJson.Options, "application/problem+json");
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
KnitErp.Web.Api.ApiV1.MapApiV1(app);
app.MapOpenApi("/api/{documentName}/openapi.json").AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext http, IAntiforgery antiforgery, SignInService signIn) =>
{
    if (!await antiforgery.IsRequestValidAsync(http))
    {
        return Results.BadRequest();
    }

    if (SessionClaims.Read(http.User) is { } session)
    {
        await signIn.RecordSignOutAsync(session.UserId, session.OrganizationId, http.RequestAborted);
    }

    await http.SignOutAsync(AuthSchemes.Session);
    await http.SignOutAsync(AuthSchemes.Pending);
    return Results.LocalRedirect("~/account/login");
}).AllowAnonymous();

// Файлы Excel отдаются обычными ссылками: права проверяет сервис, отказ — 403 и запись в журнал.
// Смена языка: ставит cookie и возвращает на страницу. Сохранение в профиль делает меню профиля (D60).
app.MapGet("/language/{code}", (string code, string? returnUrl, HttpContext http) =>
{
    KnitErp.Web.Localization.Text.SetCookie(http, code);
    // Страницы входа — тоже назад на себя (ReturnUrls.Safe их не пропускает: после входа на них не возвращают).
    var back = returnUrl is not null && returnUrl.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)
               && !returnUrl.Contains("://", StringComparison.Ordinal) && !returnUrl.Contains('\\')
        ? returnUrl
        : ReturnUrls.Safe(returnUrl);
    return Results.LocalRedirect(back);
}).AllowAnonymous();

app.MapGet("/catalog/items.xlsx", (ItemExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.ExportAsync(ct), $"nomenklatura-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/catalog/items-template.xlsx", (ItemExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.TemplateAsync(ct), "shablon-nomenklatury.xlsx"));
// Фото номенклатуры (D82): только вошедшему с правом просмотра номенклатуры своей организации; кэш — только в браузере.
app.MapGet("/catalog/photos/{id:long}", async (long id, KnitErp.Application.Catalog.ModificationService photos, HttpContext http, CancellationToken ct) =>
{
    try
    {
        var (contentType, content) = await photos.GetPhotoAsync(id, ct);
        http.Response.Headers.CacheControl = "private, max-age=86400";
        http.Response.Headers.ContentDisposition = "inline";
        return Results.File(content, contentType);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});
app.MapGet("/counterparties.xlsx", (CounterpartyExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.ExportAsync(ct), $"kontragenty-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/counterparties-template.xlsx", (CounterpartyExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.TemplateAsync(ct), "shablon-kontragentov.xlsx"));
app.MapGet("/employees.xlsx", (KnitErp.Application.Structure.EmployeeExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.ExportAsync(ct), $"sotrudniki-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/employees-template.xlsx", (KnitErp.Application.Structure.EmployeeExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.TemplateAsync(ct), "shablon-sotrudnikov.xlsx"));
KnitErp.Web.Crm.CrmEndpoints.MapCrm(app); // D614, D630, D645 (сессия CRM): выгрузки и шаблоны CRM

// Табель подразделения за месяц в Excel (D209): право и область — в сервисе.
app.MapGet("/hr/timesheet.xlsx", async (long department, int year, int month, KnitErp.Application.Hr.TimesheetService timesheets, CancellationToken ct) =>
{
    try
    {
        var (fileName, content) = await timesheets.ExportAsync(department, year, month, ct);
        return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});

// Расчёт заработка месяца в Excel для сверки (D216): право и журнал — в сервисе.
// Шаблон сдельных расценок, заполненный операциями производства без расценки (D225): право — в сервисе.
app.MapGet("/hr/piece-rates-template.xlsx", (KnitErp.Application.Hr.PayrollService payroll, CancellationToken ct) =>
    FileOrForbid(() => payroll.RatesTemplateAsync(ct), "shablon-rascenok.xlsx"));
app.MapGet("/hr/payroll.xlsx", async (int year, int month, KnitErp.Application.Hr.PayrollService payroll, CancellationToken ct) =>
{
    try
    {
        var (fileName, content) = await payroll.ExportAsync(year, month, ct);
        return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});

// Утверждённые начисления месяца для 1С: ЗУП (D213): право — в сервисе.
app.MapGet("/hr/payroll/{id:long}/zup.xml", async (long id, KnitErp.Application.Hr.PayrollEnterpriseDataExport export, CancellationToken ct) =>
{
    try
    {
        var (fileName, content) = await export.ExportAsync(id, ct);
        return Results.File(content, "application/xml", fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
    catch (KnitErp.Domain.Common.BusinessRuleException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

// Скан согласия на обработку ПДн (D200): право и журнал просмотра — в сервисе.
app.MapGet("/employees/consent-scans/{id:long}", async (long id, KnitErp.Application.Hr.PersonalDataService personalData, HttpContext http, CancellationToken ct) =>
{
    try
    {
        var (fileName, contentType, content) = await personalData.GetConsentScanAsync(id, ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(content, contentType, fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});

// Скан договора контрагента (D500): право, своя организация и журнал просмотра — в сервисе; отдаётся вложением.
app.MapGet("/counterparties/contract-files/{id:long}", async (long id, KnitErp.Application.Catalog.CounterpartyCardService cards, HttpContext http, CancellationToken ct) =>
{
    try
    {
        var (fileName, contentType, content) = await cards.GetFileAsync(id, ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(content, contentType, fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});

// Файл модели разработки (D705): право, своя организация и журнал скачивания — в сервисе; всегда вложением и без угадывания типа.
app.MapGet("/dev/files/{id:long}", async (long id, string? inline, KnitErp.Application.Development.DevModelService models, HttpContext http, CancellationToken ct) =>
{
    try
    {
        // ?inline=1 — превью картинки на карточке (без журнала, без имени файла); иначе — скачивание вложением с записью в журнал.
        var preview = inline is "1" or "true";
        var (fileName, contentType, content) = await models.GetFileAsync(id, preview, ct);
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return preview ? Results.File(content, contentType) : Results.File(content, contentType, fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
});

// Excel моделей разработки (D794): выгрузка списка в работе и шаблон загрузки; права — в сервисе.
app.MapGet("/dev/models.xlsx", (KnitErp.Application.Development.DevExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.ExportAsync(new KnitErp.Application.Development.DevModelFilter(IncludeFinal: true), ct), $"modeli-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/dev/models-template.xlsx", (KnitErp.Application.Development.DevExchangeService exchange, CancellationToken ct) =>
    FileOrForbid(() => exchange.TemplateAsync(ct), "shablon-modelei.xlsx"));

app.MapGet("/opening-balances/template.xlsx", (OpeningBalanceService balances, CancellationToken ct) =>
    FileOrForbid(() => balances.LinesTemplateAsync(ct), "shablon-nachalnyh-ostatkov.xlsx"));
app.MapGet("/stock-documents/template.xlsx", (StockDocumentService documents, CancellationToken ct) =>
    FileOrForbid(() => documents.LinesTemplateAsync(ct), "shablon-strok-dokumenta.xlsx"));
// Отчёты и журналы в Excel — те же фильтры, что на экране; права и область складов проверяют сервисы.
app.MapGet("/reports/production.xlsx", (DateOnly from, DateOnly to, KnitErp.Application.Production.ProductionReportService reports, CancellationToken ct) =>
    FileOrForbid(() => reports.ExportAsync(new KnitErp.Application.Production.ProductionReportFilter(from, to), ct), $"proizvodstvo-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/production/scores.xlsx", (DateOnly from, DateOnly to, KnitErp.Application.Production.ProductionScoreService scores, CancellationToken ct) =>
    FileOrForbid(() => scores.ExportAsync(from, to, ct), $"pokazateli-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/stock.xlsx", (long? warehouse, byte? type, string? search, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.StockAsync(new StockFilter(warehouse, (KnitErp.Domain.Catalog.ItemType?)type, search), ct),
        $"ostatki-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/movements.xlsx", (long? warehouse, DateOnly? from, DateOnly? to, string? search, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.MovementsAsync(new MovementFilter(warehouse, from, to, search), ct), $"dvizheniya-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/turnover.xlsx", (long? warehouse, DateOnly from, DateOnly to, byte? type, string? search, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.TurnoverAsync(new TurnoverFilter(warehouse, from, to, (KnitErp.Domain.Catalog.ItemType?)type, search), ct),
        $"oboroty-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/inventory-variances.xlsx", (long? warehouse, DateOnly from, DateOnly to, string? search, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.InventoryVariancesAsync(new InventoryVarianceFilter(warehouse, from, to, search), ct),
        $"otkloneniya-inventarizacii-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/sales-by-item.xlsx", (DateOnly from, DateOnly to, string? customer, string? search, KnitErp.Web.Exports.ReportExports export,
        CancellationToken ct) =>
    FileOrForbid(() => export.SalesByItemAsync(new KnitErp.Application.Sales.SalesAnalyticsFilter(from, to, customer, search), ct),
        $"realizacii-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/reports/profitability.xlsx", (DateOnly from, DateOnly to, byte? group, string? customer, KnitErp.Web.Exports.ReportExports export,
        CancellationToken ct) =>
    FileOrForbid(() => export.ProfitabilityAsync(new KnitErp.Application.Sales.SalesAnalyticsFilter(from, to, customer),
        group == 2 ? KnitErp.Application.Sales.ProfitGrouping.Customer : KnitErp.Application.Sales.ProfitGrouping.Item, ct),
        $"pribylnost-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/vat-invoices/issued.xlsx", (DateOnly from, DateOnly to, string? search, long? entity, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.IssuedVatInvoicesAsync(new KnitErp.Application.Taxes.VatJournalFilter(from, to, search, LegalEntityId: entity), ct),
        $"scheta-faktury-vydannye-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/vat-invoices/received.xlsx", (DateOnly from, DateOnly to, string? search, long? entity, KnitErp.Web.Exports.ReportExports export,
        CancellationToken ct) =>
    FileOrForbid(() => export.ReceivedVatInvoicesAsync(new KnitErp.Application.Taxes.VatJournalFilter(from, to, search, LegalEntityId: entity), ct),
        $"scheta-faktury-poluchennye-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));
app.MapGet("/stock-documents.xlsx", (byte? kind, byte? status, DateOnly? from, DateOnly? to, long? warehouse, string? search, int? order,
        KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.StockDocumentsAsync(new StockDocumentListFilter((KnitErp.Domain.Warehousing.StockOperationKind?)kind,
        (KnitErp.Domain.Warehousing.StockDocumentStatus?)status, from, to, warehouse, search, order is null ? null : order == 1), ct), $"reestr-dokumentov-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/inventory.xlsx", (DateOnly? from, DateOnly? to, long? warehouse, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.InventoriesAsync(new InventoryListFilter(from, to, warehouse), ct), $"reestr-inventarizaciy-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/opening-balances.xlsx", (KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.OpeningBalancesAsync(ct), $"reestr-nachalnyh-ostatkov-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/audit.xlsx", (KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.AuditAsync(ct), $"zhurnal-audita-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
// D105: выгрузка для 1С в формате EnterpriseData; права проверяет сервис, ошибки правил — текстом.
app.MapGet("/exchange/enterprise-data.xml", async (DateOnly from, DateOnly to, long? entity, KnitErp.Application.Exchange.EnterpriseDataExportService export,
    CancellationToken ct) =>
{
    try
    {
        var file = await export.ExportAsync(from, to, entity, ct);
        return Results.File(file.Xml, "application/xml", file.FileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
    catch (KnitErp.Domain.Common.BusinessRuleException ex)
    {
        return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});
app.MapGet("/signins.xlsx", (DateOnly? from, DateOnly? to, bool? failures, KnitErp.Web.Exports.ReportExports export, CancellationToken ct) =>
    FileOrForbid(() => export.SignInsAsync(from, to, failures == true, ct), $"zhurnal-vhodov-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));
app.MapGet("/inventory/{id:long}/sheet.xlsx", (long id, InventoryService inventory, CancellationToken ct) =>
    FileOrForbid(() => inventory.CountSheetAsync(id, ct), $"blank-inventarizacii-{id}.xlsx"));

// D310 (сессия 4): отчёт загрузки из МойСклад — итоги и замечания по строкам.
app.MapGet("/exchange/moysklad/runs/{id:long}/report.xlsx", (long id, KnitErp.Application.Exchange.MoySklad.MoySkladImportService import, CancellationToken ct) =>
    FileOrForbid(() => import.ReportAsync(id, ct), $"moysklad-zagruzka-{id}.xlsx"));

// D314 (сессия 4): фид каталога YML по секретной ссылке — без входа (площадка забирает файл сама). Неизвестный или выключенный ключ — 404.
// Готовый файл кэшируется на 5 минут по отпечатку ключа: частый опрос не нагружает базу.
app.MapGet("/feeds/{key}.yml", async (string key, KnitErp.Application.Catalog.CatalogFeedService feeds, KnitErp.Web.Exchange.FeedCache cache,
    HttpContext http, CancellationToken ct) =>
{
    if (!KnitErp.Domain.Catalog.CatalogFeed.LooksLikeKey(key))
    {
        return Results.NotFound();
    }

    var cacheKey = "feed:" + Convert.ToHexString(KnitErp.Domain.Catalog.CatalogFeed.Hash(key));
    if (!cache.TryGet(cacheKey, out var xml))
    {
        var file = await feeds.BuildAsync(key, $"{http.Request.Scheme}://{http.Request.Host}/", ct);
        if (file is null)
        {
            return Results.NotFound();
        }

        xml = file.Xml;
        cache.Set(cacheKey, xml);
    }

    http.Response.Headers.CacheControl = "public, max-age=300";
    http.Response.Headers["X-Robots-Tag"] = "noindex";
    return Results.File(xml!, "application/xml; charset=utf-8");
}).AllowAnonymous();

// Встраивать страницы knitERP в чужие сайты запрещено (как и в общей CSP).
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = "'none'");

app.Run();

static async Task<IResult> FileOrForbid(Func<Task<byte[]>> build, string fileName)
{
    try
    {
        return Results.File(await build(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
    catch (AccessDeniedException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
    catch (KnitErp.Domain.Common.BusinessRuleException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}

static async Task MigrateAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<KnitErpDbContext>();
    var pending = await KnitErp.Infrastructure.DependencyInjection.PendingMigrationsAsync(db);
    await KnitErp.Infrastructure.DependencyInjection.MigrateDatabaseAsync(db);
    app.Logger.LogInformation("Применено миграций: {Count} ({Names})", pending.Count, string.Join(", ", pending));
    var encrypted = await SecuritySetup.EncryptLegacySecretsAsync(db);
    if (encrypted > 0)
    {
        app.Logger.LogInformation("Зашифровано секретов 2FA, записанных до включения шифрования: {Count}", encrypted);
    }
}

// Рабочая среда сама схему не меняет: при неприменённых миграциях приложение не запускается,
// чтобы новая версия кода не работала со старой схемой.
static async Task EnsureSchemaIsCurrentAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<KnitErpDbContext>();
    var pending = await KnitErp.Infrastructure.DependencyInjection.PendingMigrationsAsync(db);
    if (pending.Count > 0)
    {
        throw new InvalidOperationException(
            $"Схема базы устарела, неприменённые миграции: {string.Join(", ", pending)}. Выполните «dotnet KnitErp.Web.dll migrate».");
    }
}

// Создание рабочей организации на сервере. Ссылка установки пароля Владельца печатается один раз и в журнал не пишется:
// её нужно передать Владельцу лично. Повторный запуск с тем же ИНН ничего не создаёт.
static async Task<int> CreateOrganizationAsync(WebApplication app, string[] args)
{
    var (request, errors) = OrganizationProvisioning.Parse(args);
    if (request is null)
    {
        foreach (var e in errors)
        {
            Console.Error.WriteLine(e);
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Пример:");
        foreach (var line in OrganizationProvisioning.Usage)
        {
            Console.Error.WriteLine(line);
        }

        return 2;
    }

    await EnsureSchemaIsCurrentAsync(app);
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<KnitErpDbContext>();
    var inn = request.Command.Inn.Trim();
    if (await db.Organizations.AnyAsync(o => o.CountryCode == request.Command.CountryCode && o.Inn == inn))
    {
        Console.Error.WriteLine($"Организация с ИНН {inn} уже есть. Ничего не создано.");
        return 1;
    }

    try
    {
        var created = await scope.ServiceProvider.GetRequiredService<OrganizationService>().CreateWithOwnerAsync(request.Command);
        app.Logger.LogInformation("Создана организация {OrganizationId} с Владельцем {UserId}", created.OrganizationId, created.OwnerUserId);
        Console.WriteLine($"Создана организация: {request.Command.ShortName} (№ {created.OrganizationId}).");
        if (created.OwnerSetupToken is { } token)
        {
            Console.WriteLine("Ссылка для Владельца — установить пароль и подключить вход с кодом (действует 72 часа, показывается один раз):");
            Console.WriteLine(OrganizationProvisioning.SetupLink(request.BaseUrl, token));
        }
        else
        {
            Console.WriteLine("У Владельца уже есть пароль: организация появится у него в списке после входа.");
        }

        return 0;
    }
    catch (KnitErp.Domain.Common.BusinessRuleException ex)
    {
        Console.Error.WriteLine($"Не создано: {ex.Message}");
        return 1;
    }
}

// Аварийное восстановление входа (D06): сброс 2FA и резервных кодов, ссылка нового пароля. Ссылка печатается один раз.
static async Task<int> EmergencyAccessAsync(WebApplication app, string[] args)
{
    var (values, errors) = EmergencyCommand.Parse(args);
    if (values is null)
    {
        foreach (var e in errors)
        {
            Console.Error.WriteLine(e);
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(EmergencyCommand.Usage);
        return 2;
    }

    await EnsureSchemaIsCurrentAsync(app);
    using var scope = app.Services.CreateScope();
    try
    {
        var result = await scope.ServiceProvider.GetRequiredService<KnitErp.Application.Authentication.RecoveryCodeService>()
            .EmergencyRestoreAsync(values.Email, values.Reason);
        app.Logger.LogWarning("Аварийное восстановление входа пользователю {UserId}", result.UserId);
        Console.WriteLine($"Вход с кодом (2FA) и резервные коды пользователя «{result.DisplayName}» сброшены, блокировка входа снята.");
        Console.WriteLine("Прежний пароль действует. Ссылка для нового пароля (72 часа, показывается один раз):");
        Console.WriteLine(OrganizationProvisioning.SetupLink(values.BaseUrl, result.RecoveryToken));
        Console.WriteLine("При следующем входе Владелец и Администратор подключат приложение-аутентификатор заново и выпустят новые резервные коды.");
        return 0;
    }
    catch (NotFoundException)
    {
        Console.Error.WriteLine($"Пользователь {values.Email} не найден. Ничего не изменено.");
        return 1;
    }
    catch (KnitErp.Domain.Common.BusinessRuleException ex)
    {
        Console.Error.WriteLine($"Не выполнено: {ex.Message}");
        return 1;
    }
}

// Тестовая организация для локальной разработки. Реальные данные сюда не попадают.
static async Task SeedDevelopmentAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<KnitErpDbContext>();
    if (!await db.Organizations.AnyAsync())
    {
        var orgs = scope.ServiceProvider.GetRequiredService<OrganizationService>();
        var created = await orgs.CreateWithOwnerAsync(new CreateOrganizationCommand(
            "Общество с ограниченной ответственностью «Солвер»", "ООО «Солвер»", "9705239429", "770501001",
            KppVerified: true, "Europe/Moscow", "owner@kniterp.local", "Владелец (разработка)"));

        // Только для разработки: ссылка установки пароля Владельца выводится в лог локального запуска.
        app.Logger.LogWarning("Пароль Владельца не задан. Откройте /account/invite?token={Token}", created.OwnerSetupToken);
    }
}

public partial class Program;

