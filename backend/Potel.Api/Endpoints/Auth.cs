using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record LoginRequest(string Email, string Password);
public record PasswordRequest(string Current, string New);
public record UserInput(string Name, string Email, string Role, bool Active, string? Password);
public record UserDto(int Id, string Name, string Email, string Role, bool Active, DateTime CreatedAt, DateTime? LastLoginAt)
{
    public static UserDto From(User u) => new(u.Id, u.Name, u.Email, u.Role, u.Active, u.CreatedAt, u.LastLoginAt);
}

public static class AuthEndpoints
{
    public const int MinPasswordLength = 8;
    static readonly PasswordHasher<User> Hasher = new();

    public static string Hash(User u, string password) => Hasher.HashPassword(u, password);

    public static int? UserId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    static async Task SignIn(HttpContext http, User u)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Name),
            new Claim(ClaimTypes.Email, u.Email),
            new Claim(ClaimTypes.Role, u.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (AppDb db, HttpContext http, LoginRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            var ok = u is not null && u.Active &&
                     Hasher.VerifyHashedPassword(u, u.PasswordHash, req.Password ?? "") != PasswordVerificationResult.Failed;
            if (!ok) return Results.Json(new { error = "E-mailadres of wachtwoord klopt niet" }, statusCode: 401);
            u!.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SignIn(http, u);
            return Results.Ok(UserDto.From(u));
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (AppDb db, ClaimsPrincipal user) =>
            user.UserId() is { } id && await db.Users.FindAsync(id) is { Active: true } u
                ? Results.Ok(UserDto.From(u)) : Results.Unauthorized());

        g.MapPut("/password", async (AppDb db, ClaimsPrincipal user, PasswordRequest req) =>
        {
            var u = user.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (u is null) return Results.Unauthorized();
            if (Hasher.VerifyHashedPassword(u, u.PasswordHash, req.Current ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "Je huidige wachtwoord klopt niet" });
            if ((req.New ?? "").Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
            u.PasswordHash = Hash(u, req.New!);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    public static void MapUsers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/users").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapGet("/", async (AppDb db) => (await db.Users.OrderBy(u => u.Name).ToListAsync()).Select(UserDto.From));

        g.MapPost("/", async (AppDb db, UserInput input) =>
        {
            var email = (input.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(input.Name) || !email.Contains('@')) return Results.BadRequest(new { error = "Vul een naam en een geldig e-mailadres in" });
            if (!Roles.All.Contains(input.Role)) return Results.BadRequest(new { error = "Onbekende rol" });
            if ((input.Password ?? "").Length < MinPasswordLength) return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
            if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { error = "Dit e-mailadres is al in gebruik" });
            var u = new User { Name = input.Name.Trim(), Email = email, Role = input.Role, Active = input.Active };
            u.PasswordHash = Hash(u, input.Password!);
            db.Users.Add(u);
            db.Log("gebruiker", $"Gebruiker {u.Name} toegevoegd");
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{u.Id}", UserDto.From(u));
        });

        g.MapPut("/{id:int}", async (AppDb db, ClaimsPrincipal me, int id, UserInput input) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            var email = (input.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(input.Name) || !email.Contains('@')) return Results.BadRequest(new { error = "Vul een naam en een geldig e-mailadres in" });
            if (!Roles.All.Contains(input.Role)) return Results.BadRequest(new { error = "Onbekende rol" });
            if (await db.Users.AnyAsync(x => x.Email == email && x.Id != id)) return Results.Conflict(new { error = "Dit e-mailadres is al in gebruik" });
            var losesAdmin = u.Role == Roles.Admin && u.Active && (input.Role != Roles.Admin || !input.Active);
            if (losesAdmin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Roles.Admin && x.Active))
                return Results.BadRequest(new { error = "Er moet minstens één actieve beheerder overblijven" });
            if (me.UserId() == id && !input.Active) return Results.BadRequest(new { error = "Je kunt jezelf niet uitschakelen" });
            if (!string.IsNullOrEmpty(input.Password))
            {
                if (input.Password.Length < MinPasswordLength) return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
                u.PasswordHash = Hash(u, input.Password);
            }
            u.Name = input.Name.Trim(); u.Email = email; u.Role = input.Role; u.Active = input.Active;
            await db.SaveChangesAsync();
            return Results.Ok(UserDto.From(u));
        });

        g.MapDelete("/{id:int}", async (AppDb db, ClaimsPrincipal me, int id) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (me.UserId() == id) return Results.BadRequest(new { error = "Je kunt jezelf niet verwijderen" });
            if (u.Role == Roles.Admin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Roles.Admin && x.Active))
                return Results.BadRequest(new { error = "Er moet minstens één actieve beheerder overblijven" });
            db.Users.Remove(u);
            db.Log("gebruiker", $"Gebruiker {u.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
