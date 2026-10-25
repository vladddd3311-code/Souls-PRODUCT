using System.Data;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var pathAliases = new Dictionary<string, PathString>(StringComparer.OrdinalIgnoreCase)
{
    ["/config"] = "/public/config",
    ["/api/config"] = "/public/config",
    ["/api/public/config"] = "/public/config",
    ["/time"] = "/public/time",
    ["/api/time"] = "/public/time",
    ["/api/public/time"] = "/public/time",
    ["/api/auth/challenge"] = "/auth/challenge",
    ["/api/auth/register"] = "/auth/register",
    ["/api/auth/login"] = "/auth/login",
    ["/api/auth/forgot"] = "/auth/forgot",
    ["/api/auth/reset"] = "/auth/reset",
    ["/device/start"] = "/web/device/start",
    ["/device/poll"] = "/web/device/poll",
    ["/device/authorize"] = "/web/device/authorize",
    ["/api/web/device/start"] = "/web/device/start",
    ["/api/web/device/poll"] = "/web/device/poll",
    ["/api/web/device/authorize"] = "/web/device/authorize",
    ["/api/device/start"] = "/web/device/start",
    ["/api/device/poll"] = "/web/device/poll",
    ["/api/device/authorize"] = "/web/device/authorize",
    ["/api/subscription/activate"] = "/subscription/activate",
    ["/api/device/bind"] = "/device/bind",
    ["/api/device/reset-hwid-key"] = "/device/reset-hwid-key",
    ["/api/profile/name"] = "/profile/name",
    ["/launch-token"] = "/launcher/launch-token",
    ["/launcher/token"] = "/launcher/launch-token",
    ["/launcher/launch"] = "/launcher/launch-token",
    ["/api/launch-token"] = "/launcher/launch-token",
    ["/api/launcher/token"] = "/launcher/launch-token",
    ["/api/launcher/launch"] = "/launcher/launch-token",
    ["/api/launcher/launch-token"] = "/launcher/launch-token",
    ["/api/mod/verify-token"] = "/mod/verify-token",
    ["/api/mod/heartbeat"] = "/mod/heartbeat",
};
var databaseUrl = app.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("DATABASE_URL is required");
var connectionString = NormalizePostgresUrl(databaseUrl);

await using (var db = new NpgsqlConnection(connectionString))
{
    await db.OpenAsync();
    await db.ExecuteAsync("""
        create table if not exists users (
          email text primary key,
          name text not null,
          password_hash text not null,
          password_salt text not null,
          role text not null default 'free',
          premium_until timestamptz null,
          hwid_hash text not null default '',
          banned boolean not null default false,
          created_at timestamptz not null default now()
        );
        create table if not exists subscription_keys (
          key text primary key,
          days integer not null check (days between 1 and 3650),
          used_by text null references users(email),
          used_at timestamptz null,
          created_at timestamptz not null default now()
        );
        create table if not exists reset_codes (
          email text primary key references users(email) on delete cascade,
          code_hash text not null,
          expires_at timestamptz not null,
          used_at timestamptz null
        );
        create table if not exists launch_tokens (
          token_hash text primary key,
          email text not null references users(email) on delete cascade,
          hwid_hash text not null,
          launcher_version text not null,
          mod_version text not null,
          expires_at timestamptz not null,
          used_at timestamptz null,
          last_seen_at timestamptz null,
          session_expires_at timestamptz null,
          created_at timestamptz not null default now()
        );
        create index if not exists launch_tokens_expires_idx on launch_tokens(expires_at);
        create table if not exists hwid_reset_keys (
          key text primary key,
          used_by text null references users(email),
          used_at timestamptz null,
          created_at timestamptz not null default now()
        );
        create table if not exists auth_sessions (
          token_hash text primary key,
          email text not null references users(email) on delete cascade,
          expires_at timestamptz not null,
          revoked_at timestamptz null,
          created_at timestamptz not null default now()
        );
        create index if not exists auth_sessions_email_idx on auth_sessions(email);
        create table if not exists launcher_login_requests (
          code text primary key,
          poll_token_hash text not null unique,
          email text null references users(email) on delete cascade,
          result_token text null,
          expires_at timestamptz not null,
          consumed_at timestamptz null,
          created_at timestamptz not null default now()
        );
        create index if not exists launcher_login_expires_idx on launcher_login_requests(expires_at);
        create table if not exists registration_challenges (
          id text primary key,
          answer_hash text not null,
          expires_at timestamptz not null,
          created_at timestamptz not null default now()
        );
        create index if not exists registration_challenges_expires_idx on registration_challenges(expires_at);
        create table if not exists prime_keys (
          key text primary key,
          buyer text not null default '',
          notes text not null default '',
          active boolean not null default true,
          created_at timestamptz not null default now(),
          activated_at timestamptz null,
          last_seen_at timestamptz null,
          hwid_hash text not null default ''
        );
        """);
    await db.ExecuteAsync("""
        alter table launch_tokens add column if not exists last_seen_at timestamptz null;
        alter table launch_tokens add column if not exists session_expires_at timestamptz null;
        create index if not exists launch_tokens_session_idx on launch_tokens(session_expires_at);
        """);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var originalPath = context.Request.Path;
    await next();
    app.Logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, originalPath, context.Response.StatusCode);
});

