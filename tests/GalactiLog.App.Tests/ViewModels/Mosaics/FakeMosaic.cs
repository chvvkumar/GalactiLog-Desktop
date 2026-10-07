using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// The in-memory mosaic behind MosaicDetailViewModelTests. It applies the rules the page relies on
// MosaicRepository and MosaicQueries for, in their simplest form, so a test reads what the page
// shows after a write rather than which delegate was called. Moved out of the test file in
// Phase 19A Task 3 (a Phase 18 deferred minor), when it gained the layout store and the frame set.
internal sealed class FakeMosaic
{
    internal sealed record Triple(Guid Target, string TargetName, DateOnly Date, string? Label, int Frames, double Seconds, string Filter);

    internal sealed class SessionRow(Guid panel, Guid target, DateOnly date, string? label, bool included)
    {
        public Guid Panel { get; } = panel;

        public Guid Target { get; } = target;

        public DateOnly Date { get; } = date;

        public string? Label { get; } = label;

        public bool Included { get; set; } = included;

        public bool Is(Guid target, DateOnly date, string? label)
            => Target == target && Date == date && string.Equals(Label ?? "", label ?? "", StringComparison.OrdinalIgnoreCase);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; } = "M 31 mosaic";

    public bool Deleted { get; set; }

    public List<string?> NotesWrites { get; } = [];

    public List<(Guid Id, string Label)> Panels { get; } = [];

    public List<SessionRow> Rows { get; } = [];

    public List<Triple> Catalogue { get; } = [];

    public List<AvailableLabel> AvailableLabels { get; } = [];

    /// <summary>The stored layout by panel id, as UpdateLayout writes it (spec 12.17's arranger).</summary>
    public Dictionary<Guid, (double? X, double? Y, int Rotation, bool FlipH)> Layouts { get; } = [];

    public double RotationAngle { get; set; }

    public int LayoutWrites { get; private set; }

    /// <summary>What PanelFrames answers.</summary>
    public PanelFrameSet Frames { get; set; } = new([], null, new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>());

