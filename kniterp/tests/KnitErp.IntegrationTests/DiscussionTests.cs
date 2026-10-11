using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D107: обсуждение документов — комментарии, упоминания, уведомления, лента, права и изоляция организаций.</summary>
public sealed class DiscussionTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 781_000_000;

    [SqlFact]
    public async Task Comment_mention_notify_reply_edit_and_delete()
    {
        var start = host.Clock.UtcNow;
        try
        {
            var (org, yarn, shop, _) = await SetUpAsync();
            var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper, yarn);
            long docYarn, docShop, first;
            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Нить»", null, null, true, false, null));
                var reason = await s.Db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                    .Select(r => r.Id).FirstAsync();
                docYarn = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(yarn, null, supplier, reason, new DateOnly(2026, 10, 11), null));
                docShop = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(shop, null, supplier, reason, new DateOnly(2026, 10, 11), null));

                // Пустой текст, несуществующий объект и коллега не из организации — отказ.
                Assert.Equal("discussion.text_required",
                    (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "  ", null))).Code);
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.AddAsync(TaggedEntity.StockDocument, 999_999_999, "Текст", null));
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "Текст", [999_999_999]));
                Assert.Equal("discussion.mentions", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                    s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "Текст", Enumerable.Range(1, 11).Select(i => 1_000_000_000L + i).ToList()))).Code);

                var thread = await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn);
                Assert.Empty(thread.Comments);
                Assert.Contains(thread.Colleagues, c => c.UserId == keeper);
                Assert.DoesNotContain(thread.Colleagues, c => c.UserId == org.OwnerUserId);

                // Себя упомянуть нельзя — молча пропускается; коллега получает упоминание.
                first = await s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "Проверьте крашение партии, пожалуйста", [keeper, org.OwnerUserId, keeper]);
                await s.Discussions.AddAsync(TaggedEntity.StockDocument, docShop, "Цех: документ без кладовщика пряжи", null);
                var c = Assert.Single((await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn)).Comments);
                Assert.True(c is { IsMine: true, CanEdit: true, CanDelete: true, IsDeleted: false });
                Assert.Single(c.Mentions);
                Assert.Equal(1, (await s.Discussions.CountsAsync(TaggedEntity.StockDocument, [docYarn, docShop, 1]))[docYarn]);
            }

            // Кладовщик склада пряжи: видит обсуждение своего склада, уведомление об упоминании; чужой склад — «не найдено».
            host.Clock.UtcNow = start.AddMinutes(5);
            await using (var s = host.As(keeper, org.OrganizationId))
            {
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docShop));
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.AddAsync(TaggedEntity.StockDocument, docShop, "Чужой склад", null));
                var mention = Assert.Single(await s.Notifications.ListAsync(), n => n.Key == $"comment:{first}");
                Assert.Contains("упомянул(а) вас", mention.Text);
                Assert.Equal($"stock-documents/{docYarn}#discussion", mention.Href);

                var feed = await s.Discussions.FeedAsync();
                Assert.Equal([first], feed.Select(f => f.Comment.Id));
                Assert.True(feed[0].MentionsMe);
                Assert.Single(await s.Discussions.FeedAsync(onlyMentions: true));

                var own = Assert.Single((await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn)).Comments);
                Assert.True(own is { IsMine: false, CanEdit: false, CanDelete: false });
                Assert.Equal("discussion.not_author", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Discussions.EditAsync(first, "Правка", own.RowVersion))).Code);
                Assert.Equal("discussion.not_author", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Discussions.DeleteAsync(first, own.RowVersion))).Code);

                await s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "Проверил, крашение одно", null);
            }

            // Владелец: ответ кладовщика пришёл уведомлением; правка в окне 15 минут, позже — нельзя; старая версия — конфликт.
            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                Assert.Contains(await s.Notifications.ListAsync(), n => n.Text.StartsWith("Новый ответ в обсуждении", StringComparison.Ordinal)
                                                                        && n.Href == $"stock-documents/{docYarn}#discussion");
                var mine = (await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn)).Comments.Single(x => x.Id == first);
                await s.Discussions.EditAsync(first, "Проверьте крашение партии КБ-7, пожалуйста", mine.RowVersion);
                await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.Discussions.EditAsync(first, "Ещё раз", mine.RowVersion));
                var thread = await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn);
                mine = thread.Comments.Single(x => x.Id == first);
                Assert.Equal(("Проверьте крашение партии КБ-7, пожалуйста", true), (mine.Text, mine.EditedAtUtc is not null));

                host.Clock.UtcNow = start.AddMinutes(16);
                Assert.False((await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn)).Comments.Single(x => x.Id == first).CanEdit);
                Assert.Equal("discussion.edit_window",
                    (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Discussions.EditAsync(first, "Поздно", mine.RowVersion))).Code);

                // Владелец ведёт организацию — может убрать чужой комментарий; текст стирается, место в ленте остаётся.
                var reply = thread.Comments.Single(x => x.Id != first);
                Assert.True(reply is { IsMine: false, CanDelete: true });
                await s.Discussions.DeleteAsync(reply.Id, reply.RowVersion);
                var deleted = (await s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn)).Comments.Single(x => x.Id == reply.Id);
                Assert.True(deleted is { IsDeleted: true, Text: "", CanDelete: false, CanEdit: false });
                Assert.Equal(1, (await s.Discussions.CountsAsync(TaggedEntity.StockDocument, [docYarn]))[docYarn]);
                Assert.DoesNotContain(await s.Notifications.ListAsync(), n => n.Key == $"comment:{reply.Id}");

                var actions = await s.Db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.EntityType == "DiscussionComment")
                    .Select(a => a.Action).ToListAsync();
                Assert.Equal(3, actions.Count(a => a == AuditActions.CommentAdded));
                Assert.Contains(AuditActions.CommentEdited, actions);
                Assert.Contains(AuditActions.CommentDeleted, actions);
                Assert.Equal(2, (await s.Discussions.FeedAsync()).Count(f => f.Object.EntityId == docYarn));
            }

            // Другая организация: обсуждение, комментарий и лента не видны.
            var (other, _, _, otherItem) = await SetUpAsync();
            await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
            {
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.ThreadAsync(TaggedEntity.StockDocument, docYarn));
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.AddAsync(TaggedEntity.StockDocument, docYarn, "Чужое", null));
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.DeleteAsync(first, [0]));
                Assert.Empty(await s.Discussions.FeedAsync());
                Assert.Empty(await s.Discussions.CountsAsync(TaggedEntity.StockDocument, [docYarn]));
                // Упомянуть пользователя другой организации нельзя.
                await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.AddAsync(TaggedEntity.Item, otherItem, "Текст", [keeper]));
            }
        }
        finally
        {
            host.Clock.UtcNow = start;
        }
    }

    [SqlFact]
    public async Task Database_rejects_unknown_target_and_deleted_comment_with_text()
    {
        var (org, _, _, item) = await SetUpAsync();
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = await s.Discussions.AddAsync(TaggedEntity.Item, item, "Проверка ограничений", null);
        }

        await using var db = host.NewDb();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE kniterp.discussion_comments SET Target = 99 WHERE Id = {id}"));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE kniterp.discussion_comments SET DeletedAtUtc = SYSUTCDATETIME(), DeletedByUserId = AuthorUserId WHERE Id = {id}"));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO kniterp.discussion_mentions (CommentId, UserId) VALUES ({id}, {org.OwnerUserId}), ({id}, {org.OwnerUserId})"));
    }

    private async Task<(CreatedOrganization Org, long Yarn, long Shop, long Item)> SetUpAsync()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            var shop = await s.Warehouses.CreateWarehouseAsync("Цех", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            return (org, yarn, shop, item);
        }
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode, long? warehouseId)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, null, WarehouseId: warehouseId))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private static string NextValidInn()
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
