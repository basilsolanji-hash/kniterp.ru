using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmReferenceDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_activities_kind",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_activities_kind",
                schema: "kniterp",
                table: "crm_activities",
                sql: "[Kind] BETWEEN 1 AND 16 AND [Target] IN (1, 2, 3) AND [Source] BETWEEN 1 AND 6");

            // D650: типовые источники и причины отказа — организациям, у которых CRM уже открыта (у новых их создаёт EnsureDefaultsAsync).
            // Значение с тем же названием (в том числе в архиве) не добавляется: пользователь мог его переименовать или убрать сознательно.
            migrationBuilder.Sql(@"EXEC(N'
INSERT INTO [kniterp].[crm_references] ([OrganizationId], [Kind], [Name], [SortOrder], [IsArchived])
SELECT o.[OrganizationId], v.[Kind], v.[Name], ISNULL(m.[MaxOrder], 0) + v.[Ord] * 10, 0
FROM (SELECT DISTINCT [OrganizationId] FROM [kniterp].[crm_pipelines]) o
CROSS JOIN (VALUES
(1, 1, N''Сайт''),
(1, 2, N''Звонок''),
(1, 3, N''Выставка''),
(1, 4, N''Маркетплейс''),
(1, 5, N''Рекомендация''),
(1, 6, N''Соцсети''),
(1, 7, N''Мессенджер''),
(1, 8, N''Заявка с сайта (форма)''),
(1, 9, N''Входящее письмо''),
(1, 10, N''Повторный клиент''),
(1, 11, N''Холодный звонок''),
(1, 12, N''Рассылка''),
(1, 13, N''Wildberries''),
(1, 14, N''Ozon''),
(1, 15, N''Яндекс Маркет''),
(1, 16, N''Авито''),
(1, 17, N''Яндекс Директ''),
(1, 18, N''Поисковая выдача''),
(1, 19, N''Карты (Яндекс, 2ГИС)''),
(1, 20, N''ВКонтакте''),
(1, 21, N''Telegram''),
(1, 22, N''WhatsApp''),
(1, 23, N''MAX''),
(1, 24, N''Тендер''),
(1, 25, N''B2B-площадка''),
(1, 26, N''Партнёр или агент''),
(1, 27, N''Шоурум''),
(1, 28, N''Отраслевой каталог''),
(1, 29, N''Ярмарка''),
(1, 30, N''Офлайн-реклама''),
(2, 1, N''Дорого''),
(2, 2, N''Не подошли сроки''),
(2, 3, N''Выбрали другого поставщика''),
(2, 4, N''Нет ответа''),
(2, 5, N''Нет потребности''),
(2, 6, N''Дубликат''),
(2, 7, N''Не подошёл минимальный тираж''),
(2, 8, N''Не подошло качество образцов''),
(2, 9, N''Не подошли материалы или пряжа''),
(2, 10, N''Не подошли условия оплаты''),
(2, 11, N''Нужна отсрочка платежа''),
(2, 12, N''Не устроила доставка''),
(2, 13, N''Не делаем такое изделие''),
(2, 14, N''Нет мощностей на эти сроки''),
(2, 15, N''Отложили закупку''),
(2, 16, N''Бюджет урезан''),
(2, 17, N''Ушли к прежнему поставщику''),
(2, 18, N''Сделали своими силами''),
(2, 19, N''Проект отменён''),
(2, 20, N''Не прошли тендер''),
(2, 21, N''Нет нужной сертификации или маркировки''),
(2, 22, N''Ошибочная заявка или спам''),
(2, 23, N''Не дозвонились, клиент пропал'')
) v([Kind], [Ord], [Name])
OUTER APPLY (SELECT MAX(r.[SortOrder]) AS [MaxOrder] FROM [kniterp].[crm_references] r WHERE r.[OrganizationId] = o.[OrganizationId] AND r.[Kind] = v.[Kind]) m
WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[crm_references] r WHERE r.[OrganizationId] = o.[OrganizationId] AND r.[Kind] = v.[Kind] AND r.[Name] = v.[Name])
')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_activities_kind",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_activities_kind",
                schema: "kniterp",
                table: "crm_activities",
                sql: "[Kind] BETWEEN 1 AND 9 AND [Target] IN (1, 2, 3) AND [Source] BETWEEN 1 AND 6");
        }
    }
}
