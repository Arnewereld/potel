using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Integrations;

namespace Potel.Api.Endpoints;

public record GitHubTokenRequest(string? Token);
public record GitHubEmailsRequest(string[]? Emails);
public record GitHubAcceptRequest(int ProjectId, DateTime Date, int Minutes, string? Description, bool Billable, string[]? Shas);
public record GitHubSkipRequest(int ProjectId, string[]? Shas);

// Hoe lang je aan een dag commits werkte, geschat uit de tijd tussen de commits. De regels staan ook in het scherm.
public static class CommitDuration
{
    // Zit er meer tijd tussen twee commits, dan was je tussendoor met iets anders bezig en begint een nieuw blok.
    public const int GapMinutes = 120;
    // Voor de eerste commit van elk blok werkte je ook al; zoveel minuten rekenen we daarvoor.
    public const int FirstCommitMinutes = 30;
    // Afgerond naar boven op een kwartier, en nooit meer dan dit per dag.
    public const int RoundToMinutes = 15;
    public const int MaxMinutesPerDay = 10 * 60;

    public static int Estimate(IEnumerable<DateTime> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        if (sorted.Count == 0) return 0;
        double total = FirstCommitMinutes;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = (sorted[i] - sorted[i - 1]).TotalMinutes;
            total += gap <= GapMinutes ? gap : FirstCommitMinutes;
        }
        var rounded = (int)Math.Ceiling(total / RoundToMinutes) * RoundToMinutes;
        return Math.Clamp(rounded, FirstCommitMinutes, MaxMinutesPerDay);
    }
}

// Urenvoorstellen uit GitHub: Potel haalt je commits op uit de repositories van je projecten en stelt per project per dag
// een boeking voor. Je accepteert, past aan of slaat over; Potel boekt nooit zelf. Een geboekte of overgeslagen commit
// komt nooit meer terug.
public static partial class GitHubEndpoints
{
    public const string ProtectorPurpose = "Potel.GitHub.Token.v1";
    public const int MaxDays = 7;
    public const int MaxEmails = 5;
    public const int MaxRepos = 10;
    public const int MaxDescription = 1000;
    public const int MaxShas = 300;
    const string NoToken = "Er is nog geen GitHub-token ingesteld. Een beheerder doet dat bij Instellingen > GitHub.";

    [GeneratedRegex(@"^[A-Za-z0-9_]{20,255}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[^@\s,;<>""]{1,64}@[^@\s,;<>""]{1,190}\.[^@\s,;<>""]{2,}$")]
    private static partial Regex EmailPattern();

    // Het token weghalen mag ook in een werkruimte die alleen-lezen is: het is een geheim dat je kwijt wilt kunnen.
    public static bool IsRemoveToken(HttpRequest r) =>
        HttpMethods.IsDelete(r.Method) && r.Path.Equals("/api/github/token", StringComparison.OrdinalIgnoreCase);

    static IDataProtector Protector(IDataProtectionProvider dp) => dp.CreateProtector(ProtectorPurpose);

