using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <summary>
/// Выгрузка утверждённых начислений в 1С: «Зарплата и управление персоналом 3» (D213) — отдельный класс рядом с
/// <c>EnterpriseDataExportService</c>, тот не меняется. Формат — сообщение EnterpriseData 1.8 с теми же заголовками; в теле — сотрудник
/// (табельный номер и ФИО, без СНИЛС, паспорта и адреса) и документ начислений за месяц по видам. Состав объектов — черновой:
/// сопоставление с правилами обмена ЗУП согласуется с сессией № 1 и бухгалтером перед первой загрузкой.
/// Ссылки — постоянные GUID от организации и записи: повторная выгрузка обновляет документ в 1С, а не дублирует.
/// </summary>
public sealed class PayrollEnterpriseDataExport(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private static readonly XNamespace Msg = "http://www.1c.ru/SSL/Exchange/Message";
    private static readonly XNamespace Ed = "http://v8.1c.ru/edi/edi_stnd/EnterpriseData/" + KnitErp.Application.Exchange.EnterpriseDataExportService.FormatVersion;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<(string FileName, byte[] Content)> ExportAsync(long runId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollCalculate, ct);
        var run = await db.PayrollRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Расчёт");
        if (run.Status != PayrollStatus.Approved)
        {
            throw new BusinessRuleException("hr.payroll.export_draft", "В 1С выгружается только утверждённый расчёт.");
        }

        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId).ToListAsync(ct);
        var ids = lines.Select(l => l.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName }).ToListAsync(ct);
        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => new { o.FullName, o.Inn }).SingleAsync(ct);

        var body = new XElement(Ed + "Body");
        foreach (var e in employees.OrderBy(e => e.PersonnelNumber))
        {
            body.Add(new XElement(Ed + "Справочник.Сотрудники",
                new XElement(Ed + "КлючевыеСвойства",
                    new XElement(Ed + "Ссылка", Guid(ctx.OrganizationId, "employee", e.Id)),
                    new XElement(Ed + "ТабельныйНомер", e.PersonnelNumber),
                    new XElement(Ed + "Наименование", e.MiddleName is null ? $"{e.LastName} {e.FirstName}" : $"{e.LastName} {e.FirstName} {e.MiddleName}"))));
        }

        var document = new XElement(Ed + "Документ.НачисленияЗаработкаKnitERP",
            new XElement(Ed + "КлючевыеСвойства",
                new XElement(Ed + "Ссылка", Guid(ctx.OrganizationId, "payroll", run.Id)),
                new XElement(Ed + "Дата", run.To.ToString("yyyy-MM-dd", Inv) + "T00:00:00"),
                new XElement(Ed + "Номер", $"ЗП-{run.Year}-{run.Month:00}"),
                new XElement(Ed + "Организация", new XElement(Ed + "Наименование", org.FullName), new XElement(Ed + "ИНН", org.Inn))),
            new XElement(Ed + "ПериодРегистрации", run.From.ToString("yyyy-MM-dd", Inv) + "T00:00:00"),
            new XElement(Ed + "Начисления",
                lines.OrderBy(l => l.EmployeeId).ThenBy(l => l.Kind).Select(l => new XElement(Ed + "Строка",
                    new XElement(Ed + "Сотрудник", Guid(ctx.OrganizationId, "employee", l.EmployeeId)),
                    new XElement(Ed + "ВидНачисления", Kind(l.Kind)),
                    new XElement(Ed + "Описание", l.Description),
                    new XElement(Ed + "Количество", l.Quantity.ToString("0.######", Inv)),
                    new XElement(Ed + "Сумма", l.Amount.ToString("0.00", Inv))))));
        body.Add(document);

        var message = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("Message", new XAttribute(XNamespace.Xmlns + "msg", Msg),
                new XElement(Msg + "Header",
                    new XElement(Msg + "Format", Ed.NamespaceName),
                    new XElement(Msg + "CreationDate", clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", Inv)),
                    new XElement(Msg + "AvailableVersion", KnitErp.Application.Exchange.EnterpriseDataExportService.FormatVersion)),
                body));
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            message.Save(writer);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PayrollExported, nameof(PayrollRun), runId.ToString(),
            null, $"{run.Month:00}.{run.Year}: сотрудников {employees.Count}, строк {lines.Count}", "EnterpriseData", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return ($"knitERP-ZUP-{run.Year}-{run.Month:00}.xml", buffer.ToArray());
    }

    /// <summary>Вид начисления — код для правил обмена ЗУП (сопоставляется с видами расчёта в 1С).</summary>
    private static string Kind(PayrollLineKind kind) => kind switch
    {
        PayrollLineKind.Piece => "СдельнаяОплата",
        PayrollLineKind.Salary => "ОплатаПоОкладу",
        PayrollLineKind.Night => "ДоплатаЗаРаботуВНочноеВремя",
        PayrollLineKind.Overtime => "ОплатаСверхурочныхЧасов",
        PayrollLineKind.Holiday => "ОплатаПраздничныхИВыходныхДней",
        _ => kind.ToString(),
    };

    private static string Guid(long organizationId, string kind, long id)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"kniterp:{organizationId}:{kind}:{id}"));
        return new System.Guid(hash).ToString();
    }
}
