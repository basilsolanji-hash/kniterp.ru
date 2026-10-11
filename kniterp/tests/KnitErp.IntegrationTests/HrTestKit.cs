using KnitErp.Application.Access;
using KnitErp.Application.Hr;
using KnitErp.Application.Organizations;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Сервисы модуля «Сотрудники» от имени пользователя — отдельно от общего набора, чтобы не мешать другим модулям.</summary>
public sealed class HrServices(KnitErpDbContext db, PersonalDataService personalData) : IAsyncDisposable
{
    public KnitErpDbContext Db { get; } = db;
    public PersonalDataService PersonalData { get; } = personalData;

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>Организация, структура и сотрудники для тестов кадров. Данные вымышленные (152-ФЗ).</summary>
public static class HrTestKit
{
    private static int _innSeed = 600_000_000;
    private static int _personSeed = 100_200_300;

    public static HrServices Hr(this SqlTestHost host, long userId, long organizationId)
    {
        var db = host.NewDb();
        var user = new TestUser { UserId = userId, OrganizationId = organizationId };
        var guard = new AccessGuard(db, user, host.Clock);
        return new HrServices(db, new PersonalDataService(db, guard, user, host.Clock));
    }

    public static async Task<CreatedOrganization> CreateOrganizationAsync(this SqlTestHost host)
    {
        var inn = NextLegalInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая фабрика {inn}", $"Фабрика {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    /// <summary>Цех, должность и принятый сотрудник.</summary>
    public static async Task<(long DepartmentId, long PositionId, long EmployeeId)> HireAsync(
        this SqlTestHost host, CreatedOrganization org, string number = "0001", string lastName = "Петрова", DateOnly? hiredOn = null)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var dep = await s.Structure.CreateDepartmentAsync($"Цех {Guid.NewGuid():N}"[..12], null);
        var pos = await s.Structure.CreatePositionAsync($"Вязальщица {Guid.NewGuid():N}"[..16]);
        var id = await s.Employees.HireAsync(new EmployeeCommand(number, lastName, "Анна", "Сергеевна", dep, pos, hiredOn ?? new DateOnly(2026, 1, 15)));
        return (dep, pos, id);
    }

    public static async Task<long> InviteActiveAsync(this SqlTestHost host, CreatedOrganization org, string roleCode,
        long? departmentId = null, long? employeeId = null)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, SystemRoles.IsAdministrative(roleCode) ? "Тест" : null,
                departmentId, employeeId))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Тестовый СНИЛС с верным контрольным числом (не принадлежит реальному человеку: проверка только формальная).</summary>
    public static string NextSnils()
    {
        var body = Interlocked.Increment(ref _personSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * (9 - i);
        }

        var check = sum < 100 ? sum : sum % 101 % 100;
        return body + check.ToString("D2");
    }

    /// <summary>Тестовый ИНН физического лица с верными контрольными разрядами.</summary>
    public static string NextPersonInn()
    {
        var body = "77" + Interlocked.Increment(ref _personSeed).ToString("D9")[1..] + "0";
        body = body[..10];
        int[] w11 = [7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
        int[] w12 = [3, 7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
        var c11 = Check(body, w11);
        var c12 = Check(body + c11, w12);
        return body + c11 + c12;

        static int Check(string s, int[] w)
        {
            var sum = 0;
            for (var i = 0; i < w.Length; i++)
            {
                sum += (s[i] - '0') * w[i];
            }

            return sum % 11 % 10;
        }
    }

    private static string NextLegalInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }
}
