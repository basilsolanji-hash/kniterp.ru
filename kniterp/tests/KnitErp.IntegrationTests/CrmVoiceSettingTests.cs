using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D132 п. 5: диктовка заметок CRM по умолчанию выключена; включает и выключает только Владелец (Администратор с правом настроек — нет),
/// каждое изменение — в журнал аудита; настройка своя у каждой организации.
/// </summary>
public sealed class CrmVoiceSettingTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Voice_dictation_is_off_by_default_and_only_owner_turns_it_on()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var other = await host.CreateCrmOrganizationAsync();
        var admin = await host.CrmUserAsync(org, SystemRoles.Administrator);
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);

        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            Assert.False(await crm.Settings.VoiceDictationAsync());
            Assert.False((await crm.Settings.GetAsync()).CanSetVoiceDictation);
        }

        await using (var crm = host.Crm(admin, org.OrganizationId))
        {
            var settings = await crm.Settings.GetAsync();
            Assert.True(settings.CanEdit);
            Assert.False(settings.CanSetVoiceDictation);
            Assert.Equal("crm.settings.voice_owner", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Settings.SetVoiceDictationAsync(true, settings.AutomationVersion))).Code);
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var settings = await crm.Settings.GetAsync();
            Assert.True(settings.CanSetVoiceDictation);
            await crm.Settings.SetVoiceDictationAsync(true, settings.AutomationVersion);
            Assert.True(await crm.Settings.VoiceDictationAsync());
            // Устаревшая версия строки — конфликт, а не тихая перезапись.
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => crm.Settings.SetVoiceDictationAsync(false, settings.AutomationVersion ?? [1]));
            Assert.True(await crm.Db.AuditEntries.AsNoTracking().AnyAsync(a => a.OrganizationId == org.OrganizationId
                && a.Action == AuditActions.CrmSettingsChanged && a.After == "диктовка заметок включена (распознаёт браузер)"));
        }

        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            Assert.True(await crm.Settings.VoiceDictationAsync());
        }

        await using (var crm = host.Crm(other.OwnerUserId, other.OrganizationId))
        {
            Assert.False(await crm.Settings.VoiceDictationAsync()); // чужая организация — своя настройка
        }
    }
}
