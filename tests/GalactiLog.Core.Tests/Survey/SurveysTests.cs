using GalactiLog.Core.Survey;
using Xunit;

namespace GalactiLog.Core.Tests.Survey;

// Unit A case 8. A failure is a stored typo or a wrong-case id reaching the request.
public class SurveysTests
{
    [Fact]
    public void Resolve_KeepsEachOfTheFiveAndDefaultsEverythingElse()
    {
        Assert.Equal(5, Surveys.All.Count);
        foreach (var survey in Surveys.All)
        {
            Assert.Same(survey, Surveys.Resolve(survey.Id));
        }

        foreach (var stored in new[] { null, "", "p/dss2/color", "p/dss2/red", "P/unknown" })
        {
            Assert.Equal(Surveys.DefaultId, Surveys.Resolve(stored).Id);
        }
    }
}