app.MapGet("/health", async () =>
{
    await using var db = await OpenDb();
    return Results.Ok(new { status = await db.ExecuteScalarAsync<int>("select 1") == 1 ? "ok" : "error", service = "SoulsDLC API" });
});

app.MapGet("/public/config", () => Results.Ok(new
{
    microsoftClientId = app.Configuration["MICROSOFT_CLIENT_ID"] ?? "",
    minecraftVersion = "1.21.4",
    modVersion = "2.4.2-protected",
    launcherVersion = "V16"
}));

app.MapGet("/downloads/SoulsDLC-Launcher-WINDOWS-V15.exe", () =>
    Results.File(
        Path.Combine(app.Environment.WebRootPath, "downloads", "SoulsDLC-Launcher-WINDOWS-V15.exe"),
        "application/octet-stream",
        "SoulsDLC-Launcher-WINDOWS-V15.exe"));

app.MapGet("/downloads/SoulsDLC-Launcher-HOSTED.exe", () =>
    Results.File(
        Path.Combine(app.Environment.WebRootPath, "downloads", "SoulsDLC-Launcher-HOSTED.exe"),
        "application/octet-stream",
        "SoulsDLC-Launcher-HOSTED.exe"));
app.MapGet("/downloads/SoulsDLC-Launcher.exe", () =>
    Results.Redirect("/downloads/SoulsDLC-Launcher-HOSTED.exe", permanent: false));

app.MapGet("/auth/challenge", async () =>
{
    var left = RandomNumberGenerator.GetInt32(2, 20);
    var right = RandomNumberGenerator.GetInt32(2, 20);
    var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    await using var db = await OpenDb();
    await db.ExecuteAsync("delete from registration_challenges where expires_at<=now()");
    await db.ExecuteAsync("""
        insert into registration_challenges(id,answer_hash,expires_at)
        values (@Id,@Answer,now()+interval '5 minutes')
        """, new { Id = id, Answer = Sha256((left + right).ToString()) });
    return Results.Ok(new { challengeId = id, question = $"{left} + {right} = ?" });
});

app.MapPost("/auth/register", async (Credentials body) =>
{
    var email = NormalizeEmail(body.Email);
    var name = body.Name.Trim();
    var normalizedName = NormalizeName(name);
    if (!ValidEmail(email) || body.Password.Length < 8 || name.Length is < 2 or > 24)
        return Results.BadRequest(new { error = "invalid_credentials" });
    if (body.HwidHash.Length != 64)
    {
        await using var challengeDb = await OpenDb();
        var consumed = await challengeDb.ExecuteScalarAsync<string?>("""
            delete from registration_challenges
            where id=@Id and answer_hash=@Answer and expires_at>now()
            returning id
            """, new { Id = body.ChallengeId.Trim(), Answer = Sha256(body.ChallengeAnswer.Trim()) });
        if (consumed is null)
            return Results.BadRequest(new { error = "bot_check_failed" });
    }
    var (hash, salt) = HashPassword(body.Password);
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    try
    {
        await db.ExecuteAsync("select pg_advisory_xact_lock(hashtext(@Name))", new { Name = normalizedName }, tx);
        var nameTaken = await db.ExecuteScalarAsync<bool>(
            "select exists(select 1 from users where lower(name)=@Name)", new { Name = normalizedName }, tx);
        if (nameTaken)
        {
            await tx.RollbackAsync();
            return Results.Conflict(new { error = "name_taken" });
        }
        await db.ExecuteAsync("""
            insert into users(email,name,password_hash,password_salt)
            values (@Email,@Name,@Hash,@Salt)
            """, new { Email = email, Name = name, Hash = hash, Salt = salt }, tx);
        await tx.CommitAsync();
        return Results.Ok(new { created = true });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "account_exists" });
    }
});

app.MapPost("/auth/login", async (Credentials body) =>
{
    await using var db = await OpenDb();
    var user = await GetUser(db, NormalizeEmail(body.Email));
    if (user is null || user.Banned || !VerifyPassword(body.Password, user.PasswordHash, user.PasswordSalt))
        return Results.Unauthorized();
    if (user.HwidHash.Length == 64 && !FixedHexEquals(user.HwidHash, body.HwidHash))
        return Results.Json(new { error = "device_mismatch" }, statusCode: 403);
    var authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await db.ExecuteAsync("""
        insert into auth_sessions(token_hash,email,expires_at)
        values (@Hash,@Email,now()+interval '30 days')
        """, new { Hash = Sha256(authToken), user.Email });
    return Results.Ok(new { user.Email, user.Name, user.Role, user.PremiumUntil, deviceBound = user.HwidHash.Length == 64, authToken });
});

app.MapPost("/web/login", async (Credentials body) =>
{
    await using var db = await OpenDb();
    var user = await GetUser(db, NormalizeEmail(body.Email));
    if (user is null || user.Banned || !VerifyPassword(body.Password, user.PasswordHash, user.PasswordSalt))
        return Results.Unauthorized();
    var authToken = await CreateAuthSession(db, user.Email);
    return Results.Ok(PublicUser(user, authToken));
});

