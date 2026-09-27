using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using MinecraftManager.Server.Api.BackgroundServices;
using MinecraftManager.Server.Api.Hubs;
using MinecraftManager.Server.Api.Errors;
using MinecraftManager.Server.Api.Health;
using MinecraftManager.Server.Api.Observability;
using MinecraftManager.Server.Api.Security;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Application.Services;
using MinecraftManager.Server.Contracts.V1;
using MinecraftManager.Server.Domain.Common;
using MinecraftManager.Server.Domain.Entities;
using MinecraftManager.Server.Infrastructure.Imports;
using MinecraftManager.Server.Infrastructure.Persistence;
using MinecraftManager.Server.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(AppContext.BaseDirectory, "data-protection-keys"))).SetApplicationName("MinecraftManager.Server");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<ServerDbContext>().AddCheck<S3HealthCheck>("object-storage");
builder.Services.AddOptions<AuthenticationOptions>().BindConfiguration(AuthenticationOptions.SectionName).Validate(x => x.SigningKey.Length >= 32, "SigningKey must contain at least 32 characters.").ValidateOnStart();
builder.Services.AddOptions<ObjectStorageOptions>().BindConfiguration(ObjectStorageOptions.SectionName).Validate(x => !string.IsNullOrWhiteSpace(x.Bucket), "Object storage bucket is required.").Validate(x => (x.AccessKey is null) == (x.SecretKey is null), "Object storage access and secret keys must be supplied together.").ValidateOnStart();
builder.Services.AddDbContext<ServerDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddScoped<IServerDataStore>(sp => sp.GetRequiredService<ServerDbContext>());
builder.Services.AddIdentityCore<ServerUser>(o => { o.Lockout.MaxFailedAccessAttempts = 5; o.Password.RequiredLength = 12; }).AddRoles<ServerRole>().AddEntityFrameworkStores<ServerDbContext>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    var auth = builder.Configuration.GetSection(AuthenticationOptions.SectionName).Get<AuthenticationOptions>()!;
    o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = auth.Issuer, ValidateAudience = true, ValidAudience = auth.Audience, ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)), ClockSkew = TimeSpan.FromSeconds(30) };
    o.Events = new JwtBearerEvents { OnMessageReceived = context => { if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/client-events")) context.Token = context.Request.Query["access_token"]; return Task.CompletedTask; } };
});
builder.Services.AddAuthorization(o => { o.AddPolicy("Admin", p => p.RequireClaim("principal_type", "admin")); o.AddPolicy("Device", p => p.RequireClaim("principal_type", "device")); o.AddPolicy("Publisher", p => p.RequireRole("Owner", "Publisher")); o.AddPolicy("Deployer", p => p.RequireRole("Owner", "Deployer")); });
builder.Services.AddRateLimiter(o => o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddSingleton<ISystemClock, SystemClock>(); builder.Services.AddScoped<PackService>(); builder.Services.AddScoped<ClientRegistry>(); builder.Services.AddScoped<DeploymentService>(); builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddSingleton<ServerTelemetry>();
builder.Services.AddSingleton<IAmazonS3>(sp => { var config = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value; AWSCredentials? credentials = config.AccessKey is not null ? new BasicAWSCredentials(config.AccessKey, config.SecretKey) : null; var s3 = new AmazonS3Config { ForcePathStyle = config.ForcePathStyle, RegionEndpoint = RegionEndpoint.GetBySystemName(config.Region) }; if (!string.IsNullOrWhiteSpace(config.ServiceUrl)) s3.ServiceURL = config.ServiceUrl; return credentials is null ? new AmazonS3Client(s3) : new AmazonS3Client(credentials, s3); });
builder.Services.AddScoped<IFileStorage, S3FileStorage>();
builder.Services.AddHttpClient<ModrinthService>(c => { c.BaseAddress = new Uri("https://api.modrinth.com/v2/"); c.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftManager/1.0 (server)"); c.Timeout = TimeSpan.FromSeconds(30); });
builder.Services.AddHttpClient<CustomUrlImporter>(c => { c.Timeout = TimeSpan.FromMinutes(2); c.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftManager/1.0 (server import)"); }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10) });
builder.Services.AddSingleton<RemoteUrlPolicy>();
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 16 * 1024); builder.Services.AddSingleton<IClientNotifier, SignalRClientNotifier>();
if (builder.Configuration.GetValue("BackgroundJobs:Enabled", true))
{
    builder.Services.AddHostedService<OutboxDispatcher>();
    builder.Services.AddHostedService<MaintenanceWorker>();
}

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var telemetry = context.RequestServices.GetRequiredService<ServerTelemetry>();
    var started = System.Diagnostics.Stopwatch.GetTimestamp(); telemetry.Requests.Add(1);
    try { await next(); if (context.Response.StatusCode >= 500) telemetry.RequestFailures.Add(1); }
    catch { telemetry.RequestFailures.Add(1); throw; }
    finally { telemetry.RequestDurationMilliseconds.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
});
app.Use(async (context, next) => { context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.TraceIdentifier = context.Request.Headers.TryGetValue("X-Correlation-ID", out var value) && value.ToString().Length <= 128 ? value.ToString() : context.TraceIdentifier; await next(); });
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.FindFirstValue("principal_type") == "device" && Guid.TryParse(context.User.FindFirstValue("machine_id"), out var machineId))
    {
        var claimedVersion = context.User.FindFirstValue("authorization_version");
        var db = context.RequestServices.GetRequiredService<ServerDbContext>();
        var valid = await db.Set<Machine>().AsNoTracking().AnyAsync(x => x.Id == machineId && x.RevokedAtUtc == null && x.AuthorizationVersion.ToString() == claimedVersion, context.RequestAborted);
        if (!valid) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
    }
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/api/v1/admin") && context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        try
        {
            var db = context.RequestServices.GetRequiredService<ServerDbContext>();
            db.Add(new AuditEvent { ActorType = "administrator", ActorId = actorId, Action = $"{context.Request.Method} {context.Request.Path}", TargetType = "api_resource", CorrelationId = context.TraceIdentifier });
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception ex) { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Audit").LogError(ex, "Failed to append audit event for trace {TraceId}", context.TraceIdentifier); }
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous(); app.MapHealthChecks("/health/ready");
app.MapGet("/health/startup", () => Results.Ok(new { status = "started" })).AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapPost("/admin/bootstrap", async (BootstrapRequest request, UserManager<ServerUser> users, RoleManager<ServerRole> roles, ITokenService tokens, CancellationToken ct) =>
{
    if (await users.Users.AnyAsync(ct)) return Results.Conflict(Problem("bootstrap_complete", "Server bootstrap has already completed.", 409));
    var configuredBootstrapToken = builder.Configuration["Bootstrap:Token"];
    if (string.IsNullOrWhiteSpace(configuredBootstrapToken) || configuredBootstrapToken.Length < 32 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configuredBootstrapToken), Encoding.UTF8.GetBytes(request.BootstrapToken))) return Results.Unauthorized();
    foreach (var role in new[] { "Owner", "PackEditor", "Publisher", "Deployer", "MachineManager", "Auditor" }) if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new ServerRole { Name = role });
    var user = new ServerUser { Id = Guid.NewGuid(), UserName = request.UserName, Email = request.Email, EmailConfirmed = true };
    var created = await users.CreateAsync(user, request.Password); if (!created.Succeeded) return Results.ValidationProblem(created.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
    await users.AddToRoleAsync(user, "Owner"); var token = tokens.CreateAdminAccessToken(user.Id, user.UserName!, ["Owner"]); return Results.Ok(new { accessToken = token.Token, token.ExpiresAtUtc });
}).AllowAnonymous().RequireRateLimiting("auth");
api.MapPost("/admin/session", async (LoginRequest request, UserManager<ServerUser> users, ITokenService tokens) => { var user = await users.FindByNameAsync(request.UserName); if (user is null || !user.Enabled || await users.IsLockedOutAsync(user)) return Results.Unauthorized(); if (!await users.CheckPasswordAsync(user, request.Password)) { await users.AccessFailedAsync(user); return Results.Unauthorized(); } await users.ResetAccessFailedCountAsync(user); var roles = await users.GetRolesAsync(user); var token = tokens.CreateAdminAccessToken(user.Id, user.UserName!, roles); return Results.Ok(new { accessToken = token.Token, token.ExpiresAtUtc }); }).AllowAnonymous().RequireRateLimiting("auth");
api.MapPost("/admin/registration-codes", async (ClientRegistry registry, ClaimsPrincipal principal, CancellationToken ct) => { var owner = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!); var created = await registry.CreateCodeAsync(owner, TimeSpan.FromMinutes(10), ct); return Results.Ok(new { registrationCode = created.Code, expiresAtUtc = created.Entity.ExpiresAtUtc }); }).RequireAuthorization("Admin");
api.MapPost("/client-registrations/complete", async (CompleteRegistrationRequest request, ClientRegistry registry, ITokenService tokens, CancellationToken ct) => { var result = await registry.RegisterAsync(request.RegistrationCode, request.DisplayName, ct); var access = tokens.CreateDeviceAccessToken(result.Machine); return Results.Created($"/api/v1/client/machines/{result.Machine.Id}", new RegistrationResponse(result.Machine.Id, access.Token, access.ExpiresAtUtc, result.RefreshCredential)); }).AllowAnonymous().RequireRateLimiting("auth");
api.MapPost("/client-sessions/token", async (RefreshTokenRequest request, ClientRegistry registry, ITokenService tokens, CancellationToken ct) => { var rotated = await registry.RotateAsync(request.RefreshCredential, ct); var access = tokens.CreateDeviceAccessToken(rotated.Machine); return Results.Ok(new RegistrationResponse(rotated.Machine.Id, access.Token, access.ExpiresAtUtc, rotated.RefreshCredential)); }).AllowAnonymous().RequireRateLimiting("auth");
api.MapPut("/client/instances/{clientInstanceId:guid}", async (Guid clientInstanceId, UpsertInstanceRequest request, ClientRegistry registry, ClaimsPrincipal principal, CancellationToken ct) => { var machine = Guid.Parse(principal.FindFirstValue("machine_id")!); var instance = await registry.UpsertInstanceAsync(machine, clientInstanceId, request, ct); return Results.Ok(new InstanceResponse(instance.Id, machine, clientInstanceId, instance.DisplayName, instance.InstalledPackVersion)); }).RequireAuthorization("Device");
api.MapPost("/admin/packs", async (CreatePackRequest request, PackService service, CancellationToken ct) => Results.Created("", await service.CreatePackAsync(request.Slug, request.DisplayName, ct))).RequireAuthorization("Admin");
api.MapGet("/admin/packs", async (ServerDbContext db, CancellationToken ct) => Results.Ok(await db.Set<Pack>().AsNoTracking().OrderBy(x => x.DisplayName).Take(200).ToListAsync(ct))).RequireAuthorization("Admin");
api.MapPost("/admin/packs/{packId:guid}/versions", async (Guid packId, CreateVersionRequest request, PackService service, CancellationToken ct) => Results.Created("", await service.CreateDraftAsync(packId, request.Version, request.MinimumClientVersion, request.MinecraftVersion, request.LoaderType, request.LoaderVersion, ct))).RequireAuthorization("Admin");
api.MapGet("/admin/packs/{packId:guid}/versions", async (Guid packId, ServerDbContext db, CancellationToken ct) => Results.Ok(await db.Set<PackVersion>().AsNoTracking().Where(x => x.PackId == packId).OrderByDescending(x => x.PublishedAtUtc).Take(200).Select(x => new { x.Id, x.Version, x.State, x.ManifestSha256, x.PublishedAtUtc }).ToListAsync(ct))).RequireAuthorization("Admin");
api.MapPut("/admin/pack-versions/{id:guid}/managed-paths", async (Guid id, IReadOnlyList<string> request, PackService service, CancellationToken ct) => { await service.SetManagedPathsAsync(id, request, ct); return Results.NoContent(); }).RequireAuthorization("Admin");
api.MapPost("/admin/pack-versions/{id:guid}/files", async (Guid id, AddPackFileRequest request, PackService service, CancellationToken ct) => { await service.AddFileAsync(id, request.Path, request.BlobId, request.ContentType, ct); return Results.NoContent(); }).RequireAuthorization("Admin");
api.MapGet("/admin/pack-versions/{id:guid}/manifest-preview", async (Guid id, PackService service, CancellationToken ct) => { var result = await service.PreviewAsync(id, ct); return Results.Bytes(result.Manifest, "application/json", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"sha256-{result.Digest}\"")); }).RequireAuthorization("Admin");
api.MapPost("/admin/pack-versions/{id:guid}/publish", async (Guid id, PackService service, CancellationToken ct) => Results.Ok(await service.PublishAsync(id, ct))).RequireAuthorization("Publisher");
api.MapPost("/admin/files", async (HttpRequest request, IFileStorage storage, CancellationToken ct) => { var expected = request.Headers["X-Content-SHA256"].FirstOrDefault(); long? size = request.ContentLength; var blob = await storage.StoreVerifiedAsync(request.Body, 2L * 1024 * 1024 * 1024, expected, size, ct); return Results.Created($"/api/v1/admin/files/{blob.BlobId}", blob); }).DisableAntiforgery().RequireAuthorization("Admin");
api.MapPost("/admin/assignments", async (CreateAssignmentRequest request, DeploymentService service, ClaimsPrincipal principal, CancellationToken ct) => Results.Ok(await service.AssignAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), request.InstanceId, request.PackVersionId, ct))).RequireAuthorization("Deployer");
api.MapPost("/admin/deployments", async (CreateDeploymentRequest request, DeploymentService service, ClaimsPrincipal principal, CancellationToken ct) => Results.Created("", await service.CreateAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), request.PackVersionId, request.TargetInstanceIds, request.Note, ct))).RequireAuthorization("Deployer");
api.MapPost("/admin/deployments/{id:guid}/cancel", async (Guid id, DeploymentService service, CancellationToken ct) => { await service.CancelAsync(id, ct); return Results.NoContent(); }).RequireAuthorization("Deployer");
api.MapPost("/admin/machines/{id:guid}/revoke", async (Guid id, ServerDbContext db, CancellationToken ct) => { var machine = await db.Set<Machine>().SingleOrDefaultAsync(x => x.Id == id, ct); if (machine is null) return Results.NotFound(); machine.RevokedAtUtc ??= DateTimeOffset.UtcNow; machine.AuthorizationVersion++; foreach (var credential in await db.Set<DeviceCredential>().Where(x => x.MachineId == id && x.RevokedAtUtc == null).ToListAsync(ct)) credential.RevokedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }).RequireAuthorization("Admin");
api.MapPost("/client/deployments/{id:guid}/status", async (Guid id, DeploymentStatusRequest request, DeploymentService service, ClaimsPrincipal principal, CancellationToken ct) => Results.Ok(await service.ReportAsync(Guid.Parse(principal.FindFirstValue("machine_id")!), id, request, ct))).RequireAuthorization("Device");
api.MapGet("/client/assignments", async (ServerDbContext db, ClaimsPrincipal principal, CancellationToken ct) => { var machine = Guid.Parse(principal.FindFirstValue("machine_id")!); return Results.Ok(await (from assignment in db.Set<PackAssignment>() join instance in db.Set<MinecraftInstance>() on assignment.InstanceId equals instance.Id join version in db.Set<PackVersion>() on assignment.PackVersionId equals version.Id where instance.MachineId == machine && assignment.IsActive select new AssignmentResponse(instance.Id, assignment.PackId, version.Id, version.Version, assignment.Revision)).ToListAsync(ct)); }).RequireAuthorization("Device");
api.MapPost("/client/heartbeat", async (HeartbeatRequest request, ServerDbContext db, ClaimsPrincipal principal, CancellationToken ct) => { var machineId = Guid.Parse(principal.FindFirstValue("machine_id")!); var machine = await db.Set<Machine>().SingleOrDefaultAsync(x => x.Id == machineId && x.RevokedAtUtc == null, ct); if (machine is null) return Results.Unauthorized(); machine.ClientVersion = request.ClientVersion.Length <= 64 ? request.ClientVersion : throw new DomainRuleException("invalid_value", "Client version is invalid."); machine.LastSeenAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return Results.NoContent(); }).RequireAuthorization("Device");
api.MapGet("/client/deployments", async (ServerDbContext db, ClaimsPrincipal principal, CancellationToken ct) => { var machine = Guid.Parse(principal.FindFirstValue("machine_id")!); var rows = await (from target in db.Set<DeploymentTarget>() join instance in db.Set<MinecraftInstance>() on target.InstanceId equals instance.Id join deployment in db.Set<Deployment>() on target.DeploymentId equals deployment.Id where instance.MachineId == machine orderby deployment.CreatedAtUtc descending select new { deployment.Id, target.InstanceId, target.Status, target.LastSequence, deployment.PackVersionId, deployment.CreatedAtUtc, deployment.CancelledAtUtc }).Take(100).ToListAsync(ct); return Results.Ok(rows); }).RequireAuthorization("Device");
api.MapGet("/packs/{packId:guid}/versions/{version}/manifest", async (Guid packId, string version, ServerDbContext db, ClaimsPrincipal principal, CancellationToken ct) => { var machine = Guid.Parse(principal.FindFirstValue("machine_id")!); var item = await (from assignment in db.Set<PackAssignment>() join instance in db.Set<MinecraftInstance>() on assignment.InstanceId equals instance.Id join pv in db.Set<PackVersion>() on assignment.PackVersionId equals pv.Id where instance.MachineId == machine && assignment.IsActive && assignment.PackId == packId && pv.NormalizedVersion == version.ToLower() select pv).SingleOrDefaultAsync(ct); return item?.ManifestJson is null ? Results.NotFound() : Results.Bytes(item.ManifestJson, "application/json", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"sha256-{item.ManifestSha256}\"")); }).RequireAuthorization("Device");
api.MapPost("/files/{fileId:guid}/download-ticket", async (Guid fileId, ServerDbContext db, IFileStorage storage, ClaimsPrincipal principal, CancellationToken ct) => { var machine = Guid.Parse(principal.FindFirstValue("machine_id")!); var blob = await (from file in db.Set<PackFile>() join assignment in db.Set<PackAssignment>() on file.PackVersionId equals assignment.PackVersionId join instance in db.Set<MinecraftInstance>() on assignment.InstanceId equals instance.Id join b in db.Set<FileBlob>() on file.BlobId equals b.Id where file.Id == fileId && instance.MachineId == machine && assignment.IsActive && b.State == BlobState.Verified select b).FirstOrDefaultAsync(ct); if (blob is null) return Results.NotFound(); var ticket = await storage.CreateDownloadTicketAsync(blob.StorageKey, TimeSpan.FromMinutes(5), ct); return Results.Ok(new DownloadTicketResponse(ticket.Url, ticket.ExpiresAtUtc, blob.Size, blob.Sha256)); }).RequireAuthorization("Device");
api.MapGet("/admin/modrinth/search", async (string query, ModrinthService service, CancellationToken ct) => Results.Ok(await service.SearchAsync(query, ct))).RequireAuthorization("Admin");
api.MapPost("/admin/imports/modrinth", async (RemoteImportRequest request, ModrinthService service, CancellationToken ct) => { if (!request.RedistributionAllowed) return Results.UnprocessableEntity(Problem("license_not_approved", "Redistribution must be reviewed before import.", 422)); if (request.MaximumBytes <= 0) return Results.BadRequest(Problem("invalid_size", "MaximumBytes must be positive.", 400)); return Results.Ok(await service.ImportAsync(request.Url, request.ExpectedSha256, Math.Min(request.MaximumBytes, 2L * 1024 * 1024 * 1024), ct)); }).RequireAuthorization("Admin");
api.MapPost("/admin/imports/url", async (RemoteImportRequest request, CustomUrlImporter service, CancellationToken ct) => { if (!request.RedistributionAllowed) return Results.UnprocessableEntity(Problem("license_not_approved", "Redistribution must be reviewed before import.", 422)); if (request.MaximumBytes <= 0) return Results.BadRequest(Problem("invalid_size", "MaximumBytes must be positive.", 400)); return Results.Ok(await service.ImportAsync(request.Url, request.ExpectedSha256, Math.Min(request.MaximumBytes, 2L * 1024 * 1024 * 1024), ct)); }).RequireAuthorization("Admin");
api.MapGet("/admin/audit-events", async (ServerDbContext db, DateTimeOffset? before, CancellationToken ct) => Results.Ok(await db.Set<AuditEvent>().AsNoTracking().Where(x => before == null || x.OccurredAtUtc < before).OrderByDescending(x => x.OccurredAtUtc).Take(100).ToListAsync(ct))).RequireAuthorization("Admin");
app.MapHub<ClientEventsHub>("/hubs/client-events");
app.Run();

static ProblemV1 Problem(string code, string title, int status) => new(code, title, status, string.Empty);
public sealed record BootstrapRequest(string BootstrapToken, string UserName, string Email, string Password);
public sealed record LoginRequest(string UserName, string Password);
public sealed record CreatePackRequest(string Slug, string DisplayName);
public sealed record CreateVersionRequest(string Version, string? MinimumClientVersion, string? MinecraftVersion = null, string? LoaderType = null, string? LoaderVersion = null);
public sealed record AddPackFileRequest(string Path, Guid BlobId, string? ContentType);
public sealed record CreateAssignmentRequest(Guid InstanceId, Guid PackVersionId);
public sealed record RemoteImportRequest(Uri Url, string? ExpectedSha256, long MaximumBytes, bool RedistributionAllowed);
public partial class Program;
