namespace Potel.Api.Invoicing;

// Grenzen voor wat de planner op de achtergrond met facturen doet. Aan te passen in appsettings.json onder "Invoicing".
public class InvoicingLimits
{
    // Vanaf hoe laat (Nederlandse tijd) de planner facturen klaarzet; 0 is meteen na middernacht.
    public int StartHour { get; set; } = 8;
    // Hoe vaak de planner per werkruimte kijkt of er iets te doen is, in minuten.
    public int CheckEveryMinutes { get; set; } = 5;
    // Per werkruimte per ronde hooguit zoveel facturen uit terugkerende facturen; de rest volgt de volgende ronde.
    public int MaxRecurringPerRun { get; set; } = 25;
    // Lag de server stil, dan haalt een terugkerende factuur hooguit zoveel gemiste perioden in. Oudere perioden slaan we over
    // (met een regel in het logboek), zodat een vergeten server niet ineens tientallen facturen klaarzet.
    public int MaxCatchUpPeriods { get; set; } = 12;
}

// Werk dat de planner per werkruimte op de achtergrond doet, naast de werkstromen. De planner zet de werkruimte in de scope
// en slaat werkruimtes over die alleen-lezen zijn; een taak controleert dat zelf ook nog.
public interface IWorkspaceJob
{
    Task RunAsync(CancellationToken ct);
}
