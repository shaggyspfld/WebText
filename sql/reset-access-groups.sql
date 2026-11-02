/*
  EMERGENCY: restore admin access.

  If the admin group on the Settings page was changed to a group nobody can use, run this against the WebText
  database. It removes the saved group names so the app falls back to the defaults:
      users:  txtAppUser
      admins: txtAppAdmin
  Changes take effect within 30 seconds (or recycle the IIS app pool).
*/
DELETE FROM [AppSettings] WHERE [Name] IN (N'Access.UserGroup', N'Access.AdminGroup');

INSERT INTO [AppSettingChanges] ([Name], [NewValueDisplay], [ChangedBy], [ChangedAtUtc])
VALUES (N'Access.AdminGroup', N'(reset to default by SQL script)', SUSER_SNAME(), SYSUTCDATETIME()),
       (N'Access.UserGroup',  N'(reset to default by SQL script)', SUSER_SNAME(), SYSUTCDATETIME());