    // De adressen van deze gebruiker; zonder eigen lijst het adres van zijn account.
    static async Task<(List<string> Emails, bool Own, string Account)> EmailsAsync(AppDb db, int userId)
    {
        var account = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync() ?? "";
        var own = await db.GitHubAuthors.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId);
        var list = own?.Emails.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
        return list.Count > 0 ? (list, true, account) : (string.IsNullOrEmpty(account) ? [] : [account], false, account);
    }

    static async Task<object> SettingsDtoAsync(AppDb db, int userId)
    {
        var s = await db.GitHubSettings.AsNoTracking().FirstOrDefaultAsync();
        var (emails, own, account) = await EmailsAsync(db, userId);
        return new
        {
            token = s is null ? null : new { last4 = s.TokenLast4, s.UpdatedAt, s.UpdatedBy },
            emails = own ? emails : [],
            accountEmail = account,
            rules = Rules,
        };
    }

    static readonly object Rules = new
    {
        gapMinutes = CommitDuration.GapMinutes, firstCommitMinutes = CommitDuration.FirstCommitMinutes,
        roundToMinutes = CommitDuration.RoundToMinutes, maxMinutesPerDay = CommitDuration.MaxMinutesPerDay,
    };

    static string FirstLine(string message)
    {
        var line = message.Replace("\r", "").Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    // De omschrijving van een voorstel: de eerste regel van elke commit, oudste eerst, zonder dubbele.
    public static string Describe(IEnumerable<string> messages)
    {
        var text = string.Join("; ", messages.Select(FirstLine).Where(m => m.Length > 0).Distinct());
        return text.Length > MaxDescription ? text[..(MaxDescription - 1)] + "…" : text;
    }

    static List<string>? CleanShas(string[]? shas)
    {
        if (shas is not { Length: > 0 and <= MaxShas }) return null;
        var clean = shas.Select(s => (s ?? "").Trim().ToLowerInvariant()).Distinct().ToList();
        return clean.All(GitHubClient.IsSha) ? clean : null;
    }

    public static void MapGitHub(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/github");

        // Of er een token is (alleen de laatste 4 tekens) en met welke adressen jij commit.
        g.MapGet("/settings", async (AppDb db, ClaimsPrincipal user) =>
            user.UserId() is { } me ? Results.Ok(await SettingsDtoAsync(db, me)) : Results.Unauthorized());

        g.MapPut("/token", async (AppDb db, IDataProtectionProvider dp, BusinessClock clock, ClaimsPrincipal user, GitHubTokenRequest req) =>
        {
            var token = req.Token?.Trim() ?? "";
            if (!TokenPattern().IsMatch(token))
                return Results.BadRequest(new { error = "Dit lijkt geen GitHub-token. Een token begint meestal met github_pat_ of ghp_ en bestaat uit letters, cijfers en liggende streepjes." });
            await using var tx = await WriteLock.BeginAsync(db);
            var s = await db.GitHubSettings.FirstOrDefaultAsync();
            if (s is null) db.GitHubSettings.Add(s = new GitHubSettings());
            s.ProtectedToken = Protector(dp).Protect(token);
            s.TokenLast4 = token[^4..];
            s.UpdatedAt = clock.UtcNow;
            s.UpdatedBy = user.FindFirstValue(ClaimTypes.Name);
            db.Log("instellingen", "GitHub-token opgeslagen");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await SettingsDtoAsync(db, user.UserId() ?? 0));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapDelete("/token", async (AppDb db, ClaimsPrincipal user) =>
        {
            if (await db.GitHubSettings.ExecuteDeleteAsync() > 0)
            {
                db.Log("instellingen", "GitHub-token verwijderd");
                await db.SaveChangesAsync();
            }
            return Results.Ok(await SettingsDtoAsync(db, user.UserId() ?? 0));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Iedereen stelt zijn eigen adressen in. Een lege lijst betekent: het adres van je account.
        g.MapPut("/emails", async (AppDb db, ClaimsPrincipal user, GitHubEmailsRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            var emails = (req.Emails ?? []).Select(e => (e ?? "").Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
            if (emails.Count > MaxEmails) return Results.BadRequest(new { error = $"Vul hooguit {MaxEmails} e-mailadressen in." });
            if (emails.FirstOrDefault(e => e.Length > 254 || !EmailPattern().IsMatch(e)) is { } wrong)
                return Results.BadRequest(new { error = $"{wrong} is geen geldig e-mailadres." });
            await using var tx = await WriteLock.BeginAsync(db);
            var row = await db.GitHubAuthors.FirstOrDefaultAsync(a => a.UserId == me);
            if (emails.Count == 0) { if (row is not null) db.GitHubAuthors.Remove(row); }
            else
            {
                if (row is null) db.GitHubAuthors.Add(row = new GitHubAuthor { UserId = me });
                row.Emails = string.Join('\n', emails);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await SettingsDtoAsync(db, me));
        });

        // Voorstellen voor een dag of een week (hooguit 7 dagen), voor alle lopende projecten met een GitHub-repository of één project.
        g.MapGet("/suggestions", async (AppDb db, IDataProtectionProvider dp, GitHubClient github, ClaimsPrincipal user, DateTime? from, DateTime? to, int? projectId, CancellationToken ct) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            if (from is not { } start || to is not { } end || end.Date < start.Date)
                return Results.BadRequest(new { error = "Kies een dag of een week." });
            start = start.Date; end = end.Date;
            if ((end - start).TotalDays >= MaxDays) return Results.BadRequest(new { error = $"Kies hooguit {MaxDays} dagen tegelijk." });

            var settings = await db.GitHubSettings.AsNoTracking().FirstOrDefaultAsync();
            if (settings is null) return Results.BadRequest(new { error = NoToken });
            string token;
            try
            {
                token = Protector(dp).Unprotect(settings.ProtectedToken);
            }
            catch (CryptographicException)
            {
                return Results.BadRequest(new { error = "Het opgeslagen GitHub-token is niet meer te lezen, bijvoorbeeld na een verhuizing van de server. Een beheerder kan het opnieuw instellen bij Instellingen > GitHub." });
            }

            var projects = await db.Projects.AsNoTracking().Where(p => projectId == null ? p.Status == "actief" : p.Id == projectId).OrderBy(p => p.Id).ToListAsync();
            if (projectId is not null && projects.Count == 0) return Results.NotFound();
            var withRepo = projects.Select(p => (Project: p, Repo: GitHubRepo.Parse(p.RepoUrl))).Where(x => x.Repo is not null).ToList();
            if (projectId is not null && withRepo.Count == 0)
                return Results.BadRequest(new { error = "Dit project heeft geen GitHub-repository. Vul bij het project een link in zoals https://github.com/eigenaar/repository." });
            // Meerdere projecten met dezelfde repository: het voorstel gaat naar het eerste; bij boeken kies je zelf het project.
            var repos = withRepo.GroupBy(x => x.Repo!.FullName, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            var skippedRepos = repos.Count > MaxRepos ? repos.Skip(MaxRepos).Select(x => x.Repo!.FullName).ToList() : [];
            repos = repos.Take(MaxRepos).ToList();

            var (emails, _, _) = await EmailsAsync(db, me);
            emails = emails.Take(MaxEmails).ToList();
            if (emails.Count == 0) return Results.BadRequest(new { error = "Vul bij Instellingen > GitHub in met welk e-mailadres je commit." });
            var wanted = emails.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sinceUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), BusinessClock.Zone);
            var untilUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end.AddDays(1), DateTimeKind.Unspecified), BusinessClock.Zone);

            var problems = new List<object>();
            var truncated = new List<string>();
            var found = new List<(Project Project, GitHubRepo Repo, GitHubCommitInfo Commit)>();
            foreach (var (project, repo) in repos)
            {
                try
                {
                    foreach (var email in emails)
                    {
                        var page = await github.CommitsAsync(token, repo!, email, sinceUtc, untilUtc, ct);
                        if (page.Truncated) truncated.Add(repo!.FullName);
                        found.AddRange(page.Commits
                            .Where(c => !c.IsMerge && c.AuthorEmail is { } a && wanted.Contains(a) && c.TimeUtc >= sinceUtc && c.TimeUtc < untilUtc)
                            .Select(c => (project, repo!, c)));
                    }
                }
                catch (GitHubException e) when (e.StopsEverything)
                {
                    return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
                }
                catch (GitHubException e)
                {
                    problems.Add(new { project = project.Name, repo = repo!.FullName, message = e.Message });
                }
            }

            // Wat al geboekt of overgeslagen is, komt niet meer terug.
            var shas = found.Select(f => f.Commit.Sha).Distinct().ToList();
            var handled = (await db.GitHubCommits.AsNoTracking().Where(c => shas.Contains(c.Sha)).Select(c => c.Sha).ToListAsync()).ToHashSet();
            var suggestions = found
                .Where(f => !handled.Contains(f.Commit.Sha))
                .DistinctBy(f => f.Commit.Sha)
                .GroupBy(f => (f.Project.Id, Day: TimeZoneInfo.ConvertTimeFromUtc(f.Commit.TimeUtc, BusinessClock.Zone).Date))
                .OrderBy(x => x.Key.Day).ThenBy(x => x.First().Project.Name)
                .Select(x =>
                {
                    var commits = x.OrderBy(f => f.Commit.TimeUtc).ToList();
                    var first = commits[0];
                    return new
                    {
                        key = $"{first.Project.Id}-{x.Key.Day:yyyy-MM-dd}",
                        projectId = first.Project.Id, projectName = first.Project.Name, repo = first.Repo.FullName,
                        date = x.Key.Day,
                        minutes = CommitDuration.Estimate(commits.Select(f => f.Commit.TimeUtc)),
                        description = Describe(commits.Select(f => f.Commit.Message)),
                        commits = commits.Select(f => new
                        {
                            f.Commit.Sha, message = FirstLine(f.Commit.Message), time = f.Commit.TimeUtc, url = f.Commit.Url,
                        }),
                    };
                })
                .ToList();

            return Results.Ok(new
            {
                from = start, to = end, emails, suggestions, problems, truncated = truncated.Distinct().ToList(), skippedRepos, rules = Rules,
            });
        });

        // Een voorstel boeken, eventueel aangepast. De commits gaan mee, zodat ze nooit opnieuw voorgesteld worden.
        g.MapPost("/accept", async (AppDb db, ClaimsPrincipal user, GitHubAcceptRequest req) =>
        {
            if (CleanShas(req.Shas) is not { } shas) return Results.BadRequest(new { error = "Er ging iets mis met de commits van dit voorstel. Haal de voorstellen opnieuw op." });
            if (req.Date.Year < 2000) return Results.BadRequest(new { error = "Kies een datum." });
            if (req.Minutes <= 0) return Results.BadRequest(new { error = "Vul de tijd in" });
            if (req.Minutes > 24 * 60) return Results.BadRequest(new { error = "Meer dan 24 uur op één dag kan niet" });
            var description = (req.Description ?? "").Trim();
            if (description.Length > MaxDescription) return Results.BadRequest(new { error = $"De omschrijving mag hooguit {MaxDescription} tekens zijn." });
            await using var tx = await WriteLock.BeginAsync(db);
            if (!await db.Projects.AnyAsync(p => p.Id == req.ProjectId)) return Results.BadRequest(new { error = "Kies een project" });
            if (await db.GitHubCommits.AnyAsync(c => shas.Contains(c.Sha)))
                return Results.Conflict(new { error = "Deze commits zijn al geboekt of overgeslagen. Haal de voorstellen opnieuw op." });
            var entry = new TimeEntry { ProjectId = req.ProjectId, Date = req.Date.Date, Minutes = req.Minutes, Description = description, Billable = req.Billable, UserId = user.UserId() };
            db.TimeEntries.Add(entry);
            await db.SaveChangesAsync();
            foreach (var sha in shas)
                db.GitHubCommits.Add(new GitHubCommit { Sha = sha, ProjectId = req.ProjectId, TimeEntryId = entry.Id, UserId = user.UserId() });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                return Results.Conflict(new { error = "Deze commits zijn al geboekt of overgeslagen. Haal de voorstellen opnieuw op." });
            }
            await tx.CommitAsync();
            return Results.Created($"/api/time/{entry.Id}", entry);
        });

        // Overslaan: deze commits komen niet meer terug, zonder dat er uren geboekt worden.
        g.MapPost("/skip", async (AppDb db, ClaimsPrincipal user, GitHubSkipRequest req) =>
        {
            if (CleanShas(req.Shas) is not { } shas) return Results.BadRequest(new { error = "Er ging iets mis met de commits van dit voorstel. Haal de voorstellen opnieuw op." });
            await using var tx = await WriteLock.BeginAsync(db);
            if (!await db.Projects.AnyAsync(p => p.Id == req.ProjectId)) return Results.BadRequest(new { error = "Kies een project" });
            var known = await db.GitHubCommits.Where(c => shas.Contains(c.Sha)).Select(c => c.Sha).ToListAsync();
            foreach (var sha in shas.Except(known))
                db.GitHubCommits.Add(new GitHubCommit { Sha = sha, ProjectId = req.ProjectId, Skipped = true, UserId = user.UserId() });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { skipped = shas.Count - known.Count });
        });
    }
}