app.MapGet("/web/me", async (HttpRequest request) =>
{
    var raw = BearerToken(request);
    if (raw.Length == 0) return Results.Unauthorized();
    await using var db = await OpenDb();
    var user = await GetSessionUser(db, raw);
    return user is null ? Results.Unauthorized() : Results.Ok(PublicUser(user, ""));
});

app.MapPost("/web/device/start", async (HttpRequest request) =>
{
    var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    var pollToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await using var db = await OpenDb();
    await db.ExecuteAsync("delete from launcher_login_requests where expires_at<=now() or consumed_at is not null");
    await db.ExecuteAsync("""
        insert into launcher_login_requests(code,poll_token_hash,expires_at)
        values (@Code,@Hash,now()+interval '10 minutes')
        """, new { Code = code, Hash = Sha256(pollToken) });
    var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
    var site = $"{scheme}://{request.Host}/account.html?code={code}";
    return Results.Ok(new { code, pollToken, site, expiresIn = 600 });
});

app.MapPost("/web/device/authorize", async (DeviceAuthorize body) =>
{
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var user = await GetSessionUser(db, body.AuthToken, tx);
    if (user is null) { await tx.RollbackAsync(); return Results.Unauthorized(); }
    var launcherToken = await CreateAuthSession(db, user.Email, tx);
    var changed = await db.ExecuteAsync("""
        update launcher_login_requests set email=@Email,result_token=@Token
        where code=@Code and expires_at>now() and consumed_at is null and result_token is null
        """, new { user.Email, Token = launcherToken, Code = body.Code.Trim() }, tx);
    if (changed != 1) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "invalid_or_expired_code" }); }
    await tx.CommitAsync();
    return Results.Ok(new { authorized = true });
});

app.MapPost("/web/device/poll", async (DevicePoll body) =>
{
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var row = await db.QuerySingleOrDefaultAsync<DeviceLoginRow>("""
        select r.result_token as "AuthToken",u.email as "Email",u.name as "Name",u.role as "Role",
               u.premium_until as "PremiumUntil",u.hwid_hash as "HwidHash",u.banned as "Banned",
               '' as "PasswordHash",'' as "PasswordSalt"
        from launcher_login_requests r left join users u on u.email=r.email
        where r.poll_token_hash=@Hash and r.expires_at>now() and r.consumed_at is null
        for update of r
        """, new { Hash = Sha256(body.PollToken) }, tx);
    if (row is null) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "expired" }); }
    if (string.IsNullOrWhiteSpace(row.AuthToken)) { await tx.RollbackAsync(); return Results.Accepted(); }
    await db.ExecuteAsync("update launcher_login_requests set consumed_at=now() where poll_token_hash=@Hash",
        new { Hash = Sha256(body.PollToken) }, tx);
    await tx.CommitAsync();
    return Results.Ok(new { row.Email, row.Name, row.Role, row.PremiumUntil, deviceBound = row.HwidHash.Length == 64, row.AuthToken });
});

app.MapPost("/subscription/activate", async (ActivateKey body) =>
{
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var email = NormalizeEmail(body.Email);
    var days = await db.QuerySingleOrDefaultAsync<int?>("""
        select days from subscription_keys
        where key=@Key and used_at is null
        for update
        """, new { Key = body.Key.Trim().ToUpperInvariant() }, tx);
    if (days is null || !await db.ExecuteScalarAsync<bool>("select exists(select 1 from users where email=@Email)", new { Email = email }, tx))
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "invalid_or_used_key" });
    }
    var until = await db.ExecuteScalarAsync<DateTime>("""
        update users set
          role='premium',
          premium_until=greatest(coalesce(premium_until,now()),now()) + (@Days || ' days')::interval,
          hwid_hash=case
            when exists(select 1 from launch_tokens where email=@Email) then hwid_hash
            else ''
          end
        where email=@Email
        returning premium_until
        """, new { Email = email, Days = days.Value }, tx);
    await db.ExecuteAsync("""
        update subscription_keys set used_by=@Email,used_at=now()
        where key=@Key and used_at is null
        """, new { Email = email, Key = body.Key.Trim().ToUpperInvariant() }, tx);
    await tx.CommitAsync();
    return Results.Ok(new { premiumUntil = until, days });
});

app.MapPost("/web/subscription/activate", async (HttpRequest request, ActivateKey body) =>
{
    var token = BearerToken(request);
    if (token.Length == 0) return Results.Unauthorized();
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var user = await GetSessionUser(db, token, tx);
    if (user is null) { await tx.RollbackAsync(); return Results.Unauthorized(); }
    var key = body.Key.Trim().ToUpperInvariant();
    var days = await db.QuerySingleOrDefaultAsync<int?>("""
        select days from subscription_keys
        where key=@Key and used_at is null
        for update
        """, new { Key = key }, tx);
    if (days is null)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "invalid_or_used_key" });
    }
    var until = await db.ExecuteScalarAsync<DateTime>("""
        update users set role='premium',
          premium_until=greatest(coalesce(premium_until,now()),now()) + (@Days || ' days')::interval
        where email=@Email returning premium_until
        """, new { user.Email, Days = days.Value }, tx);
    await db.ExecuteAsync("""
        update subscription_keys set used_by=@Email,used_at=now()
        where key=@Key and used_at is null
        """, new { user.Email, Key = key }, tx);
    await tx.CommitAsync();
    return Results.Ok(new { premiumUntil = until, days });
});

