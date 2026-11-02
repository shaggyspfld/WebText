using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Services;

namespace WebText.Web.Auth;

public enum AccessLevel { User, Admin }

/// <summary>Requires membership of the AD user group (or admin group) or the admin group.</summary>
public class GroupRequirement : IAuthorizationRequirement
{
    public GroupRequirement(AccessLevel level) => Level = level;
    public AccessLevel Level { get; }
}

public static class Policies
{
    public const string User = "AppUser";
    public const string Admin = "AppAdmin";
    public const string SignedIn = "SignedIn";
}

/// <summary>
/// Checks group membership: local groups (Users page) for people signed in with Local sign-in, otherwise Windows groups.
/// Windows:  Group names come from the Settings page (defaults txtAppUser / txtAppAdmin)
/// and may be written as "txtAppUser" or "DOMAIN\txtAppUser". Results are cached for a few minutes per user,
/// so adding someone to a group in AD takes effect after they sign out/in and the cache expires.
/// </summary>
public class GroupMembership
{
    public const string DevAllGroupsClaim = "webtext:dev-all-groups";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly SettingsService _settings;
    private readonly ILogger<GroupMembership> _log;
    private readonly IServiceScopeFactory _scopes;

    public GroupMembership(IMemoryCache cache, SettingsService settings, ILogger<GroupMembership> log, IServiceScopeFactory scopes)
    {
        _cache = cache; _settings = settings; _log = log; _scopes = scopes;
    }

    /// <summary>"DOMAIN\txtAppAdmin" → "txtAppAdmin" (local groups have no domain).</summary>
    public static string LocalGroupName(string group)
    {
        var g = group.Trim();
        return g.Contains('\\') ? g[(g.LastIndexOf('\\') + 1)..] : g;
    }

    public bool IsAdmin(ClaimsPrincipal user) => IsMember(user, _settings.Current.AdminGroup);

    public bool IsUser(ClaimsPrincipal user) =>
        IsMember(user, _settings.Current.UserGroup) || IsAdmin(user);

    public bool IsMember(ClaimsPrincipal user, string? group)
    {
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(group)) return false;
        if (user.HasClaim(c => c.Type == DevAllGroupsClaim)) return true;

        if (user.LocalUserId() is { } localId)
        {
            var state = LocalSessionCache.Get(_cache, _scopes, localId);
            return state is { Active: true } && state.Groups.Contains(LocalGroupName(group));
        }

        var name = user.Identity.Name ?? "";
        var key = $"grp|{name}|{group}".ToLowerInvariant();
        return _cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return Check(user, group.Trim(), name);
        });
    }

    private bool Check(ClaimsPrincipal user, string group, string userName)
    {
        var candidates = new List<string> { group };
        // "txtAppUser" → also try "<user's domain>\txtAppUser".
        if (!group.Contains('\\') && userName.Contains('\\'))
            candidates.Add(userName[..userName.IndexOf('\\')] + "\\" + group);

        foreach (var g in candidates)
        {
            try
            {
                if (user.IsInRole(g)) return true;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                _log.LogDebug(ex, "Group lookup failed for {Group}", g);
            }

            // Fallback: compare the group's SID with the group SIDs in the Windows token.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var sid = new NTAccount(g).Translate(typeof(SecurityIdentifier)).Value;
                    if (user.HasClaim(c => c.Type == ClaimTypes.GroupSid && c.Value == sid)) return true;
                }
                catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
                {
                    _log.LogDebug(ex, "Could not resolve group {Group}", g);
                }
            }
        }
        return false;
    }
}

public class GroupRequirementHandler : AuthorizationHandler<GroupRequirement>
{
    private readonly GroupMembership _groups;
    public GroupRequirementHandler(GroupMembership groups) => _groups = groups;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, GroupRequirement requirement)
    {
        var ok = requirement.Level == AccessLevel.Admin ? _groups.IsAdmin(context.User) : _groups.IsUser(context.User);
        if (ok) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