    public Guid AddPanel(string label)
    {
        if (Panels.Any(panel => string.Equals(panel.Label, label, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicatePanelLabelException(label);
        }

        var id = Guid.NewGuid();
        Panels.Add((id, label));
        return id;
    }

    public void Row(Guid panel, Guid target, DateOnly date, string? label, bool included)
        => Rows.Add(new SessionRow(panel, target, date, label, included));

    private string PanelLabel(Guid id) => Panels.Single(panel => panel.Id == id).Label;

    private Triple Find(Guid target, DateOnly date, string? label)
        => Catalogue.Single(triple => triple.Target == target && triple.Date == date
            && string.Equals(triple.Label ?? "", label ?? "", StringComparison.OrdinalIgnoreCase));

    private bool IncludedAnywhere(Guid target, DateOnly date, string? label)
        => Rows.Any(row => row.Included && row.Is(target, date, label));

    private static PanelNight NightOf(Triple triple)
        => new(triple.Target, triple.TargetName, triple.Date, triple.Label,
            new Dictionary<string, int> { [triple.Filter] = triple.Frames }, triple.Frames, triple.Seconds);

    private List<Triple> AvailableOf(Guid panel)
    {
        var contributors = Rows.Where(row => row.Panel == panel).Select(row => row.Target).ToHashSet();
        return [.. Catalogue
            .Where(triple => contributors.Contains(triple.Target) && !IncludedAnywhere(triple.Target, triple.Date, triple.Label))
            .OrderByDescending(triple => triple.Date)];
    }

    private void Include(Guid panel, Guid target, DateOnly date, string? label)
    {
        if (Rows.FirstOrDefault(row => row.Included && row.Panel != panel && row.Is(target, date, label)) is { } other)
        {
            throw new NightAlreadyInMosaicException(date, Find(target, date, label).TargetName, label, PanelLabel(other.Panel));
        }

        if (Rows.FirstOrDefault(row => row.Panel == panel && row.Is(target, date, label)) is { } existing)
        {
            existing.Included = true;
        }
        else
        {
            Row(panel, target, date, label, included: true);
        }
    }

    public MosaicDetail? Read()
    {
        if (Deleted)
        {
            return null;
        }

        var panels = Panels.Select((panel, order) =>
        {
            var included = Rows.Where(row => row.Panel == panel.Id && row.Included)
                .Select(row => Find(row.Target, row.Date, row.Label))
                .OrderByDescending(triple => triple.Date)
                .ToList();
            var available = AvailableOf(panel.Id);
            var layout = Layouts.GetValueOrDefault(panel.Id);
            return new PanelDetail(
                panel.Id, panel.Label, order,
                layout.X, layout.Y, layout.Rotation, layout.FlipH,
                [.. included.Select(triple => triple.Target).Distinct()],
                [.. included.Select(triple => triple.TargetName).Distinct()],
                included.Sum(triple => triple.Seconds),
                included.Sum(triple => triple.Frames),
                included.Select(triple => (triple.Target, triple.Date)).Distinct().Count(),
                available.Count,
                0,
                [.. included.Select(NightOf)],
                [.. available.Select(NightOf)],
                included.GroupBy(triple => triple.Filter).ToDictionary(group => group.Key, group => group.Sum(triple => triple.Seconds)));
        }).ToList();

        var leader = panels.Count == 0 ? 0 : panels.Max(panel => panel.IntegrationSeconds);
        panels = [.. panels.Select(panel => panel with { DeficitSeconds = leader > 0 ? leader - panel.IntegrationSeconds : 0 })];
        var labels = AvailableLabels
            .Where(label => !Panels.Any(panel => string.Equals(panel.Label, label.Label, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new MosaicDetail(
            Id, Name, null, RotationAngle, panels.Sum(panel => panel.IntegrationSeconds), panels.Sum(panel => panel.Frames),
            null, null, [], panels, labels);
    }

    public MosaicsBackend Backend() => new()
    {
        Detail = _ => Read(),
        PanelFrames = _ => Frames,
        UpdateLayout = (_, rotation, panels) =>
        {
            LayoutWrites++;
            RotationAngle = rotation;
            foreach (var (panel, x, y, turn, flip) in panels)
            {
                Layouts[panel] = (x, y, turn, flip);
            }
        },
        Rename = (_, name) =>
        {
            if (string.Equals(name, "Andromeda", StringComparison.OrdinalIgnoreCase))
            {
                throw new DuplicateMosaicNameException(name);
            }

            Name = name;
        },
        Delete = _ => Deleted = true,
        SetNotes = (_, notes) => NotesWrites.Add(notes),
        IncludeNight = Include,
        RemoveNight = (panel, target, date, label) => Rows.Single(row => row.Panel == panel && row.Is(target, date, label)).Included = false,
        IncludeAll = panel =>
        {
            var available = AvailableOf(panel);
            available.ForEach(triple => Include(panel, triple.Target, triple.Date, triple.Label));
            return available.Count;
        },
        IncludeAllAvailable = _ =>
        {
            var count = 0;
            foreach (var (panel, _) in Panels)
            {
                foreach (var triple in AvailableOf(panel))
                {
                    Include(panel, triple.Target, triple.Date, triple.Label);
                    count++;
                }
            }

            return count;
        },
        IncludeAsNewPanel = (_, _, target, date, label, newLabel) =>
        {
            var panel = AddPanel(newLabel);
            Include(panel, target, date, label);
            return panel;
        },
        AddTargetNights = (panel, target) =>
        {
            var added = Catalogue.Where(triple => triple.Target == target && !IncludedAnywhere(target, triple.Date, triple.Label)).ToList();
            added.ForEach(triple => Row(panel, target, triple.Date, triple.Label, included: false));
            return added.Count;
        },
        DeletePanel = panel =>
        {
            if (Rows.Any(row => row.Panel == panel && row.Included))
            {
                throw new PanelNotEmptyException();
            }

            Rows.RemoveAll(row => row.Panel == panel);
            Panels.RemoveAll(entry => entry.Id == panel);
        },
        AddPanelWithTarget = (_, target, label) =>
        {
            var panel = AddPanel(label);
            var triples = Catalogue.Where(triple => triple.Target == target
                && string.Equals(triple.Label, label, StringComparison.OrdinalIgnoreCase)).ToList();
            triples.ForEach(triple => Include(panel, target, triple.Date, triple.Label));
            return new PanelAddResult(panel, triples.Count, 0);
        },
        SearchTargets = term =>
        [
            .. Catalogue
                .Where(triple => triple.TargetName.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(triple => (triple.Target, triple.TargetName))
                .Distinct()
                .Select(target => new TargetSearchResult(target.Target, null, target.TargetName, null, null, 1, 1)),
        ],
    };
}
