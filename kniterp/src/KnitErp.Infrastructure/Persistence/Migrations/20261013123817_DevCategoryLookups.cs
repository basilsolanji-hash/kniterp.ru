using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D733: категория изделия и вид рисунка — записи справочника разработки «Категории изделий» (вид 6) и «Виды рисунков» (вид 7).
    /// Встроенные значения создаются каждой организации, ссылки моделей, шаблонов, табеля мер и рисунков переносятся по прежнему коду.
    /// </summary>
    public partial class DevCategoryLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D733: категория изделия и вид рисунка — записи справочника разработки (вид 6 и 7) вместо чисел в коде.
            migrationBuilder.DropCheckConstraint(name: "ck_dev_patterns_kind", schema: "kniterp", table: "dev_patterns");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_models_category", schema: "kniterp", table: "dev_models");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_category_templates_category", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups", sql: "[Kind] BETWEEN 1 AND 7");

            migrationBuilder.AddColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_models", type: "bigint", nullable: true);
            migrationBuilder.AddColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_category_templates", type: "bigint", nullable: true);
            migrationBuilder.AddColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_category_measures", type: "bigint", nullable: true);
            migrationBuilder.AddColumn<long>(name: "KindId", schema: "kniterp", table: "dev_patterns", type: "bigint", nullable: true);

            // Встроенные значения — каждой организации (у кого записей этого вида ещё нет); код прежнего числа — в [Number].
            migrationBuilder.Sql(@"EXEC(N'INSERT INTO [kniterp].[dev_lookups] ([OrganizationId], [Kind], [Name], [Number], [SortOrder], [IsArchived])
                SELECT o.[Id], v.[Kind], v.[Name], v.[Code], v.[Sort], 0
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES
                    (6, N''Свитер'', 1, 10),
                    (6, N''Кардиган'', 2, 20),
                    (6, N''Джемпер'', 3, 30),
                    (6, N''Шапка'', 4, 40),
                    (6, N''Шарф'', 5, 50),
                    (6, N''Платье'', 6, 60),
                    (6, N''Полотно'', 7, 70),
                    (6, N''Жилет'', 8, 80),
                    (6, N''Комплект'', 9, 90),
                    (6, N''Аксессуар'', 10, 100),
                    (6, N''Прочее'', 99, 110),
                    (7, N''Жаккард двухсторонний'', 1, 10),
                    (7, N''Жаккард с протяжками'', 2, 20),
                    (7, N''Интарсия'', 3, 30),
                    (7, N''Структурный (косы, араны, ажур)'', 4, 40),
                    (7, N''Полосы'', 5, 50),
                    (7, N''Принт по полотну'', 6, 60)) v ([Kind], [Name], [Code], [Sort])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[dev_lookups] l WHERE l.[OrganizationId] = o.[Id] AND l.[Kind] = v.[Kind])')");

            // Ссылки — на запись своей организации с тем же кодом.
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [CategoryId] = l.[Id] FROM [kniterp].[dev_models] x
                JOIN [kniterp].[dev_lookups] l ON l.[OrganizationId] = x.[OrganizationId] AND l.[Kind] = 6 AND l.[Number] = x.[Category]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [CategoryId] = l.[Id] FROM [kniterp].[dev_category_templates] x
                JOIN [kniterp].[dev_lookups] l ON l.[OrganizationId] = x.[OrganizationId] AND l.[Kind] = 6 AND l.[Number] = x.[Category]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [CategoryId] = l.[Id] FROM [kniterp].[dev_category_measures] x
                JOIN [kniterp].[dev_lookups] l ON l.[OrganizationId] = x.[OrganizationId] AND l.[Kind] = 6 AND l.[Number] = x.[Category]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [KindId] = l.[Id] FROM [kniterp].[dev_patterns] x
                JOIN [kniterp].[dev_lookups] l ON l.[OrganizationId] = x.[OrganizationId] AND l.[Kind] = 7 AND l.[Number] = x.[Kind]')");

            migrationBuilder.AlterColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_models", type: "bigint", nullable: false, oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.AlterColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_category_templates", type: "bigint", nullable: false, oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.AlterColumn<long>(name: "CategoryId", schema: "kniterp", table: "dev_category_measures", type: "bigint", nullable: false, oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.AlterColumn<long>(name: "KindId", schema: "kniterp", table: "dev_patterns", type: "bigint", nullable: false, oldClrType: typeof(long), oldType: "bigint", oldNullable: true);

            migrationBuilder.DropPrimaryKey(name: "PK_dev_category_templates", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.DropIndex(name: "ux_dev_category_measures_name", schema: "kniterp", table: "dev_category_measures");
            migrationBuilder.DropColumn(name: "Kind", schema: "kniterp", table: "dev_patterns");
            migrationBuilder.DropColumn(name: "Category", schema: "kniterp", table: "dev_models");
            migrationBuilder.DropColumn(name: "Category", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.DropColumn(name: "Category", schema: "kniterp", table: "dev_category_measures");

            migrationBuilder.AddPrimaryKey(name: "PK_dev_category_templates", schema: "kniterp", table: "dev_category_templates", columns: new[] { "OrganizationId", "CategoryId" });
            migrationBuilder.CreateIndex(name: "IX_dev_patterns_OrganizationId_KindId", schema: "kniterp", table: "dev_patterns", columns: new[] { "OrganizationId", "KindId" });
            migrationBuilder.CreateIndex(name: "IX_dev_models_OrganizationId_CategoryId", schema: "kniterp", table: "dev_models", columns: new[] { "OrganizationId", "CategoryId" });
            migrationBuilder.CreateIndex(name: "ux_dev_category_measures_name", schema: "kniterp", table: "dev_category_measures",
                columns: new[] { "OrganizationId", "CategoryId", "Name" }, unique: true);
            migrationBuilder.AddForeignKey(name: "fk_dev_category_measures_category", schema: "kniterp", table: "dev_category_measures", columns: new[] { "OrganizationId", "CategoryId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_dev_category_templates_category", schema: "kniterp", table: "dev_category_templates", columns: new[] { "OrganizationId", "CategoryId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_dev_models_category", schema: "kniterp", table: "dev_models", columns: new[] { "OrganizationId", "CategoryId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_dev_patterns_kind", schema: "kniterp", table: "dev_patterns", columns: new[] { "OrganizationId", "KindId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Обратно к числам: свои (не встроенные) категории — «Прочее» (99), свои виды рисунка — первый вид (1); их шаблоны теряются.
            migrationBuilder.DropForeignKey(name: "fk_dev_category_measures_category", schema: "kniterp", table: "dev_category_measures");
            migrationBuilder.DropForeignKey(name: "fk_dev_category_templates_category", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.DropForeignKey(name: "fk_dev_models_category", schema: "kniterp", table: "dev_models");
            migrationBuilder.DropForeignKey(name: "fk_dev_patterns_kind", schema: "kniterp", table: "dev_patterns");
            migrationBuilder.DropIndex(name: "IX_dev_patterns_OrganizationId_KindId", schema: "kniterp", table: "dev_patterns");
            migrationBuilder.DropIndex(name: "IX_dev_models_OrganizationId_CategoryId", schema: "kniterp", table: "dev_models");
            migrationBuilder.DropPrimaryKey(name: "PK_dev_category_templates", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.DropIndex(name: "ux_dev_category_measures_name", schema: "kniterp", table: "dev_category_measures");
            migrationBuilder.AddColumn<byte>(name: "Kind", schema: "kniterp", table: "dev_patterns", type: "tinyint", nullable: true);
            migrationBuilder.AddColumn<byte>(name: "Category", schema: "kniterp", table: "dev_models", type: "tinyint", nullable: true);
            migrationBuilder.AddColumn<byte>(name: "Category", schema: "kniterp", table: "dev_category_templates", type: "tinyint", nullable: true);
            migrationBuilder.AddColumn<byte>(name: "Category", schema: "kniterp", table: "dev_category_measures", type: "tinyint", nullable: true);
            migrationBuilder.Sql(@"EXEC(N'DELETE x FROM [kniterp].[dev_category_templates] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[CategoryId] WHERE l.[Number] IS NULL')");
            migrationBuilder.Sql(@"EXEC(N'DELETE x FROM [kniterp].[dev_category_measures] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[CategoryId] WHERE l.[Number] IS NULL')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [Category] = COALESCE(l.[Number], 99) FROM [kniterp].[dev_models] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[CategoryId]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [Category] = COALESCE(l.[Number], 99) FROM [kniterp].[dev_category_templates] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[CategoryId]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [Category] = COALESCE(l.[Number], 99) FROM [kniterp].[dev_category_measures] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[CategoryId]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE x SET [Kind] = COALESCE(l.[Number], 1) FROM [kniterp].[dev_patterns] x JOIN [kniterp].[dev_lookups] l ON l.[Id] = x.[KindId]')");
            migrationBuilder.AlterColumn<byte>(name: "Kind", schema: "kniterp", table: "dev_patterns", type: "tinyint", nullable: false, oldClrType: typeof(byte), oldType: "tinyint", oldNullable: true);
            migrationBuilder.AlterColumn<byte>(name: "Category", schema: "kniterp", table: "dev_models", type: "tinyint", nullable: false, oldClrType: typeof(byte), oldType: "tinyint", oldNullable: true);
            migrationBuilder.AlterColumn<byte>(name: "Category", schema: "kniterp", table: "dev_category_templates", type: "tinyint", nullable: false, oldClrType: typeof(byte), oldType: "tinyint", oldNullable: true);
            migrationBuilder.AlterColumn<byte>(name: "Category", schema: "kniterp", table: "dev_category_measures", type: "tinyint", nullable: false, oldClrType: typeof(byte), oldType: "tinyint", oldNullable: true);
            migrationBuilder.DropColumn(name: "KindId", schema: "kniterp", table: "dev_patterns");
            migrationBuilder.DropColumn(name: "CategoryId", schema: "kniterp", table: "dev_models");
            migrationBuilder.DropColumn(name: "CategoryId", schema: "kniterp", table: "dev_category_templates");
            migrationBuilder.DropColumn(name: "CategoryId", schema: "kniterp", table: "dev_category_measures");
            migrationBuilder.Sql(@"EXEC(N'DELETE FROM [kniterp].[dev_lookups] WHERE [Kind] IN (6, 7)')");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups", sql: "[Kind] BETWEEN 1 AND 5");
            migrationBuilder.AddPrimaryKey(name: "PK_dev_category_templates", schema: "kniterp", table: "dev_category_templates", columns: new[] { "OrganizationId", "Category" });
            migrationBuilder.CreateIndex(name: "ux_dev_category_measures_name", schema: "kniterp", table: "dev_category_measures",
                columns: new[] { "OrganizationId", "Category", "Name" }, unique: true);
            migrationBuilder.AddCheckConstraint(name: "ck_dev_patterns_kind", schema: "kniterp", table: "dev_patterns", sql: "[Kind] BETWEEN 1 AND 6");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_models_category", schema: "kniterp", table: "dev_models", sql: "[Category] BETWEEN 1 AND 10 OR [Category] = 99");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_category_templates_category", schema: "kniterp", table: "dev_category_templates", sql: "[Category] BETWEEN 1 AND 10 OR [Category] = 99");
        }
    }
}