app.MapPost("/device/bind", async (Credentials body) =>
{
    if (body.HwidHash.Length != 64) return Results.BadRequest(new { error = "invalid_hwid" });
    await using var db = await OpenDb();
    var user = await GetUser(db, NormalizeEmail(body.Email));
    if (user is null || !VerifyPassword(body.Password, user.PasswordHash, user.PasswordSalt))
        return Results.Unauthorized();
    if (user.HwidHash.Length == 0)
    {
        await db.ExecuteAsync("update users set hwid_hash=@Hwid where email=@Email and hwid_hash=''",
            new { Hwid = body.HwidHash, Email = user.Email });
        return Results.Ok(new { bound = true });
    }
    return FixedHexEquals(user.HwidHash, body.HwidHash)
        ? Results.Ok(new { bound = false })
        : Results.Json(new { error = "device_mismatch" }, statusCode: 403);
});

app.MapPost("/device/reset-hwid-key", async (ResetHwidKey body) =>
{
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var email = NormalizeEmail(body.Email);
    var sessionValid = !string.IsNullOrWhiteSpace(body.AuthToken) && await db.ExecuteScalarAsync<bool>("""
        select exists(select 1 from auth_sessions
        where token_hash=@Hash and email=@Email and revoked_at is null and expires_at>now())
        """, new { Hash = Sha256(body.AuthToken), Email = email }, tx);
    if (!sessionValid)
    {
        await tx.RollbackAsync();
        return Results.Unauthorized();
    }
    var changedKey = await db.ExecuteAsync("""
        update hwid_reset_keys set used_by=@Email,used_at=now()
        where key=@Key and used_at is null
        """, new { Email = email, Key = body.Key.Trim().ToUpperInvariant() }, tx);
    if (changedKey != 1)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { error = "invalid_or_used_key" });
    }
    await db.ExecuteAsync("update users set hwid_hash='' where email=@Email", new { Email = email }, tx);
    await tx.CommitAsync();
    return Results.Ok(new { reset = true });
});

app.MapPost("/auth/forgot", async (ForgotRequest body) =>
{
    var email = NormalizeEmail(body.Email);
    await using var db = await OpenDb();
    if (!await db.ExecuteScalarAsync<bool>("select exists(select 1 from users where email=@Email)", new { Email = email }))
    {
        return ValidLauncherProof("forgot", email, "", "", body.Timestamp, body.ClientProof)
            ? Results.Ok(new { sent = false, error = "account_not_found" })
            : Results.Ok();
    }
    var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    await db.ExecuteAsync("""
        insert into reset_codes(email,code_hash,expires_at,used_at)
        values (@Email,@Hash,now()+interval '10 minutes',null)
        on conflict(email) do update set code_hash=excluded.code_hash,expires_at=excluded.expires_at,used_at=null
        """, new { Email = email, Hash = Sha256(code) });
    var delivery = await SendResetCodeAsync(email, code);
    if (delivery.Sent) return Results.Ok(new { sent = true, provider = delivery.Provider });

    if (ValidLauncherProof("forgot", email, "", "", body.Timestamp, body.ClientProof))
    {
        return Results.Ok(new
        {
            sent = false,
            fallbackCode = code,
            error = delivery.Error,
            detail = delivery.Detail
        });
    }

    return Results.Json(new { error = delivery.Error, detail = delivery.Detail }, statusCode: 502);
});

app.MapPost("/auth/reset", async (ResetRequest body) =>
{
    if (body.NewPassword.Length < 8) return Results.BadRequest(new { error = "weak_password" });
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var email = NormalizeEmail(body.Email);
    var changed = await db.ExecuteAsync("""
        update reset_codes set used_at=now()
        where email=@Email and code_hash=@Hash and used_at is null and expires_at>now()
        """, new { Email = email, Hash = Sha256(body.Code.Trim()) }, tx);
    if (changed != 1) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "invalid_code" }); }
    var (hash, salt) = HashPassword(body.NewPassword);
    await db.ExecuteAsync("update users set password_hash=@Hash,password_salt=@Salt where email=@Email",
        new { Email = email, Hash = hash, Salt = salt }, tx);
    await tx.CommitAsync();
    return Results.Ok();
});

