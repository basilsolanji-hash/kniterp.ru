using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D734: типовые справочники «Разработки» — производитель и тип станка из справочника, класс машины с шагом 0,1, новые виды
    /// справочника (детали, пряжа, составы, отделка, размерные шкалы). Остальные типовые значения организация получает при первом
    /// обращении к разработке (DevLookupDefaults), без дублей.
    /// </summary>
    public partial class DevMachineLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D734: производитель станка — запись справочника «Производители оборудования» (вид 8), тип — «Типы оборудования» (вид 9);
            // класс машины — с шагом 0,1 (E1,5). Новые виды справочника 10–14 — подсказки деталей, пряжи, составов, отделки, шкал.
            migrationBuilder.DropCheckConstraint(name: "ck_dev_machines_brand", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_machines_gauge", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups", sql: "[Kind] BETWEEN 1 AND 14");
            migrationBuilder.AlterColumn<decimal>(name: "Gauge", schema: "kniterp", table: "dev_machines", type: "decimal(4,1)", precision: 4, scale: 1,
                nullable: false, oldClrType: typeof(int), oldType: "int");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_machines_gauge", schema: "kniterp", table: "dev_machines", sql: "[Gauge] BETWEEN 1 AND 30");
            migrationBuilder.AddColumn<long>(name: "BrandId", schema: "kniterp", table: "dev_machines", type: "bigint", nullable: true);
            migrationBuilder.AddColumn<long>(name: "TypeId", schema: "kniterp", table: "dev_machines", type: "bigint", nullable: true);

            // Производители — каждой организации (кроме уже заведённых кодов); станки — на запись с прежним кодом марки.
            migrationBuilder.Sql(@"EXEC(N'INSERT INTO [kniterp].[dev_lookups] ([OrganizationId], [Kind], [Name], [Number], [SortOrder], [IsArchived])
                SELECT o.[Id], 8, v.[Name], v.[Code], v.[Sort], 0
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES
                    (N''Stoll'', 1, 10),
                    (N''Shima Seiki'', 2, 20),
                    (N''Steiger'', 10, 30),
                    (N''Protti'', 11, 40),
                    (N''Santoni'', 12, 50),
                    (N''Mayer & Cie'', 13, 60),
                    (N''Terrot'', 14, 70),
                    (N''Pailung'', 15, 80),
                    (N''Lonati'', 16, 90),
                    (N''Cixing'', 17, 100),
                    (N''Longxing'', 18, 110),
                    (N''Jinlong'', 19, 120),
                    (N''Haina'', 20, 130),
                    (N''Ruifeng'', 21, 140),
                    (N''Fukuhara'', 22, 150),
                    (N''Orizio'', 23, 160),
                    (N''Brother'', 24, 170),
                    (N''Silver Reed'', 25, 180),
                    (N''Другой производитель'', 9, 190)) v ([Name], [Code], [Sort])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[dev_lookups] l WHERE l.[OrganizationId] = o.[Id] AND l.[Kind] = 8
                    AND (l.[Number] = v.[Code] OR l.[Name] = v.[Name]))')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE m SET [BrandId] = l.[Id] FROM [kniterp].[dev_machines] m
                JOIN [kniterp].[dev_lookups] l ON l.[OrganizationId] = m.[OrganizationId] AND l.[Kind] = 8 AND l.[Number] = m.[Brand]')");

            migrationBuilder.AlterColumn<long>(name: "BrandId", schema: "kniterp", table: "dev_machines", type: "bigint", nullable: false, oldClrType: typeof(long),
                oldType: "bigint", oldNullable: true);
            migrationBuilder.DropColumn(name: "Brand", schema: "kniterp", table: "dev_machines");
            migrationBuilder.CreateIndex(name: "IX_dev_machines_OrganizationId_BrandId", schema: "kniterp", table: "dev_machines", columns: new[] { "OrganizationId", "BrandId" });
            migrationBuilder.CreateIndex(name: "IX_dev_machines_OrganizationId_TypeId", schema: "kniterp", table: "dev_machines", columns: new[] { "OrganizationId", "TypeId" });
            migrationBuilder.AddForeignKey(name: "fk_dev_machines_brand", schema: "kniterp", table: "dev_machines", columns: new[] { "OrganizationId", "BrandId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_dev_machines_type", schema: "kniterp", table: "dev_machines", columns: new[] { "OrganizationId", "TypeId" },
                principalSchema: "kniterp", principalTable: "dev_lookups", principalColumns: new[] { "OrganizationId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Обратно: марка — Stoll (1), Shima Seiki (2) или «другая» (9); тип станка теряется; класс — целый; записи видов 8–14 удаляются.
            migrationBuilder.DropForeignKey(name: "fk_dev_machines_brand", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropForeignKey(name: "fk_dev_machines_type", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropIndex(name: "IX_dev_machines_OrganizationId_BrandId", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropIndex(name: "IX_dev_machines_OrganizationId_TypeId", schema: "kniterp", table: "dev_machines");
            migrationBuilder.AddColumn<byte>(name: "Brand", schema: "kniterp", table: "dev_machines", type: "tinyint", nullable: true);
            migrationBuilder.Sql(@"EXEC(N'UPDATE m SET [Brand] = CASE WHEN l.[Number] IN (1, 2) THEN l.[Number] ELSE 9 END FROM [kniterp].[dev_machines] m
                JOIN [kniterp].[dev_lookups] l ON l.[Id] = m.[BrandId]')");
            migrationBuilder.AlterColumn<byte>(name: "Brand", schema: "kniterp", table: "dev_machines", type: "tinyint", nullable: false, oldClrType: typeof(byte),
                oldType: "tinyint", oldNullable: true);
            migrationBuilder.DropColumn(name: "BrandId", schema: "kniterp", table: "dev_machines");
            migrationBuilder.DropColumn(name: "TypeId", schema: "kniterp", table: "dev_machines");
            migrationBuilder.Sql(@"EXEC(N'DELETE FROM [kniterp].[dev_lookups] WHERE [Kind] BETWEEN 8 AND 14')");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_machines_gauge", schema: "kniterp", table: "dev_machines");
            migrationBuilder.Sql(@"EXEC(N'UPDATE [kniterp].[dev_machines] SET [Gauge] = ROUND([Gauge], 0)')");
            migrationBuilder.AlterColumn<int>(name: "Gauge", schema: "kniterp", table: "dev_machines", type: "int", nullable: false, oldClrType: typeof(decimal),
                oldType: "decimal(4,1)", oldPrecision: 4, oldScale: 1);
            migrationBuilder.AddCheckConstraint(name: "ck_dev_machines_gauge", schema: "kniterp", table: "dev_machines", sql: "[Gauge] BETWEEN 1 AND 30");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_machines_brand", schema: "kniterp", table: "dev_machines", sql: "[Brand] IN (1, 2, 9)");
            migrationBuilder.DropCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups");
            migrationBuilder.AddCheckConstraint(name: "ck_dev_lookups_kind", schema: "kniterp", table: "dev_lookups", sql: "[Kind] BETWEEN 1 AND 7");
        }
    }
}
