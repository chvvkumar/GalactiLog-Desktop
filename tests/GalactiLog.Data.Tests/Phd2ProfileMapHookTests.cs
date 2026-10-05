using System.Text.Json;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Task 5 brief section 8.6, spec 12.7's own sentence: "A save that changes the map's canonical
// form in no way, which is what re-saving an unchanged row does, queues nothing."
//
// The hook is NOT a profile map event. Spec 7.6's zone and site resolution order falls through
// from a profile's own value to the observer value, so observer_timezone, observer_latitude and
// observer_longitude are inputs of it exactly as the map is, and a user who sets the observer zone
// on the Location tab must get the same re-run as one who set a zone on a profile row.
//
// SettingsStore is the choke point every writer of the general document already passes through,
// which is why the hook lives there and not in the Settings panel (design-lessons rule 2): a
// future writer cannot forget to queue the re-run because it never had to remember.
public class Phd2ProfileMapHookTests
{
    private static (SettingsStore Store, TestDatabaseHandle Db) CreateStore()
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        return (new SettingsStore(new SettingsRepository(db.ConnectionString)), db);
    }

    private static JsonElement Map(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    // --- case 22: a real change raises exactly once -------------------------------------------

    [Fact]
    public void MutateGeneral_WithAMapWhoseCanonicalFormDiffers_RaisesOnce()
    {
        // A failure looks like: the user maps a profile to a rig, nothing queues the correlation,
        // and the mapping has no visible effect until the next full re-ingest.
        var (store, db) = CreateStore();
        using var _ = db;

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}"""),
        });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void MutateGeneral_ThatOnlyEditsAnEntrysTimezone_StillRaises()
    {
        // The map is more than its telescopes: a zone saved against a profile is what turns an
        // unzoned session into a correlatable one, so it is a change like any other.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}""") });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8","timezone":"UTC"}}"""),
        });

        Assert.Equal(1, raised);
    }

    // --- case 23: a canonically identical map raises nothing -----------------------------------

    [Fact]
    public void MutateGeneral_ReSavingACanonicallyIdenticalMap_RaisesNothing()
    {
        // A failure looks like: every settings save queues a correlation re-run over the whole
        // guide-log corpus, so the Equipment tab becomes unusable while a job runs on each
        // keystroke's autosave.
        //
        // The two forms below are the ones spec 5.8.1 says Normalize folds together: the legacy
        // whole-value string form, and the current object form whose telescope carries the
        // surrounding whitespace CoerceTelescope trims.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { Phd2ProfileMap = Map("""{"RigA":"Newt 8"}""") });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"  Newt 8  ","timezone":"","latitude":null,"longitude":null}}"""),
        });

        Assert.Equal(0, raised);
    }

    [Fact]
    public void MutateGeneral_ThatChangesAnotherGeneralKeyOnly_RaisesNothing()
    {
        // Coordinator override 3. The hook sits beside GeneralChanged, which fires on every
        // general save; without the canonical comparison it would fire with it, and every
        // unrelated settings write would queue a pass over the corpus.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { Phd2ProfileMap = Map("""{"RigA":"Newt 8"}""") });

        var mapRaised = 0;
        var generalRaised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => mapRaised++;
        store.GeneralChanged += (_, _) => generalRaised++;

        store.MutateGeneral(g => g with { DefaultPageSize = 25 });

        Assert.Equal(1, generalRaised);
        Assert.Equal(0, mapRaised);
    }

    [Fact]
    public void SaveGeneral_ReSavingTheStoredDocument_RaisesNothing()
    {
        // SaveGeneral is the second writer of the general document, so the comparison has to sit
        // on it too or the choke point has a hole in it.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { Phd2ProfileMap = Map("""{"RigA":"Newt 8"}""") });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.SaveGeneral(store.GetGeneral());

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SaveGeneral_WithADifferentMap_Raises()
    {
        var (store, db) = CreateStore();
        using var _ = db;

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.SaveGeneral(store.GetGeneral() with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}"""),
        });

        Assert.Equal(1, raised);
    }

    // --- the observer half of the resolution order --------------------------------------------

    [Fact]
    public void MutateGeneral_ThatChangesTheObserverTimezoneAndNotTheMap_RaisesOnce()
    {
        // A failure looks like: a user with no profile mapped at all sets the observer timezone,
        // which is step 2 of spec 7.6's resolution order and the only zone their sessions will
        // ever get, and nothing re-runs, so every session stays unzoned until the next re-ingest.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { Phd2ProfileMap = Map("""{"RigA":"Newt 8"}""") });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void MutateGeneral_ThatMovesTheObserverSiteToGreenwich_RaisesOnce()
    {
        // Longitude drives the imaging-night arithmetic a session is filed under, so it is an
        // input like any other. Zero is the value on purpose: a truth test on either coordinate
        // would read Greenwich and the equator as "not set" and raise nothing here.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { ObserverLatitude = 51.5, ObserverLongitude = 12.0 });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with { ObserverLongitude = 0 });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void MutateGeneral_ThatReSavesEveryInputUnchanged_RaisesNothing()
    {
        // The whole point of the comparison: the map, the zone and both coordinates all set, and
        // a save that touches none of them. A failure here queues a pass over the corpus on every
        // unrelated settings write.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8","timezone":"UTC"}}"""),
            ObserverTimezone = "UTC",
            ObserverLatitude = 51.5,
            ObserverLongitude = 0,
        });

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with { DefaultPageSize = 25 });

        Assert.Equal(0, raised);
    }

    // --- case 24: a refused write raises nothing ----------------------------------------------

    [Fact]
    public void MutateGeneral_ThatIsRefused_WritesNothingAndRaisesNothing()
    {
        // A failure looks like: a refused save tells a subscriber the map moved, and a job runs
        // over a map the store declined to store.
        var (store, db) = CreateStore();
        using var _ = db;

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        Assert.Throws<SettingsValidationException>(() => store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}"""),
            ObserverLatitude = 200,
        }));

        Assert.Equal(0, raised);
        Assert.Empty(Phd2Profiles.Normalize(store.GetGeneral().Phd2ProfileMap));
    }

    // --- case 25: raised outside the write gate -----------------------------------------------

    [Fact]
    public void MutateGeneral_RaisesTheHookOutsideTheWriteGate()
    {
        // A failure looks like: a subscriber that starts a job deadlocks against the next settings
        // read, which is the reason GeneralChanged is already raised outside the gate.
        //
        // The read is taken on ANOTHER thread on purpose. System.Threading.Lock is re-entrant, so
        // a subscriber reading the store on the raising thread would succeed either way and prove
        // nothing; a pool thread blocks on a gate the raising thread still holds.
        var (store, db) = CreateStore();
        using var _ = db;

        var readCompleted = false;
        store.Phd2GuidingInputsChanged += (_, _) =>
            readCompleted = Task.Run(() => store.GetGeneral()).Wait(TimeSpan.FromSeconds(10));

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}"""),
        });

        Assert.True(readCompleted);
    }

    // --- review P3-5: one re-run, not two ------------------------------------------------------

    [Fact]
    public void MutateGeneral_ThatMovesTwoGuidingInputsAtOnce_RaisesOnce()
    {
        // Spec 7.6 states it outright: "A save that changes two or more of them queues one re-run,
        // not two." It is structurally true today (one bool, one if, one Invoke) and that is
        // exactly why it is worth pinning: the day someone raises per moved input, a user who
        // corrects their site and their map in one Location save pays for two corpus-wide passes.
        var (store, db) = CreateStore();
        using var _ = db;

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        store.MutateGeneral(g => g with
        {
            Phd2ProfileMap = Map("""{"RigA":{"telescope":"Newt 8"}}"""),
            ObserverTimezone = "UTC",
            ObserverLatitude = 51.4779,
            ObserverLongitude = -0.0015,
        });

        Assert.Equal(1, raised);
    }

    // --- spec 7.6's durable obligation ---------------------------------------------------------

    [Fact]
    public void ASaveThatMovesAGuidingInput_SetsTheCorrelationPendingFlagInTheSameDocument()
    {
        // Spec 7.6, "The obligation survives a crash". The flag and the change it records have to
        // be ONE write: a process that dies between them leaves a moved guiding input with nothing
        // anywhere saying a re-run is owed, which is loss sequence 3 of the phase review (nights
        // the interrupted pass never reached keep values the saved map no longer produces).
        //
        // A failure looks like: the user re-maps a rig, quits before the pass finishes, and every
        // night after the one it reached carries the old rig's guiding numbers forever.
        var (store, db) = CreateStore();
        using var _ = db;

        Assert.False(store.GetGeneral().Phd2CorrelationPending);

        // Read inside the handler, which runs after the gate is released and therefore sees what
        // was actually stored, not what the caller passed.
        var pendingWhenRaised = false;
        store.Phd2GuidingInputsChanged += (_, _) =>
            pendingWhenRaised = store.GetGeneral().Phd2CorrelationPending;

        store.MutateGeneral(g => g with { ObserverLongitude = -0.0015 });

        Assert.True(pendingWhenRaised);
        Assert.True(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void SaveGeneral_ThatMovesAGuidingInput_SetsTheFlagToo()
    {
        // The second writer of the general document. A flag set by one writer and not the other is
        // an obligation that survives a Location tab save and not a setup wizard one.
        var (store, db) = CreateStore();
        using var _ = db;

        store.SaveGeneral(new GeneralSettings { ObserverTimezone = "UTC" });

        Assert.True(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void ClearingTheCorrelationPendingFlag_RaisesNothingAndQueuesNoSecondReRun()
    {
        // The completing pass clears the flag through this store, so if the flag were itself a
        // guiding input that write would queue another re-run, which would clear the flag, which
        // would queue another: one settings save would become a pass that never stops.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });
        var observed = store.GetGeneral();
        Assert.True(observed.Phd2CorrelationPending);

        var raised = 0;
        store.Phd2GuidingInputsChanged += (_, _) => raised++;

        Assert.True(store.ClearCorrelationPendingIfUnchanged(observed));

        Assert.Equal(0, raised);
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    // --- fix-wave review P2-3 and P1-1: the flag is monotonic, and one door clears it ------------

    [Fact]
    public void SaveGeneral_CarryingADocumentReadBeforeTheFlagWasSet_DoesNotDischargeTheReRun()
    {
        // Fix-wave review P2-3. `SaveGeneral` serializes the CALLER's document wholesale, and the
        // set-only helper never preserved what was stored, so a caller holding a document it read
        // before the flag was set wrote `phd2_correlation_pending` back to false and discharged an
        // owed re-run. Latent today only because `SaveGeneral` has no production caller.
        //
        // A failure looks like: the user re-maps a rig, some other surface saves a whole general
        // document it read a moment earlier, and the owed correlation is gone with no trace.
        var (store, db) = CreateStore();
        using var _ = db;

        // The stale document: read, then the obligation is recorded by a write it knows nothing
        // of. The obligation is armed WITHOUT moving a guiding input, because a stale document
        // that also reverts one would re-arm the flag through the ordinary set path and the case
        // would pass whatever the helper does.
        var stale = store.GetGeneral();
        store.MutateGeneral(g => g with { Phd2CorrelationPending = true });
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        store.SaveGeneral(stale with { ObserverName = "Someone" });

        Assert.True(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void NeitherWriter_CanClearTheFlag_OnlyTheCompareAndClearDoor()
    {
        // The structural half of P2-3: there is exactly one member in the solution that discharges
        // the obligation, so no future settings surface can drop one by writing a document that
        // happens to carry false. A failure looks like a new tab quietly undoing a re-map's
        // obligation on an unrelated save.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        store.MutateGeneral(g => g with { Phd2CorrelationPending = false });
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        store.SaveGeneral(store.GetGeneral() with { Phd2CorrelationPending = false });
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        Assert.True(store.ClearCorrelationPendingIfUnchanged(store.GetGeneral()));
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void TheDoor_RefusesToClearWhenAGuidingInputMovedSinceTheSnapshot()
    {
        // Fix-wave review P1-1, at the store. The comparison runs inside the write gate against
        // the document as stored at that instant, and it is the same GuidingInputsMoved the store
        // already uses to decide whether a save owes a re-run, so there is no second definition to
        // drift. A save therefore either landed before the pass's snapshot, in which case the pass
        // observed it, or after, in which case the flag stays true.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });
        var observed = store.GetGeneral();

        // The newer save the pass never saw.
        store.MutateGeneral(g => g with { ObserverLongitude = -0.0015 });

        Assert.False(store.ClearCorrelationPendingIfUnchanged(observed));
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        // And the pass that DOES see it discharges it.
        Assert.True(store.ClearCorrelationPendingIfUnchanged(store.GetGeneral()));
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void TheDoor_ClearsWhenNothingMovedAndDoesNothingWhenNothingIsOwed()
    {
        // The ordinary case, and the guard on the other side: a pass with no intervening save
        // still discharges the obligation, and a pass with nothing owed writes nothing.
        var (store, db) = CreateStore();
        using var _ = db;

        Assert.False(store.ClearCorrelationPendingIfUnchanged(store.GetGeneral()));

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });
        Assert.True(store.ClearCorrelationPendingIfUnchanged(store.GetGeneral()));
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    [Fact]
    public void ASaveThatMovesNothing_LeavesAnOwedReRunOwed()
    {
        // An unrelated save is not a discharge. A failure looks like the user changing their theme
        // between the re-map and the restart, and the owed pass quietly disappearing.
        var (store, db) = CreateStore();
        using var _ = db;

        store.MutateGeneral(g => g with { ObserverTimezone = "UTC" });

        store.MutateGeneral(g => g with { Theme = "luminance" });

        Assert.True(store.GetGeneral().Phd2CorrelationPending);
    }
}