app.MapPost("/launcher/launch-token", async (LaunchRequest body) =>
{
    if (body.HwidHash.Length != 64) return Results.BadRequest(new { error = "invalid_hwid" });
    var launchEmail = NormalizeEmail(body.Email);
    if (!ValidLauncherProof("launch", launchEmail, body.HwidHash, body.ModVersion, body.Timestamp, body.LauncherProof))
        return Results.Json(new { error = "invalid_launcher" }, statusCode: 403);
    await using var db = await OpenDb();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
    var user = await GetUser(db, launchEmail, tx);
    var sessionValid = !string.IsNullOrWhiteSpace(body.AuthToken) && await db.ExecuteScalarAsync<bool>("""
        select exists(select 1 from auth_sessions
        where token_hash=@Hash and email=@Email and revoked_at is null and expires_at>now())
        """, new { Hash = Sha256(body.AuthToken), Email = launchEmail }, tx);
    var passwordValid = user is not null && !string.IsNullOrEmpty(body.Password) &&
                        VerifyPassword(body.Password, user.PasswordHash, user.PasswordSalt);
    if (user is null || user.Banned || (!sessionValid && !passwordValid))
    {
        await tx.RollbackAsync(); return Results.Unauthorized();
    }
    if (user.PremiumUntil is null || user.PremiumUntil <= DateTime.UtcNow)
    {
        await tx.RollbackAsync(); return Results.Json(new { error = "premium_required" }, statusCode: 403);
    }
    if (user.HwidHash.Length == 0)
        await db.ExecuteAsync("update users set hwid_hash=@Hwid where email=@Email", new { Hwid = body.HwidHash, user.Email }, tx);
    else if (!FixedHexEquals(user.HwidHash, body.HwidHash))
    {
        // Older launcher builds could bind a device before Minecraft was ever launched.
        // Permit one correction only when this account has never received a launch token.
        var hasLaunched = await db.ExecuteScalarAsync<bool>("""
            select exists(select 1 from launch_tokens where email=@Email)
            """, new { user.Email }, tx);
        if (hasLaunched)
        {
            await tx.RollbackAsync(); return Results.Json(new { error = "device_mismatch" }, statusCode: 403);
        }
        await db.ExecuteAsync("update users set hwid_hash=@Hwid where email=@Email",
            new { Hwid = body.HwidHash, user.Email }, tx);
    }
    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await db.ExecuteAsync("""
        insert into launch_tokens(token_hash,email,hwid_hash,launcher_version,mod_version,expires_at)
        values (@Hash,@Email,@Hwid,@Launcher,@Mod,now()+interval '3 minutes')
        """, new { Hash = Sha256(raw), Email = user.Email, Hwid = body.HwidHash, Launcher = body.LauncherVersion, Mod = body.ModVersion }, tx);
    await tx.CommitAsync();
    return Results.Ok(new { launchToken = raw, expiresIn = 180 });
});

// Launchers use this value instead of trusting a customer's Windows clock.
app.MapGet("/public/time", () => Results.Ok(new
{
    unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
}));

app.MapPost("/profile/name", async (ProfileNameRequest body) =>
{
    var name = body.Name.Trim();
    if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "invalid_name" });
    await using var db = await OpenDb();
    var changed = await db.ExecuteAsync("""
        update users set name=@Name
        where email=@Email and exists(
          select 1 from auth_sessions
          where token_hash=@Hash and email=@Email and revoked_at is null and expires_at>now()
        )
        """, new { Name = name, Email = NormalizeEmail(body.Email), Hash = Sha256(body.AuthToken) });
    return changed == 1 ? Results.Ok() : Results.Unauthorized();
});

app.MapPost("/mod/verify-token", async (VerifyLaunch body) =>
{
    await using var db = await OpenDb();
    var consumed = await db.QuerySingleOrDefaultAsync<LaunchTokenRow>("""
        update launch_tokens t set used_at=now(), last_seen_at=now(), session_expires_at=now()+interval '5 minutes'
        from users u
        where t.token_hash=@Hash and t.email=u.email and t.used_at is null and t.expires_at>now()
          and t.hwid_hash=@Hwid and t.mod_version=@Mod and u.banned=false and u.premium_until>now()
        returning t.email as "Email",u.name as "Name",u.premium_until as "PremiumUntil"
        """, new { Hash = Sha256(body.LaunchToken), Hwid = body.HwidHash, Mod = body.ModVersion });
    return consumed is null
        ? Results.Unauthorized()
        : Results.Ok(new { valid = true, consumed.Email, consumed.Name, consumed.PremiumUntil, heartbeatAfterSeconds = 60 });
});

app.MapPost("/mod/heartbeat", async (VerifyLaunch body) =>
{
    await using var db = await OpenDb();
    var refreshed = await db.ExecuteAsync("""
        update launch_tokens t set last_seen_at=now(), session_expires_at=now()+interval '5 minutes'
        from users u
        where t.token_hash=@Hash and t.email=u.email and t.used_at is not null
          and t.session_expires_at>now() and t.hwid_hash=@Hwid and t.mod_version=@Mod
          and u.banned=false and u.premium_until>now()
        """, new { Hash = Sha256(body.LaunchToken), Hwid = body.HwidHash, Mod = body.ModVersion });
    return refreshed == 1 ? Results.Ok(new { valid = true, heartbeatAfterSeconds = 60 }) : Results.Unauthorized();
});

app.MapPost("/admin/keys", async (HttpRequest request, CreateKey body) =>
{
    if (!AdminAllowed(request) || body.Days is < 1 or > 3650) return Results.Unauthorized();
    var value = $"SOUL-{body.Days}-{Guid.NewGuid():N}".ToUpperInvariant();
    await using var db = await OpenDb();
    await db.ExecuteAsync("insert into subscription_keys(key,days) values (@Key,@Days)", new { Key = value, body.Days });
    return Results.Ok(new { key = value });
});

