using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PKVault.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteTransfers",
                columns: table => new
                {
                    Id = table.Column<string>(name: "Id", type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(name: "DeviceId", type: "TEXT", nullable: false),
                    SaveId = table.Column<uint>(name: "SaveId", type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(name: "FileName", type: "TEXT", nullable: false),
                    OriginalHash = table.Column<string>(name: "OriginalHash", type: "TEXT", nullable: false),
                    NewHash = table.Column<string>(name: "NewHash", type: "TEXT", nullable: false),
                    NewBytes = table.Column<byte[]>(name: "NewBytes", type: "BLOB", nullable: false),
                    BackupCreatedAt = table.Column<DateTime>(name: "BackupCreatedAt", type: "TEXT", nullable: false),
                    CommitSeq = table.Column<long>(name: "CommitSeq", type: "INTEGER", nullable: false),
                    State = table.Column<int>(name: "State", type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(name: "CreatedAt", type: "TEXT", nullable: false),
                    AckedAt = table.Column<DateTime>(name: "AckedAt", type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteTransfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTransfers_SaveId_State",
                table: "RemoteTransfers",
                columns: new[] { "SaveId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteTransfers");
        }
    }
}
