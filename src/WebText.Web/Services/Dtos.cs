using WebText.Web.Data;

namespace WebText.Web.Services;

public record MediaDto(string Url, string? ContentType);

public record MessageDto(
    long Id,
    int ClientId,
    string Direction,
    string Body,
    string Status,
    string? Error,
    string? SentBy,
    DateTime CreatedAtUtc,
    DateTime? SentAtUtc,
    DateTime? StatusUpdatedAtUtc,
    bool IsRead,
    string? ReadBy,
    int Segments,
    IReadOnlyList<MediaDto> Media)
{
    public static MessageDto From(Message m) => new(
        m.Id,
        m.ClientId,
        m.Direction.ToString().ToLowerInvariant(),
        m.Body,
        m.Status,
        m.ErrorDetail,
        m.SentBy,
        DateTime.SpecifyKind(m.CreatedAtUtc, DateTimeKind.Utc),
        m.SentAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
        m.StatusUpdatedAtUtc is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : null,
        m.IsRead,
        m.ReadBy,
        m.Segments,
        m.Media.Select(x => new MediaDto(x.Url, x.ContentType)).ToList());
}

public record ConversationDto(
    int ClientId,
    string Name,
    string Phone,
    string PhoneFormatted,
    string? AccountNumber,
    bool OptedOut,
    bool IsAutoCreated,
    DateTime? LastMessageAtUtc,
    string? LastMessagePreview,
    string? LastDirection,
    int Unread);

public record ClientDto(
    int Id,
    string FirstName,
    string LastName,
    string Name,
    string Phone,
    string PhoneFormatted,
    string? Email,
    string? AccountNumber,
    string? Notes,
    bool OptedOut,
    bool IsAutoCreated,
    int OtherClientsOnNumber)
{
    public static ClientDto From(Client c, int sharedCount) => new(
        c.Id, c.FirstName, c.LastName, c.DisplayName, c.PhoneE164, PhoneNumber.Format(c.PhoneE164),
        c.Email, c.AccountNumber, c.Notes, c.OptedOut, c.IsAutoCreated, sharedCount);
}
