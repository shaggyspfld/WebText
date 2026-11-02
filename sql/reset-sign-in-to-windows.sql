/*
  EMERGENCY: switch sign-in back to Windows / Active Directory.
  Use this if Local sign-in is on and nobody can sign in as an admin (e.g. every admin lost their phone).
  Members of the AD groups on the Settings page (default txtAppUser / txtAppAdmin) can then sign in with Windows.
  Takes effect within about 30 seconds (or recycle the app pool).

  Alternatively, keep Local sign-in and re-run the installer with -LocalAdminUser <name> (and -ResetLocalAdminAuthenticator)
  to reset that account's password, unlock it and put it back in the admin group.
*/
UPDATE [AppSettings]
SET [Value] = N'Windows', [UpdatedAtUtc] = SYSUTCDATETIME(), [UpdatedBy] = SUSER_SNAME()
WHERE [Name] = N'Auth.Mode';

INSERT INTO [SecurityEvents] ([AtUtc], [Event], [By], [Details])
VALUES (SYSUTCDATETIME(), N'SignInModeChanged', SUSER_SNAME(), N'Reset to Windows with sql/reset-sign-in-to-windows.sql');
