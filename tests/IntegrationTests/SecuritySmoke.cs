using ioBroker.Core.Security;
using ioBroker_NewGen.Controllers;

static class SecuritySmoke
{
    public static async Task RunAsync(MainRuntimeContext runtime, string tempRoot)
    {
        await runtime.InitializeSecurityAsync(new SecurityOptions
        {
            DatabasePath = Path.Combine(tempRoot, "security", "security.db")
        });
        var security = runtime.Security!;

        var admin = await security.Users.FindAsync("ADMIN");
        AssertEx.True(admin is { MustChangePassword: true, IsEnabled: true }, "Bootstrap-Admin muss mit Passwortwechsel-Pflicht existieren.");
        AssertEx.True((await security.Users.GetRolesAsync("admin")).Contains(SecurityRoles.Administrator), "Bootstrap-Admin muss Administrator sein.");

        var bad = await security.Authentication.LoginAsync("admin", "falsch");
        AssertEx.True(!bad.Success && security.Authentication.FailedLogins == 1, "Falsches Passwort muss scheitern und gezählt werden.");
        AssertEx.Equal(1L, Convert.ToInt64(runtime.Matrix.GetState(SecurityMatrixMirror.FailedLoginsState)?.Value), "failedLogins muss gespiegelt werden.");
        AssertEx.True(!(await security.Authentication.LoginAsync("niemand", "x")).Success, "Unbekannter Benutzer darf sich nicht anmelden.");

        var login = await security.Authentication.LoginAsync("admin", "admin", "127.0.0.1");
        AssertEx.True(login is { Success: true, MustChangePassword: true, Token: not null }, "Login mit Standardpasswort muss Passwortwechsel verlangen.");
        AssertEx.Equal(true, runtime.Matrix.GetState(SecurityMatrixMirror.UserState("admin", "online"))?.Value, "online muss gespiegelt werden.");
        AssertEx.Equal(1L, Convert.ToInt64(runtime.Matrix.GetState(SecurityMatrixMirror.ActiveSessionsState)?.Value), "activeSessions muss gespiegelt werden.");
        AssertEx.True(!login.Token!.Contains("admin"), "Token darf keine Nutzerdaten enthalten.");

        var session = await security.Authentication.ValidateSessionAsync(login.Token!);
        AssertEx.True(session is not null && session.UserId == admin!.Id, "Sitzung muss gültig sein.");
        AssertEx.True(await security.Authorization.HasPermissionAsync(admin.Id, SecurityPermissions.SystemShutdown), "Administrator braucht SystemShutdown.");
        AssertEx.True(await security.Authorization.HasPermissionAsync(admin.Id, SecurityPermissions.StateRead), "Administrator erbt Viewer-Rechte.");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => security.Users.SetPasswordAsync("admin", "kurz"));
        AssertEx.True(ex is not null, "Zu kurzes Passwort muss abgelehnt werden.");
        await security.Users.SetPasswordAsync("admin", "Sehr-Sicheres-Passwort-1");
        AssertEx.True(await security.Authentication.ValidateSessionAsync(login.Token!) is null, "Passwortwechsel muss Sitzungen beenden.");
        AssertEx.True(!(await security.Authentication.LoginAsync("admin", "admin")).Success, "Altes Passwort darf nicht mehr gelten.");

        var viewer = await security.Users.CreateAsync("vera", "Passwort-1234", "Vera", new[] { SecurityRoles.Viewer });
        AssertEx.True(await security.Authorization.HasPermissionAsync(viewer.Id, SecurityPermissions.StateRead), "Viewer darf lesen.");
        AssertEx.True(!await security.Authorization.HasPermissionAsync(viewer.Id, SecurityPermissions.StateWrite), "Viewer darf nicht schreiben.");
        await security.Roles.AssignAsync("vera", SecurityRoles.Operator);
        AssertEx.True(await security.Authorization.HasPermissionAsync(viewer.Id, SecurityPermissions.StateWrite), "Operator darf schreiben.");
        await security.Roles.RemoveAsync("vera", SecurityRoles.Operator);

        var veraLogin = await security.Authentication.LoginAsync("vera", "Passwort-1234");
        AssertEx.True(veraLogin.Success && !veraLogin.MustChangePassword, "Normaler Benutzer muss sich anmelden können.");
        await security.Authentication.LogoutAsync(veraLogin.Token!);
        AssertEx.True(await security.Authentication.ValidateSessionAsync(veraLogin.Token!) is null, "Logout muss die Sitzung beenden.");
        AssertEx.Equal(false, runtime.Matrix.GetState(SecurityMatrixMirror.UserState("vera", "online"))?.Value, "online muss nach Logout false sein.");

        await security.Users.SetEnabledAsync("vera", false);
        AssertEx.True(!(await security.Authentication.LoginAsync("vera", "Passwort-1234")).Success, "Deaktivierte Benutzer dürfen sich nicht anmelden.");
        AssertEx.True(await security.Users.DeleteAsync("vera"), "Benutzer muss löschbar sein.");
        AssertEx.True(await security.Users.FindAsync("vera") is null, "Gelöschter Benutzer darf nicht existieren.");
        AssertEx.True(await security.Audit.CountAsync("Login") >= 2, "Logins müssen im Audit-Log stehen.");

        await using var dbStream = new FileStream(security.Database.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dbReader = new StreamReader(dbStream, System.Text.Encoding.Latin1);
        var dbText = await dbReader.ReadToEndAsync();
        AssertEx.True(!dbText.Contains(login.Token!), "Das Klartext-Token darf nicht in der Datenbank stehen.");
    }
}

static class Assert
{
    public static async Task<T?> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            return ex;
        }

        return null;
    }
}
