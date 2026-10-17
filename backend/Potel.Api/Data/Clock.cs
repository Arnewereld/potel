namespace Potel.Api.Data;

// "Vandaag" en "nu" zoals een Nederlandse gebruiker ze ziet, los van de tijdzone van de server (in Docker is dat UTC).
// Factuurdatums, vervaldatums, de week en het jaar op het dashboard en verlopen facturen gaan hiervan uit.
public class BusinessClock
{
    public static TimeZoneInfo Zone { get; } = FindZone();

    public virtual DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Zone);
    public DateTime Today => Now.Date;

    // Linux en macOS kennen "Europe/Amsterdam", Windows "W. Europe Standard Time". Ontbreekt de tijdzonedatabase
    // (een kaal container-image), dan bouwen we de Nederlandse regel zelf: UTC+1, zomertijd van eind maart tot eind oktober.
    static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Europe/Amsterdam", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        var summer = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Europe/Amsterdam", TimeSpan.FromHours(1), "Amsterdam", "CET", "CEST", [summer]);
    }
}
