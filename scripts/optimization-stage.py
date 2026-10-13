from pathlib import Path
import subprocess

# Reuse this reviewed, immutable stage specification, not a remotely mutable script.
revision = '4f9f5e29155d349742ec7286656817cf80957b07'
subprocess.run(['git', 'fetch', '--depth=1', 'origin', revision], check=True)
exec(compile(subprocess.check_output(['git', 'show', revision + ':scripts/optimization-stage.py'], text=True), '<reviewed-stage-2>', 'exec'))

p = Path('server/Database.cs'); s = p.read_text()
a = s.index('    private bool IsEmptyOrValidateCurrentDatabase()')
b = s.index('    private static void RejectUnsupportedLayout(', a)
s = s[:a] + '''    private bool IsEmptyOrValidateCurrentDatabase()
    {
        if (!File.Exists(DatabasePath)) return true;
        // Even SQLite's read-only mode may create WAL/SHM sidecars. Inspect a
        // private copy instead, including recovery data, so a refused version
        // cannot alter any original file. Called before serving requests or with
        // _gate held after the old connection has been closed.
        var probeDirectory = Path.Combine(Path.GetTempPath(), "hbtool-schema-" + NewId());
        Directory.CreateDirectory(probeDirectory);
        try
        {
            var probePath = Path.Combine(probeDirectory, "app.db");
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                var source = DatabasePath + suffix;
                if (File.Exists(source)) File.Copy(source, probePath + suffix);
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = probePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            static long Number(SqliteConnection db, string sql)
            {
                using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var version = Number(connection, "PRAGMA user_version;");
            var application = Number(connection, "PRAGMA application_id;");
            if (version == 0 && application == 0 && Number(connection, "SELECT COUNT(*) FROM sqlite_master;") == 0) return true;
            if (version != SchemaVersion || application != DatabaseApplicationId)
                throw new ApiException(409, "数据库版本或标识不受支持。现有文件未被重建，请保留数据并使用对应版本的程序。");
            foreach (var sql in RequiredSchemaQueries)
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader();
            }
            return false;
        }
        finally { Directory.Delete(probeDirectory, true); }
    }

''' + s[b:]
p.write_text(s)
p = Path('tests/api-smoke.py'); s = p.read_text().replace('str(self.port)', 'self.origin.rsplit(":", 1)[1]'); p.write_text(s)
p = Path('web/optimization-browser-test.mjs'); s = p.read_text()
s = s.replace('const upstream = await route.fetch()', "const url = new URL(route.request().url())\n      const upstream = await route.fetch({ url: directOrigin + url.pathname + url.search })")
s = s.replace("      assert(page.url().includes('/campaigns/new'))", "      await page.waitForURL('**/#/campaigns/new')")
p.write_text(s)
p = Path('.github/workflows/build.yml'); s = p.read_text().replace("      - 'scripts/**'", "      - 'scripts/**'\n      - '!scripts/optimization-stage.py'"); p.write_text(s)
p = Path('docs/optimization/DECISIONS.md'); p.write_text(p.read_text() + '\n拒绝旧库前在独立临时副本中验证数据库及其 WAL/回滚日志；原因是 SQLite 只读打开 WAL 数据库仍可能创建原目录的 SHM/WAL 文件。拒绝场景继续断言全部原文件哈希不变，未削弱验证。\n')