app.MapPost("/admin/hwid-reset-keys", async (HttpRequest request) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    var value = $"HWID-{Guid.NewGuid():N}".ToUpperInvariant();
    await using var db = await OpenDb();
    await db.ExecuteAsync("insert into hwid_reset_keys(key) values (@Key)", new { Key = value });
    return Results.Ok(new { key = value });
});

app.MapGet("/admin/users", async (HttpRequest request) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    await using var db = await OpenDb();
    return Results.Ok(await db.QueryAsync("""
        select email as "Email",name as "Name",role as "Role",premium_until as "PremiumUntil",
               hwid_hash as "Hwid",banned as "Banned" from users order by created_at desc
        """));
});

app.MapPost("/admin/premium/grant", async (HttpRequest req, AdminDays body) =>
{
    if (!AdminAllowed(req) || body.Days is < 1 or > 3650) return Results.Unauthorized();
    await using var db = await OpenDb();
    await db.ExecuteAsync("""
        update users set role='premium',
        premium_until=greatest(coalesce(premium_until,now()),now())+(@Days||' days')::interval
        where email=@Email
        """, new { Email = NormalizeEmail(body.Email), body.Days });
    return Results.Ok();
});
app.MapPost("/admin/premium/revoke", async (HttpRequest req, AdminUser body) =>
{
    if (!AdminAllowed(req)) return Results.Unauthorized();
    await using var db = await OpenDb();
    await db.ExecuteAsync("update users set role='free',premium_until=null where email=@Email", new { Email = NormalizeEmail(body.Email) });
    return Results.Ok();
});
app.MapPost("/admin/device/reset", async (HttpRequest req, AdminUser body) =>
{
    if (!AdminAllowed(req)) return Results.Unauthorized();
    await using var db = await OpenDb();
    await db.ExecuteAsync("update users set hwid_hash='' where email=@Email", new { Email = NormalizeEmail(body.Email) });
    return Results.Ok();
});
app.MapPost("/admin/user/ban", async (HttpRequest req, AdminBan body) =>
{
    if (!AdminAllowed(req)) return Results.Unauthorized();
    await using var db = await OpenDb();
    await db.ExecuteAsync("update users set banned=@Banned where email=@Email", new { Email = NormalizeEmail(body.Email), body.Banned });
    return Results.Ok();
});

app.MapPost("/prime/admin/keys", async (HttpRequest request, PrimeCreateKey body) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    var value = string.IsNullOrWhiteSpace(body.Key)
        ? $"PRIME-{RandomPart()}-{RandomPart()}-{RandomPart()}"
        : body.Key.Trim().ToUpperInvariant();
    await using var db = await OpenDb();
    await db.ExecuteAsync("""
        insert into prime_keys(key,buyer,notes,active)
        values (@Key,@Buyer,@Notes,true)
        on conflict (key) do update set buyer=@Buyer,notes=@Notes,active=true
        """, new { Key = value, Buyer = body.Buyer.Trim(), Notes = body.Notes.Trim() });
    return Results.Ok(new { key = value });
});

app.MapGet("/prime/admin/keys", async (HttpRequest request) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    await using var db = await OpenDb();
    return Results.Ok(await db.QueryAsync("""
        select key as "Key",buyer as "Buyer",notes as "Notes",active as "Active",
               created_at as "CreatedAt",activated_at as "ActivatedAt",
               last_seen_at as "LastSeenAt",hwid_hash as "HwidHash"
        from prime_keys order by created_at desc
        """));
});

app.MapPost("/prime/admin/keys/state", async (HttpRequest request, PrimeKeyState body) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    await using var db = await OpenDb();
    var changed = await db.ExecuteAsync("update prime_keys set active=@Active where key=@Key",
        new { Key = body.Key.Trim().ToUpperInvariant(), body.Active });
    return changed == 1 ? Results.Ok() : Results.NotFound();
});

app.MapPost("/prime/admin/keys/reset-hwid", async (HttpRequest request, PrimeKeyReset body) =>
{
    if (!AdminAllowed(request)) return Results.Unauthorized();
    await using var db = await OpenDb();
    var changed = await db.ExecuteAsync("update prime_keys set hwid_hash='', activated_at=null where key=@Key",
        new { Key = body.Key.Trim().ToUpperInvariant() });
    return changed == 1 ? Results.Ok() : Results.NotFound();
});

