using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace WebText.Web.Data;

/// <summary>
/// Installs or upgrades the SQL Server database, run by tools\Install-Server.ps1:
///     dotnet WebText.Web.dll --setup-database &lt;settings.json&gt;
/// The tables are created from the app's own data model, so the database always matches this version.
/// It runs with ADMINISTRATOR rights (the installer's account, a SQL login such as sa, or another Windows account),
/// then grants the website's own account only what it needs day to day: read and write.
///
/// settings.json: { "server", "database", "appLogin", "auth": "Integrated" | "Sql", "sqlUser",
///                  "signIn": "Windows" | "Local" (optional), "localAdmin": "name" (optional), "resetLocalAdminAuthenticator": bool }
/// The SQL login's password, if any, comes from the WEBTEXT_SETUP_SQLPASSWORD environment variable, never a file.
/// The local admin's password comes from WEBTEXT_SETUP_LOCALADMINPASSWORD. Re-running with "localAdmin" for an existing
/// account resets its password, unlocks it and puts it back in the admin group (recovery if every admin is locked out).
///
/// Exit codes: 0 done, 1 error, 2 couldn't sign in to SQL Server, 3 signed in but not allowed to do the work.
/// </summary>
public static class DatabaseSetup
{
    public const string Arg = "--setup-database";
    public const string PasswordEnv = "WEBTEXT_SETUP_SQLPASSWORD";
    public const string LocalAdminPasswordEnv = "WEBTEXT_SETUP_LOCALADMINPASSWORD";
    public const string HashPasswordArg = "--hash-password";

    public static int HashPasswordFromStdin()
    {
        var password = Console.In.ReadLine() ?? "";
        if (password.Length < 12) { Console.Error.WriteLine("Use at least 12 characters."); return 1; }
        Console.WriteLine(WebText.Web.Auth.LocalAccounts.HashPassword(new LocalUser(), password));
        return 0;
    }

    private sealed class Options
    {
        public string Server { get; set; } = "";
        public string Database { get; set; } = "WebText";
        public string? AppLogin { get; set; }
        public string Auth { get; set; } = "Integrated";
        public string? SqlUser { get; set; }
        public string? SignIn { get; set; }
        /// <summary>Shown in the app's top bar; set only if no name has been saved on the Settings page yet.</summary>
        public string? DisplayName { get; set; }
        public string? LocalAdmin { get; set; }
        public bool ResetLocalAdminAuthenticator { get; set; }
        public string? LocalAdminPasswordHash { get; set; }
    }

