using GalactiLog.Data.Entities;

namespace GalactiLog.Data.Repositories;

// Thin CRUD over the single user_settings row (id 1). Short-lived DbContext per call, per
// design-spec 4.2's repository pattern. Creates the row with every column at its JSON
// default (UserSettingsRow's own property initializers, Task 3) on first read if it does not
// exist yet (first run, before the setup wizard, per design-spec 17.2).
public sealed class SettingsRepository
{
    private readonly string _connectionString;

    public SettingsRepository(string connectionString) => _connectionString = connectionString;

    public UserSettingsRow Load()
    {
        using var readContext = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString));
        var row = readContext.UserSettings.SingleOrDefault(r => r.Id == 1);
        if (row is not null)
        {
            return row;
        }

        row = new UserSettingsRow { Id = 1, UpdatedAt = DateTime.UtcNow };
        using var writeContext = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));
        writeContext.UserSettings.Add(row);
        writeContext.SaveChanges();
        return row;
    }

    public void Save(UserSettingsRow row)
    {
        row.UpdatedAt = DateTime.UtcNow;
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));
        context.UserSettings.Update(row);
        context.SaveChanges();
    }
}