app.MapPost("/prime/activate", async (PrimeLicenseRequest body) =>
{
    var key = body.Key.Trim().ToUpperInvariant();
    if (key.Length < 8) return Results.BadRequest(new { valid = false, error = "invalid_key" });
    await using var db = await OpenDb();
    var row = await db.QuerySingleOrDefaultAsync<PrimeKeyRow>("""
        select key as "Key",buyer as "Buyer",active as "Active",hwid_hash as "HwidHash"
        from prime_keys where key=@Key
        """, new { Key = key });
    if (row is null || !row.Active) return Results.Unauthorized();
    var hwid = body.HwidHash.Trim();
    if (!string.IsNullOrWhiteSpace(row.HwidHash) && !string.IsNullOrWhiteSpace(hwid) && row.HwidHash != hwid)
        return Results.Json(new { valid = false, error = "hwid_mismatch" }, statusCode: 403);
    await db.ExecuteAsync("""
        update prime_keys
        set activated_at=coalesce(activated_at,now()),
            last_seen_at=now(),
            hwid_hash=case when hwid_hash='' then @Hwid else hwid_hash end
        where key=@Key
        """, new { Key = key, Hwid = hwid });
    return Results.Ok(new { valid = true, key, user = row.Buyer, uid = body.Uid, expires = (string?)null });
});

app.MapPost("/prime/verify", async (PrimeLicenseRequest body) =>
{
    var key = body.Key.Trim().ToUpperInvariant();
    await using var db = await OpenDb();
    var row = await db.QuerySingleOrDefaultAsync<PrimeKeyRow>("""
        select key as "Key",buyer as "Buyer",active as "Active",hwid_hash as "HwidHash"
        from prime_keys where key=@Key
        """, new { Key = key });
    if (row is null || !row.Active) return Results.Unauthorized();
    var hwid = body.HwidHash.Trim();
    if (!string.IsNullOrWhiteSpace(row.HwidHash) && !string.IsNullOrWhiteSpace(hwid) && row.HwidHash != hwid)
        return Results.Json(new { valid = false, error = "hwid_mismatch" }, statusCode: 403);
    await db.ExecuteAsync("update prime_keys set last_seen_at=now() where key=@Key", new { Key = key });
    return Results.Ok(new { valid = true, key, user = row.Buyer, uid = body.Uid });
});

foreach (var alias in pathAliases)
{
    app.MapMethods(alias.Key, new[] { "GET", "POST" }, () =>
        Results.Redirect(alias.Value.ToString(), permanent: false, preserveMethod: true));
}

app.Run();

async Task<NpgsqlConnection> OpenDb()
{
    var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    return db;
}
bool AdminAllowed(HttpRequest request)
{
    var expected = app.Configuration["ADMIN_KEY"];
    var supplied = request.Headers["X-Admin-Key"].ToString();
    return !string.IsNullOrWhiteSpace(expected) && supplied.Length == expected.Length &&
           CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(expected));
}
static async Task<DbUser?> GetUser(NpgsqlConnection db, string email, IDbTransaction? tx = null) =>
    await db.QuerySingleOrDefaultAsync<DbUser>("""
        select email as "Email",name as "Name",password_hash as "PasswordHash",password_salt as "PasswordSalt",
               role as "Role",premium_until as "PremiumUntil",hwid_hash as "HwidHash",banned as "Banned"
        from users where email=@Email
        """, new { Email = email }, tx);
static async Task<DbUser?> GetSessionUser(NpgsqlConnection db, string token, IDbTransaction? tx = null) =>
    await db.QuerySingleOrDefaultAsync<DbUser>("""
        select u.email as "Email",u.name as "Name",u.password_hash as "PasswordHash",u.password_salt as "PasswordSalt",
               u.role as "Role",u.premium_until as "PremiumUntil",u.hwid_hash as "HwidHash",u.banned as "Banned"
        from auth_sessions s join users u on u.email=s.email
        where s.token_hash=@Hash and s.revoked_at is null and s.expires_at>now() and u.banned=false
        """, new { Hash = Sha256(token) }, tx);
static async Task<string> CreateAuthSession(NpgsqlConnection db, string email, IDbTransaction? tx = null)
{
    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await db.ExecuteAsync("insert into auth_sessions(token_hash,email,expires_at) values (@Hash,@Email,now()+interval '30 days')",
        new { Hash = Sha256(raw), Email = email }, tx);
    return raw;
}
static object PublicUser(DbUser user, string authToken) => new
{
    user.Email, user.Name, user.Role, user.PremiumUntil,
    deviceBound = user.HwidHash.Length == 64,
    authToken
};
static string BearerToken(HttpRequest request)
{
    var value = request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : "";
}
static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();
static string NormalizeName(string value) => value.Trim().ToLowerInvariant();
static bool ValidEmail(string value) => value.Contains('@') && value.Length <= 254;
static (string Hash, string Salt) HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
}
static bool VerifyPassword(string password, string hash, string salt) =>
    CryptographicOperations.FixedTimeEquals(
        Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 210_000, HashAlgorithmName.SHA256, 32),
        Convert.FromBase64String(hash));