    private static readonly Regex GoSeparator = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task<int> RunAsync(string settingsPath)
    {
        Options o;
        try
        {
            o = JsonSerializer.Deserialize<Options>(await File.ReadAllTextAsync(settingsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("empty settings");
            if (string.IsNullOrWhiteSpace(o.Server) || string.IsNullOrWhiteSpace(o.Database))
                throw new InvalidDataException("server and database are required");
        }
        catch (Exception ex)
        {
            Fail($"Can't read the setup settings ({settingsPath}): {ex.Message}");
            return 1;
        }

        var useSql = o.Auth.Equals("Sql", StringComparison.OrdinalIgnoreCase);
        var master = new SqlConnectionStringBuilder
        {
            DataSource = o.Server,
            InitialCatalog = "master",
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "WebText setup",
            IntegratedSecurity = !useSql
        };
        if (useSql)
        {
            master.UserID = o.SqlUser ?? "";
            master.Password = Environment.GetEnvironmentVariable(PasswordEnv) ?? "";
        }
        var who = useSql ? $"SQL login '{o.SqlUser}'" : $"Windows account {Environment.UserDomainName}\\{Environment.UserName}";
        var inDb = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = o.Database };

        var stage = "connect";
        try
        {
            // ---------------------------------------------------------------- 1. sign in + permission check
            Say($"Connecting to {o.Server} as {who}...");
            bool dbExists, loginExists;
            await using (var c = new SqlConnection(master.ConnectionString))
            {
                await c.OpenAsync();
                stage = "check";
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    SELECT ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0),
                           HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'),
                           HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN'),
                           CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END,
                           CASE WHEN @login IS NULL OR EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @login) THEN 1 ELSE 0 END,
                           CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)),
                           CAST(SERVERPROPERTY('Edition') AS nvarchar(100))
                    """;
                cmd.Parameters.AddWithValue("@db", o.Database);
                cmd.Parameters.AddWithValue("@login", (object?)o.AppLogin ?? DBNull.Value);
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                var sysadmin = r.GetInt32(0) == 1;
                var canCreateDb = r.GetInt32(1) == 1;
                var canAlterLogin = r.GetInt32(2) == 1;
                dbExists = r.GetInt32(3) == 1;
                loginExists = r.GetInt32(4) == 1;
                Ok($"Connected: SQL Server {r.GetString(5)} ({r.GetString(6)}){(sysadmin ? ", as a sysadmin" : "")}");

                var missing = new List<string>();
                if (!dbExists && !canCreateDb) missing.Add($"create the database [{o.Database}] (needs dbcreator or sysadmin)");
                if (!loginExists && !canAlterLogin) missing.Add($"create the login [{o.AppLogin}] (needs securityadmin or sysadmin)");
                if (missing.Count > 0)
                {
                    Fail($"{who} is not allowed to " + string.Join(" or ", missing) + ".");
                    return 3;
                }
            }

            // ---------------------------------------------------------------- 2. database
            stage = "create-db";
            if (!dbExists)
            {
                await ExecAsync(master.ConnectionString,
                    "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC sys.sp_executesql @sql;",
                    ("@db", o.Database));
                Ok($"Created database [{o.Database}]");
            }
            else Ok($"Database [{o.Database}] already exists");

            // Is this account allowed to build tables and manage users inside it?
            stage = "open-db";
            await using (var c = new SqlConnection(inDb.ConnectionString))
            {
                await c.OpenAsync();
                stage = "check-db";
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    SELECT ISNULL(IS_MEMBER('db_owner'), 0),
                           HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'CREATE TABLE'),
                           HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'ALTER ANY USER'),
                           HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'ALTER ANY ROLE')
                    """;
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                if (r.GetInt32(0) != 1 && (r.GetInt32(1) != 1 || r.GetInt32(2) != 1 || r.GetInt32(3) != 1))
                {
                    Fail($"{who} can open [{o.Database}] but isn't db_owner there, so it can't create the tables and users.");
                    return 3;
                }
            }

            // ---------------------------------------------------------------- 3. tables (from the app's data model)
            stage = "schema";
            var efOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(inDb.ConnectionString).Options;
            await using (var db = new AppDbContext(efOptions))
            {
                var hasTables = await ScalarAsync(inDb.ConnectionString,
                    "SELECT CASE WHEN OBJECT_ID(N'dbo.Clients') IS NULL THEN 0 ELSE 1 END") == 1;
                if (!hasTables)
                {
                    await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
                    Ok("Created the tables");
                }
                else
                {
                    Ok("Tables already exist; checking for upgrades");
                }

                // Upgrade scripts for databases made by earlier versions. Each is safe to run repeatedly.
                var sqlDir = Path.Combine(AppContext.BaseDirectory, "sql");
                if (Directory.Exists(sqlDir))
                {
                    foreach (var file in Directory.GetFiles(sqlDir, "upgrade-*.sql").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var batch in GoSeparator.Split(await File.ReadAllTextAsync(file)).Where(b => !string.IsNullOrWhiteSpace(b)))
                            await ExecAsync(inDb.ConnectionString, batch);
                        Ok($"Applied {Path.GetFileName(file)}");
                    }
                }

                stage = "seed";
                if (!await db.MessageTemplates.AnyAsync())
                {
                    db.MessageTemplates.AddRange(SeedData.Templates());
                    await db.SaveChangesAsync();
                    Ok("Added the starter message templates");
                }

                stage = "sign-in";
                await ConfigureSignInAsync(db, o);

                // Mask SSN-like numbers in log rows saved before redaction was added.
                stage = "redact";
                var scrubbed = await WebText.Web.Logging.LogScrubber.ScrubAsync(db);
                Ok(scrubbed == 0 ? "Logs checked: no SSN-like numbers found" : $"Masked SSN-like numbers in {scrubbed} existing log entr{(scrubbed == 1 ? "y" : "ies")}");
            }

            // ---------------------------------------------------------------- 4. the website's own account
            stage = "grant";
            if (!string.IsNullOrWhiteSpace(o.AppLogin))
            {
                // EXEC() can't contain function calls, so each statement is built in @sql and run with sp_executesql.
                await ExecAsync(master.ConnectionString, """
                    DECLARE @sql nvarchar(max);
                    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @login)
                    BEGIN
                        SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' FROM WINDOWS';
                        EXEC sys.sp_executesql @sql;
                    END
                    """, ("@login", o.AppLogin));
                await ExecAsync(inDb.ConnectionString, """
                    DECLARE @sql nvarchar(max), @q nvarchar(260) = QUOTENAME(@login);
                    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @login)
                    BEGIN
                        SET @sql = N'CREATE USER ' + @q + N' FOR LOGIN ' + @q;
                        EXEC sys.sp_executesql @sql;
                    END
                    IF ISNULL(IS_ROLEMEMBER('db_datareader', @login), 0) = 0
                    BEGIN SET @sql = N'ALTER ROLE db_datareader ADD MEMBER ' + @q; EXEC sys.sp_executesql @sql; END
                    IF ISNULL(IS_ROLEMEMBER('db_datawriter', @login), 0) = 0
                    BEGIN SET @sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + @q; EXEC sys.sp_executesql @sql; END
                    -- The installer builds the tables now, so the website no longer needs to change the schema itself.
                    IF ISNULL(IS_ROLEMEMBER('db_ddladmin', @login), 0) = 1
                    BEGIN SET @sql = N'ALTER ROLE db_ddladmin DROP MEMBER ' + @q; EXEC sys.sp_executesql @sql; END
                    """, ("@login", o.AppLogin));
                Ok($"{o.AppLogin} can read and write the database (and nothing more)");
            }

            var tables = await ScalarAsync(inDb.ConnectionString, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0");
            Ok($"Database is ready ({tables} tables)");
            return 0;
        }
        catch (SqlException ex) when (stage == "connect" && ex.Number is 18456 or 18452 or 18486 or 18487 or 18488)
        {
            Fail($"SQL Server refused the sign-in for {who}: {ex.Message}");
            return 2;
        }
        catch (SqlException ex) when (stage == "connect")
        {
            Fail($"Couldn't reach SQL Server '{o.Server}': {ex.Message}");
            Say("Check the server name (and instance, e.g. SQL01\\SQLEXPRESS), that SQL Server allows remote connections, and that TCP 1433 is open.");
            return 1;
        }
        catch (SqlException ex) when (ex.Number is 229 or 230 or 262 or 300 or 916 or 15247 or 15151 or 4060 or 1031 or 2760)
        {
            Fail($"{who} doesn't have enough rights on SQL Server ({stage}): {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Fail($"Database setup failed during '{stage}': {ex.Message}");
            return 1;
        }
    }

    /// <summary>Local admin account and the sign-in method, for servers outside the domain (or admin recovery).</summary>
    private static async Task ConfigureSignInAsync(AppDbContext db, Options o)
    {
        var values = await db.AppSettings.AsNoTracking().Where(x => !x.IsSecret).ToDictionaryAsync(x => x.Name, x => x.Value);
        var snapshot = new WebText.Web.Services.AppSettingsSnapshot(values, Array.Empty<string>());
        await SeedData.EnsureLocalAccessGroupsAsync(db, snapshot, "installer");
        var adminGroupName = WebText.Web.Auth.GroupMembership.LocalGroupName(snapshot.AdminGroup);
        var userGroupName = WebText.Web.Auth.GroupMembership.LocalGroupName(snapshot.UserGroup);

        if (!string.IsNullOrWhiteSpace(o.LocalAdmin))
        {
            var name = WebText.Web.Auth.LocalAccounts.NormalizeUserName(o.LocalAdmin)
                ?? throw new InvalidDataException($"'{o.LocalAdmin}' isn't a valid user name (2-64 of letters, numbers, . _ - @).");
            var password = Environment.GetEnvironmentVariable(LocalAdminPasswordEnv);
            var hash = o.LocalAdminPasswordHash;
            var user = await db.LocalUsers.Include(u => u.Groups).FirstOrDefaultAsync(u => u.UserName == name);
            var created = user == null;
            if (user == null)
            {
                if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(hash))
                    throw new InvalidDataException("A password is needed for the new local admin account.");
                user = new LocalUser { UserName = name, DisplayName = "Administrator", CreatedBy = "installer", MustChangePassword = false };
                db.LocalUsers.Add(user);
            }
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(hash))
            {
                if (!string.IsNullOrEmpty(password) && password.Length < 12)
                    throw new InvalidDataException("The local admin password must be at least 12 characters.");
                user.PasswordHash = !string.IsNullOrEmpty(hash) ? hash : WebText.Web.Auth.LocalAccounts.HashPassword(user, password!);
                user.PasswordChangedAtUtc = DateTime.UtcNow;
                user.MustChangePassword = false;
                if (!created) notes.Add("password reset");
            }
            if (o.ResetLocalAdminAuthenticator && user.TotpSecretProtected != null)
            {
                user.TotpSecretProtected = null;
                user.TotpLastStep = 0;
                notes.Add("authenticator removed (set up again at next sign-in)");
            }
            if (!user.IsActive) notes.Add("re-enabled");
            user.IsActive = true;
            user.FailedAttempts = 0;
            user.LockedUntilUtc = null;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.UpdatedAtUtc = DateTime.UtcNow;
            user.UpdatedBy = "installer";
            foreach (var gname in new[] { adminGroupName, userGroupName })
            {
                var g = await db.LocalGroups.FirstAsync(x => x.Name == gname);
                if (user.Groups.All(m => m.GroupId != g.Id))
                {
                    user.Groups.Add(new LocalGroupMember { GroupId = g.Id, User = user });
                    notes.Add($"added to {gname}");
                }
            }
            await db.SaveChangesAsync();
            db.SecurityEvents.Add(new SecurityEvent
            {
                Event = created ? "UserCreated" : "UserChanged", UserName = name, By = "installer",
                IpAddress = Environment.MachineName,
                Details = created ? $"Local admin created by the installer\nGroups: {adminGroupName}, {userGroupName}" : "Installer: " + string.Join(", ", notes)
            });
            await db.SaveChangesAsync();
            Ok(created ? $"Created the local admin account '{name}'" : $"Updated the local admin account '{name}'" + (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : ""));
        }

        if (!string.IsNullOrWhiteSpace(o.DisplayName) &&
            !await db.AppSettings.AnyAsync(x => x.Name == WebText.Web.Services.SettingDefs.OfficeName))
        {
            db.AppSettings.Add(new AppSetting
            {
                Name = WebText.Web.Services.SettingDefs.OfficeName, Value = o.DisplayName.Trim(), UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "installer"
            });
            await db.SaveChangesAsync();
            Ok($"Name shown in the app: {o.DisplayName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(o.SignIn))
        {
            var mode = o.SignIn.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local"
                : o.SignIn.Equals("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : throw new InvalidDataException("signIn must be Windows or Local.");
            if (mode == "Local" && !await db.LocalGroupMembers.AnyAsync(m => m.Group.Name == adminGroupName && m.User.IsActive))
                throw new InvalidDataException($"Can't switch to Local sign-in: no local user is in {adminGroupName}. Give a local admin user name too.");
            var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Name == WebText.Web.Services.SettingDefs.AuthMode);
            var old = row?.Value ?? "Windows";
            if (!string.Equals(old, mode, StringComparison.OrdinalIgnoreCase))
            {
                if (row == null) db.AppSettings.Add(row = new AppSetting { Name = WebText.Web.Services.SettingDefs.AuthMode });
                row.Value = mode; row.IsSecret = false; row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = "installer";
                db.AppSettingChanges.Add(new AppSettingChange
                {
                    Name = WebText.Web.Services.SettingDefs.AuthMode, NewValueDisplay = mode, ChangedBy = "installer", ChangedAtUtc = DateTime.UtcNow
                });
                db.SecurityEvents.Add(new SecurityEvent { Event = "SignInModeChanged", By = "installer", IpAddress = Environment.MachineName, Details = $"{old} → {mode}" });
                await db.SaveChangesAsync();
            }
            Ok($"Sign-in method: {(mode == "Local" ? "Local (user name, password and authenticator code)" : "Windows (Active Directory)")}");
        }
    }

    private static async Task ExecAsync(string cs, string sql, params (string Name, object Value)[] p)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static void Say(string s) => Console.WriteLine("    " + s);
    private static void Ok(string s) => Console.WriteLine("    OK  " + s);
    private static void Fail(string s) => Console.WriteLine("    !!  " + s);
}
