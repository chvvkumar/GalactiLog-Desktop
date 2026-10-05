using System.Reflection;
using GalactiLog.App.Tests.TestSupport;
using Xunit.Sdk;

[assembly: JoinChartThrottles]

namespace GalactiLog.App.Tests.TestSupport;

// The headless runner calls After inside the test's own dispatch, on the session thread and before
// the session tears the platform down, so every chart the test mounted is joined while its dispatcher lives.
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class JoinChartThrottlesAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest) => ChartThrottles.Mounted.Release();
}