static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string RandomPart() => Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
static bool FixedHexEquals(string left, string right)
{
    try { return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
    catch { return false; }
}
async Task<MailDelivery> SendResetCodeAsync(string email, string code)
{
    var resendKey = app.Configuration["RESEND_API_KEY"];
    if (!string.IsNullOrWhiteSpace(resendKey))
    {
        var fromEmail = app.Configuration["RESEND_FROM_EMAIL"] ?? "SoulsDLC <onboarding@resend.dev>";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", resendKey);
        var sent = await client.PostAsJsonAsync("https://api.resend.com/emails", new
        {
            from = fromEmail,
            to = new[] { email },
            subject = "SoulsDLC password reset",
            html = $"<h2>{code}</h2><p>This code expires in 10 minutes.</p>"
        });
        if (sent.IsSuccessStatusCode) return new(true, "resend", "", "");
        var detail = await sent.Content.ReadAsStringAsync();
        var fallback = await SendResetCodeSmtpAsync(email, code);
        return fallback.Sent ? fallback : new(false, "resend", "email_delivery_failed", detail);
    }

    return await SendResetCodeSmtpAsync(email, code);
}
async Task<MailDelivery> SendResetCodeSmtpAsync(string email, string code)
{
    var host = app.Configuration["SMTP_HOST"];
    var user = app.Configuration["SMTP_USER"];
    var password = app.Configuration["SMTP_PASSWORD"];
    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        return new(false, "none", "email_service_unavailable", "Set RESEND_API_KEY or SMTP_HOST/SMTP_USER/SMTP_PASSWORD.");

    try
    {
        using var message = new MailMessage
        {
            From = new MailAddress(app.Configuration["SMTP_FROM"] ?? user, "SoulsDLC"),
            Subject = "SoulsDLC password reset",
            Body = $"Your SoulsDLC password reset code is {code}. It expires in 10 minutes.",
            IsBodyHtml = false
        };
        message.To.Add(email);
        using var smtp = new SmtpClient(host, int.TryParse(app.Configuration["SMTP_PORT"], out var port) ? port : 587)
        {
            EnableSsl = !string.Equals(app.Configuration["SMTP_SSL"], "false", StringComparison.OrdinalIgnoreCase),
            Credentials = new System.Net.NetworkCredential(user, password)
        };
        await smtp.SendMailAsync(message);
        return new(true, "smtp", "", "");
    }
    catch (Exception ex)
    {
        return new(false, "smtp", "email_delivery_failed", ex.Message);
    }
}
bool ValidLauncherProof(string purpose, string email, string hwidHash, string modVersion, long timestamp, string proof)
{
    if (timestamp <= 0 || string.IsNullOrWhiteSpace(proof)) return false;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    // Existing launcher builds sign with the local Windows clock. A number of
    // legitimate customers have clocks outside the old five-minute window.
    // Keep replay protection while allowing those already distributed builds
    // to recover without an HWID reset or reinstall.
    if (Math.Abs(now - timestamp) > 172800) return false;
    var expected = Sha256($"{purpose}|{email}|{hwidHash}|{modVersion}|{timestamp}|{LauncherProofSecret()}");
    return proof.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(proof.ToLowerInvariant()),
        Encoding.UTF8.GetBytes(expected));
}
string LauncherProofSecret() => app.Configuration["LAUNCHER_PROOF_SECRET"]
    ?? throw new InvalidOperationException("LAUNCHER_PROOF_SECRET is required");
static string NormalizePostgresUrl(string value)
{
    if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return value;
    var uri = new Uri(value);
    var userInfo = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host, Port = uri.Port, Database = uri.AbsolutePath.Trim('/'),
        Username = Uri.UnescapeDataString(userInfo[0]), Password = Uri.UnescapeDataString(userInfo[1]),
        SslMode = uri.Host.EndsWith(".railway.internal") ? SslMode.Disable : SslMode.Require
    }.ConnectionString;
}

record Credentials(string Email, string Password, string HwidHash = "", string Name = "Player", string ChallengeId = "", string ChallengeAnswer = "");
record ActivateKey(string Email, string Key);
record ForgotRequest(string Email, long Timestamp = 0, string ClientProof = "");
record ResetRequest(string Email, string Code, string NewPassword);
record DeviceAuthorize(string Code, string AuthToken);
record DevicePoll(string PollToken);
record CreateKey(int Days);
record ResetHwidKey(string Email, string AuthToken, string Key);
record LaunchRequest(string Email, string Password, string AuthToken, string HwidHash, string LauncherVersion, string ModVersion, long Timestamp = 0, string LauncherProof = "");
record VerifyLaunch(string LaunchToken, string HwidHash, string ModVersion);
record ProfileNameRequest(string Email, string AuthToken, string Name);
record AdminUser(string Email);
record AdminDays(string Email, int Days);
record AdminBan(string Email, bool Banned);
record PrimeCreateKey(string Buyer = "", string Notes = "", string Key = "");
record PrimeKeyState(string Key, bool Active);
record PrimeKeyReset(string Key);
record PrimeLicenseRequest(string Key, string HwidHash = "", string User = "", int Uid = 0);
record MailDelivery(bool Sent, string Provider, string Error, string Detail);
class DbUser
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime? PremiumUntil { get; set; }
    public string HwidHash { get; set; } = "";
    public bool Banned { get; set; }
}
sealed class LaunchTokenRow
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime PremiumUntil { get; set; }
}
sealed class DeviceLoginRow : DbUser
{
    public string AuthToken { get; set; } = "";
}
sealed class PrimeKeyRow
{
    public string Key { get; set; } = "";
    public string Buyer { get; set; } = "";
    public bool Active { get; set; }
    public string HwidHash { get; set; } = "";
}
