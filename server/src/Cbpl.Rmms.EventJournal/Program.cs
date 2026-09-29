using System.Globalization;
using System.Net;
using Cbpl.Rmms.EventJournal;
using Microsoft.Data.SqlClient;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
JournalOptions options = builder.Configuration
    .GetSection("Journal").Get<JournalOptions>()
    ?? throw new InvalidOperationException("Journal configuration is missing.");
HashSet<IPAddress> allowedClients = options.Validate();

builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<EventRepository>();

WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    IPAddress? remote = context.Connection.RemoteIpAddress;
    if (remote is null || !allowedClients.Contains(JournalOptions.Normalize(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

static bool TryGetBeforeId(HttpContext context, out long? beforeId)
{
    beforeId = null;
    if (!context.Request.Query.TryGetValue("beforeId", out var rawBeforeId))
        return true;

    if (rawBeforeId.Count != 1 ||
        !long.TryParse(rawBeforeId[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out long parsed) ||
        parsed <= 0)
    {
        return false;
    }

    beforeId = parsed;
    return true;
}

app.MapGet("/api/events", async (
    HttpContext context,
    EventRepository repository,
    ILogger<Program> logger) =>
{
    if (!TryGetBeforeId(context, out long? beforeId))
        return Results.BadRequest(new { error = "Invalid beforeId." });

    try
    {
        JournalPage page = await repository.ReadPageAsync(
            beforeId, context.RequestAborted);
        return Results.Ok(page);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception exception) when (
        exception is SqlException or InvalidOperationException or TimeoutException)
    {
        logger.LogError(exception, "Event journal SQL read failed");
        return Results.Json(
            new { error = "Журнал временно недоступен." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/roll-events/{rollNumber:int}", async (
    int rollNumber,
    HttpContext context,
    EventRepository repository,
    ILogger<Program> logger) =>
{
    if (rollNumber is < 1 or > 5)
        return Results.BadRequest(new { error = "Roll number must be 1 to 5." });
    if (!TryGetBeforeId(context, out long? beforeId))
        return Results.BadRequest(new { error = "Invalid beforeId." });

    try
    {
        JournalPage page = await repository.ReadRollPageAsync(
            rollNumber, beforeId, context.RequestAborted);
        return Results.Ok(page);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception exception) when (
        exception is SqlException or InvalidOperationException or TimeoutException)
    {
        logger.LogError(exception, "Roll history SQL read failed for roll {RollNumber}",
            rollNumber);
        return Results.Json(
            new { error = "История раската временно недоступна." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

await app.RunAsync();
